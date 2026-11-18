// Fully automatic UE4SS mode for KdLocalNative.
//
// When UE4SS loads this module the launcher is not involved at all, so the
// module does the launcher's job from inside the game process:
//
//   1. start the local gateway (KartDriftLocal.Core.dll, NativeAOT shared lib)
//   2. install the ws://127.0.0.1 route into Game.ini, keeping a journal
//   3. restore the original Game.ini bytes and stop the gateway on exit
//
// The Nexon arena command line the launcher used to pass is deliberately not
// reproduced: nothing in this project reads those arguments, and the native
// module already replaces the SDK initialization and ticket calls. If the game
// turns out to need one of them, add it back with a test that proves it.
//
// The launcher path is detected through KD_LOCAL_NATIVE_CHANNEL and skips all
// of this, so the same DLL still works when injected.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <winternl.h>
#include <intrin.h>
#include <cstdio>
#include <string>
#include "compat.h"
#include "vendor/minhook/include/MinHook.h"

namespace {

typedef int (*KdStartFn)(const wchar_t*, int);
typedef int (*KdRouteInstallFn)(const wchar_t*, const wchar_t*, const wchar_t*);
typedef int (*KdRouteRestoreFn)();
typedef void (*KdStopFn)();
typedef int (*KdSetRootFn)(const wchar_t*);

HMODULE coreModule{};
KdStartFn kdStart{};
KdRouteInstallFn kdRouteInstall{};
KdRouteRestoreFn kdRouteRestore{};
KdStopFn kdStop{};
KdSetRootFn kdSetRoot{};
volatile LONG restored{};
std::wstring dataDirectory;
std::wstring moduleDirectory;
HANDLE statusMapping{};
NativeStatus* statusView{};

using ExitProcessFn = void (WINAPI*)(UINT);
ExitProcessFn originalExitProcess{};
// Unreal's RequestExit calls TerminateProcess on itself, not ExitProcess, so
// hooking only ExitProcess leaves the Game.ini route behind on a normal quit.
using TerminateProcessFn = BOOL (WINAPI*)(HANDLE, UINT);
TerminateProcessFn originalTerminateProcess{};
using GetCommandLineWFn = LPWSTR (WINAPI*)();
using GetCommandLineAFn = LPSTR (WINAPI*)();
GetCommandLineWFn originalGetCommandLineW{};
GetCommandLineAFn originalGetCommandLineA{};
std::wstring g_commandLine;
std::string g_commandLineAnsi;

std::wstring DirectoryOf(const std::wstring& path);

// Saves and logs live inside the mod folder so the package stays portable.
// Falls back to %LOCALAPPDATA% only when the mod directory is not writable.
std::wstring LocalData()
{
    std::wstring root = DirectoryOf(moduleDirectory);
    if (!root.empty())
    {
        std::wstring probe = root + L"\\local-data";
        if (CreateDirectoryW(probe.c_str(), nullptr) || GetLastError() == ERROR_ALREADY_EXISTS) return root;
    }
    wchar_t buffer[MAX_PATH]{};
    DWORD size = GetEnvironmentVariableW(L"LOCALAPPDATA", buffer, MAX_PATH);
    if (size == 0 || size >= MAX_PATH) return root;
    return std::wstring(buffer) + L"\\KartDriftLocal";
}

void Log(const wchar_t* text)
{
    CreateDirectoryW(dataDirectory.c_str(), nullptr);
    std::wstring directory = dataDirectory + L"\\logs";
    CreateDirectoryW(directory.c_str(), nullptr);
    std::wstring path = directory + L"\\mod.log";
    HANDLE file = CreateFileW(path.c_str(), FILE_APPEND_DATA, FILE_SHARE_READ, nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return;
    SYSTEMTIME now; GetLocalTime(&now);
    wchar_t line[1024];
    int count = _snwprintf_s(line, _countof(line), _TRUNCATE, L"%04d-%02d-%02d %02d:%02d:%02d.%03d %s\r\n",
        now.wYear, now.wMonth, now.wDay, now.wHour, now.wMinute, now.wSecond, now.wMilliseconds, text);
    if (count > 0)
    {
        DWORD written = 0;
        WriteFile(file, line, (DWORD)(count * sizeof(wchar_t)), &written, nullptr);
    }
    CloseHandle(file);
}

std::wstring ModulePath(HMODULE module)
{
    wchar_t path[MAX_PATH]{};
    DWORD size = GetModuleFileNameW(module, path, MAX_PATH);
    return size ? std::wstring(path, size) : std::wstring();
}

std::wstring DirectoryOf(const std::wstring& path)
{
    size_t slash = path.find_last_of(L"\\/");
    return slash == std::wstring::npos ? path : path.substr(0, slash);
}

bool Exists(const std::wstring& path)
{
    return GetFileAttributesW(path.c_str()) != INVALID_FILE_ATTRIBUTES;
}

// compat.cpp's worker refuses to start without the shared status block that the
// launcher normally creates. In mod mode nobody else does it, so the module
// publishes its own and points the worker at it through the environment.
bool CreateStatusChannel()
{
    wchar_t name[128];
    _snwprintf_s(name, _countof(name), _TRUNCATE, L"Local\\KartDriftLocal-%lu", GetCurrentProcessId());
    statusMapping = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, 4096, name);
    if (!statusMapping) { Log(L"status channel creation failed"); return false; }
    statusView = static_cast<NativeStatus*>(MapViewOfFile(statusMapping, FILE_MAP_ALL_ACCESS, 0, 0, 4096));
    if (!statusView) { Log(L"status channel mapping failed"); return false; }
    statusView->magic = kMagic;
    statusView->version = 1;
    statusView->ownerPid = GetCurrentProcessId();
    statusView->command = 1;  // no launcher handshake, enable the notice hook up front
    SetEnvironmentVariableW(L"KD_LOCAL_NATIVE_CHANNEL", name);
    Log(L"status channel created");
    return true;
}

// Logs the native layer's readiness flags so a failed launch can be diagnosed
// from mod.log alone. 63 means SDK, crypto, languages, presets, notice, analytics.
void Monitor()
{
    if (!statusView) return;
    LONG lastFlags = -1;
    for (int i = 0; i < 360; ++i)
    {
        LONG error = statusView->error, flags = statusView->flags;
        wchar_t line[96];
        if (error) { _snwprintf_s(line, _countof(line), _TRUNCATE, L"native error %ld", error); Log(line); return; }
        if (flags != lastFlags) { _snwprintf_s(line, _countof(line), _TRUNCATE, L"native flags %ld", flags); Log(line); lastFlags = flags; }
        Sleep(500);
    }
}

// A plain double click supplies none of the Nexon arena arguments the launcher
// passes. Without them the game never reaches the login stage: the mod's flags
// stall at 23 and the gateway sees no connection at all. Rewrite the PEB and
// hook both GetCommandLine variants, because kernel32 caches its own copy.
void PatchCommandLine()
{
    wchar_t exe[MAX_PATH]{};
    if (!GetModuleFileNameW(nullptr, exe, MAX_PATH)) return;
    g_commandLine = std::wstring(L"\"") + exe +
        L"\" -nexonarena -serviceid:1050768515 -nexonsn:1 -sid:00000000-0000-0000-0000-000000000001"
        L" -Server:kd-live -httpproxy=\"\"";
    auto peb = reinterpret_cast<PEB*>(__readgsqword(0x60));
    if (peb && peb->ProcessParameters)
    {
        SIZE_T bytes = (g_commandLine.size() + 1) * sizeof(wchar_t);
        auto buffer = static_cast<wchar_t*>(HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, bytes));
        if (buffer)
        {
            memcpy(buffer, g_commandLine.c_str(), bytes);
            peb->ProcessParameters->CommandLine.Buffer = buffer;
            peb->ProcessParameters->CommandLine.Length = (USHORT)(g_commandLine.size() * sizeof(wchar_t));
            peb->ProcessParameters->CommandLine.MaximumLength = (USHORT)bytes;
        }
    }
    Log(L"command line prepared");
}

