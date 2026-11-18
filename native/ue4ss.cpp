// UE4SS C++ mod entry points for KdLocalNative.
//
// UE4SS loads Mods\<Name>\dlls\main.dll with LoadLibraryExW and then resolves
// start_mod / uninstall_mod. If either export is missing UE4SS unloads the
// module again, which would tear down hooks that are already installed.
//
// The real work starts on its own thread from DllMain in compat.cpp, so these
// entry points only have to exist. Returning nullptr keeps UE4SS from calling
// into a CppUserModBase vtable this module does not implement.
#include <windows.h>

extern "C" void KdAutoStartRestore();

extern "C" __declspec(dllexport) void* start_mod() { return nullptr; }
extern "C" __declspec(dllexport) void uninstall_mod(void*) { KdAutoStartRestore(); }