LPWSTR WINAPI GetCommandLineWDetour() { return const_cast<LPWSTR>(g_commandLine.c_str()); }
LPSTR WINAPI GetCommandLineADetour()
{
    if (g_commandLineAnsi.empty() && !g_commandLine.empty())
    {
        int size = WideCharToMultiByte(CP_ACP, 0, g_commandLine.c_str(), (int)g_commandLine.size(), nullptr, 0, nullptr, nullptr);
        g_commandLineAnsi.resize(size);
        WideCharToMultiByte(CP_ACP, 0, g_commandLine.c_str(), (int)g_commandLine.size(), &g_commandLineAnsi[0], size, nullptr, nullptr);
    }
    return const_cast<LPSTR>(g_commandLineAnsi.c_str());
}

// The launcher strips proxy variables before starting the game; without this the
// game's WebSocket client can try to reach the local gateway through a proxy.
void ClearProxyEnvironment()
{
    for (const wchar_t* name : { L"http_proxy", L"https_proxy", L"all_proxy", L"no_proxy",
                                 L"HTTP_PROXY", L"HTTPS_PROXY", L"ALL_PROXY", L"NO_PROXY" })
        SetEnvironmentVariableW(name, nullptr);
}

// Runs at most once. Restores Game.ini byte for byte and stops the gateway so
// nothing is left behind in the game directory.
void Restore()
{
    if (InterlockedCompareExchange(&restored, 1, 0) != 0) return;
    if (kdRouteRestore) { int ok = kdRouteRestore(); Log(ok ? L"route restored" : L"route restore returned 0"); }
    if (kdStop) { kdStop(); Log(L"gateway stopped"); }
}

void WINAPI ExitDetour(UINT code)
{
    Restore();
    if (originalExitProcess) originalExitProcess(code);
}

BOOL WINAPI TerminateDetour(HANDLE process, UINT code)
{
    if (process == GetCurrentProcess()) Restore();
    return originalTerminateProcess ? originalTerminateProcess(process, code) : FALSE;
}

bool LoadCore()
{
    std::wstring core = moduleDirectory + L"\\KartDriftLocal.Core.dll";
    if (!Exists(core)) { Log(L"KartDriftLocal.Core.dll is missing next to main.dll"); return false; }
    coreModule = LoadLibraryW(core.c_str());
    if (!coreModule) { Log(L"LoadLibrary(KartDriftLocal.Core.dll) failed"); return false; }
    kdStart = reinterpret_cast<KdStartFn>(GetProcAddress(coreModule, "KdLocalStart"));
    kdRouteInstall = reinterpret_cast<KdRouteInstallFn>(GetProcAddress(coreModule, "KdLocalRouteInstall"));
    kdRouteRestore = reinterpret_cast<KdRouteRestoreFn>(GetProcAddress(coreModule, "KdLocalRouteRestore"));
    kdStop = reinterpret_cast<KdStopFn>(GetProcAddress(coreModule, "KdLocalStop"));
    kdSetRoot = reinterpret_cast<KdSetRootFn>(GetProcAddress(coreModule, "KdLocalSetRoot"));
    if (!kdStart || !kdRouteInstall || !kdRouteRestore) { Log(L"core exports are missing"); return false; }
    return true;
}

DWORD WINAPI AutoStartWorker(void*)
{
    if (!LoadCore()) return 0;
    // Keep every resource inside the mod folder so the package can be copied to
    // any UE4SS Mods directory and removed without leaving anything behind.
    std::wstring root = DirectoryOf(moduleDirectory);
    if (kdSetRoot && !root.empty()) kdSetRoot(root.c_str());
    // Game.ini is read during engine init, which happens well after this thread
    // starts; installing the route first keeps the window wide.
    std::wstring localization = root + L"\\Localization\\Game";
    if (!Exists(localization)) localization.clear();
    if (kdRouteInstall)
    {
        int ok = kdRouteInstall(nullptr, dataDirectory.c_str(), localization.empty() ? nullptr : localization.c_str());
        Log(ok ? L"route installed" : L"route install failed");
    }
    if (kdStart)
    {
        int port = kdStart(dataDirectory.c_str(), 59874);
        wchar_t line[128];
        _snwprintf_s(line, _countof(line), _TRUNCATE, L"gateway port %d", port);
        Log(line);
    }
    // compat.cpp also initializes MinHook from its own worker thread; the named
    // mutex keeps the two initializations from racing.
    HANDLE lock = CreateMutexW(nullptr, FALSE, L"Local\\KdLocalMinHookInit");
    if (lock) WaitForSingleObject(lock, 15000);
    MH_STATUS hookStatus = MH_Initialize();
    if (hookStatus == MH_OK || hookStatus == MH_ERROR_ALREADY_INITIALIZED)
    {
        HMODULE kernel = GetModuleHandleW(L"kernel32.dll");
        MH_CreateHook(reinterpret_cast<LPVOID>(GetProcAddress(kernel, "ExitProcess")),
            reinterpret_cast<LPVOID>(ExitDetour), reinterpret_cast<LPVOID*>(&originalExitProcess));
        MH_CreateHook(reinterpret_cast<LPVOID>(GetProcAddress(kernel, "TerminateProcess")),
            reinterpret_cast<LPVOID>(TerminateDetour), reinterpret_cast<LPVOID*>(&originalTerminateProcess));
        MH_CreateHook(reinterpret_cast<LPVOID>(GetProcAddress(kernel, "GetCommandLineW")),
            reinterpret_cast<LPVOID>(GetCommandLineWDetour), reinterpret_cast<LPVOID*>(&originalGetCommandLineW));
        MH_CreateHook(reinterpret_cast<LPVOID>(GetProcAddress(kernel, "GetCommandLineA")),
            reinterpret_cast<LPVOID>(GetCommandLineADetour), reinterpret_cast<LPVOID*>(&originalGetCommandLineA));
        MH_EnableHook(MH_ALL_HOOKS);
        Log(L"hooks installed (ExitProcess, TerminateProcess, GetCommandLineW/A)");
    }
    else Log(L"MinHook initialization failed");
    if (lock) { ReleaseMutex(lock); CloseHandle(lock); }
    Monitor();
    return 0;
}

}  // namespace

// Called from DllMain. Everything runs on a worker thread because it loads DLLs
// and touches files.
extern "C" void KdAutoStartModule(HMODULE module)
{
    wchar_t channel[8]{};
    if (GetEnvironmentVariableW(L"KD_LOCAL_NATIVE_CHANNEL", channel, 8)) return;  // launcher owns this launch
    moduleDirectory = DirectoryOf(ModulePath(module));
    dataDirectory = LocalData();
    PatchCommandLine();
    ClearProxyEnvironment();
    CreateStatusChannel();
    HANDLE worker = CreateThread(nullptr, 0, AutoStartWorker, nullptr, 0, nullptr);
    if (worker) CloseHandle(worker);
}

extern "C" void KdAutoStartRestore() { Restore(); }
