using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace KartDriftLocal
{
    // Entry points exported by the NativeAOT shared library. A UE4SS C++ mod (or
    // any native host) can LoadLibrary this DLL and start the local gateway in
    // the game process, which removes the need for the external launcher.
    public static class NativeExports
    {
        static Gateway gateway;
        static ProfileStore store;
        static LocalRoute route;

        // <mod>\settings.json, created on first run. gameLanguage picks the
        // CultureIndex written into the game's GameUserSettings.ini.
        static Dictionary<string, object> ReadSettings(string root)
        {
            string path = Path.Combine(root, "settings.json");
            if (File.Exists(path)) try { return J.D(J.Read(File.ReadAllText(path))); } catch (Exception) { }
            var defaults = J.M("gameLanguage", "zh-Hans", "language", "zh");
            try { File.WriteAllText(path, J.Write(defaults), new System.Text.UTF8Encoding(false)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return defaults;
        }
        static string DefaultData()
        { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KartDriftLocal"); }

        static void Log(string directory, string text)
        {
            try
            {
                Directory.CreateDirectory(Path.Combine(directory, "logs"));
                File.AppendAllText(Path.Combine(directory, "logs", "gateway.log"), J.Now() + " " + text + Environment.NewLine, new System.Text.UTF8Encoding(false));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        [UnmanagedCallersOnly(EntryPoint = "KdLocalStart")]
        public static int Start(IntPtr dataDirectory, int port)
        {
            try
            {
                if (gateway != null) return gateway.Port;
                string data = dataDirectory == IntPtr.Zero ? null : Marshal.PtrToStringUni(dataDirectory);
                if (String.IsNullOrEmpty(data)) data = AppContext.BaseDirectory;
                data = Path.GetFullPath(data);
                string save = Path.Combine(data, "local-data", "player.sqlite3");
                Directory.CreateDirectory(Path.GetDirectoryName(save));
                var templates = Bundle.Templates();
                store = new ProfileStore(save, templates);
                store.Performance = new Performance(J.S(J.D(J.Read(J.S(templates["PqLogin"])))["accountId"]));
                // The gateway shares this process with the game, so keep the parsed
                // profile in memory and persist it from a background thread.
                store.EnableDeferredWrites();
                // The launcher used to set the culture index before starting the
                // game; the mod has to do it, otherwise the client keeps whatever
                // language it saved last (Korean by default).
                string culture = J.S(J.Get(ReadSettings(data), "gameLanguage", "zh-Hans"));
                try
                {
                    GameLanguage.Configure(Path.Combine(Path.GetDirectoryName(Launcher.Config), "GameUserSettings.ini"), culture);
                    Log(data, "game language set to " + culture);
                }
                catch (Exception error) { Log(data, "language failed: " + error.Message); }
                try { store.SetGameLanguage(culture); } catch (Exception error) { Log(data, "profile language failed: " + error.Message); }
                gateway = new Gateway(store, port <= 0 ? 59874 : port, message => Log(data, message));
                Log(data, "gateway listening on " + gateway.Port + "; ping " + gateway.PingPort);
                return gateway.Port;
            }
            catch (Exception error)
            {
                Log(AppContext.BaseDirectory, "gateway start failed: " + error.GetType().Name + ": " + error.Message);
                return -1;
            }
        }
        [UnmanagedCallersOnly(EntryPoint = "KdLocalLoginComplete")]
        public static int LoginComplete() { return gateway != null && gateway.LoginComplete ? 1 : 0; }
        // Tells the gateway where this mod's own Localization/TrialTower folders
        // are, so the package works from any directory.
        [UnmanagedCallersOnly(EntryPoint = "KdLocalSetRoot")]
        public static int SetRoot(IntPtr path)
        {
            try
            {
                string root = path == IntPtr.Zero ? null : Marshal.PtrToStringUni(path);
                if (String.IsNullOrEmpty(root) || !Directory.Exists(root)) return 0;
                Launcher.DataRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
                Log(DefaultData(), "data root " + Launcher.DataRoot);
                return 1;
            }
            catch (Exception error) { Log(DefaultData(), "data root failed: " + error.Message); return 0; }
        }
        // Installs the ws://127.0.0.1 route into the game's Game.ini and keeps a
        // journal so the original bytes can be restored later, even after a crash.
        [UnmanagedCallersOnly(EntryPoint = "KdLocalRouteInstall")]
        public static int RouteInstall(IntPtr gameRoot, IntPtr dataDirectory, IntPtr localizationDirectory)
        {
            try
            {
                if (route != null) return 1;
                // In mod mode there is no launcher to hand over the game path, so
                // derive it from the process image directory instead.
                string game = gameRoot == IntPtr.Zero ? AppContext.BaseDirectory : Marshal.PtrToStringUni(gameRoot);
                string data = dataDirectory == IntPtr.Zero ? null : Marshal.PtrToStringUni(dataDirectory);
                if (String.IsNullOrEmpty(data)) data = DefaultData();
                Directory.CreateDirectory(data);
                string native = Path.GetFullPath(Path.Combine(game, @"..\..\Content\Localization\Game"));
                string localization = localizationDirectory == IntPtr.Zero ? null : Marshal.PtrToStringUni(localizationDirectory);
                route = new LocalRoute(Launcher.Config, data, native, String.IsNullOrEmpty(localization) ? null : localization);
                Log(data, "route installed into " + Launcher.Config);
                return 1;
            }
            catch (Exception error)
            {
                Log(DefaultData(), "route install failed: " + error.GetType().Name + ": " + error.Message);
                return 0;
            }
        }
        // Restores the original Game.ini bytes and deletes the journal.
        [UnmanagedCallersOnly(EntryPoint = "KdLocalRouteRestore")]
        public static int RouteRestore()
        {
            try { if (route == null) return 0; route.Dispose(); route = null; Log(DefaultData(), "route restored"); return 1; }
            catch (Exception error) { Log(DefaultData(), "route restore failed: " + error.Message); return 0; }
        }
        [UnmanagedCallersOnly(EntryPoint = "KdLocalStop")]
        public static void Stop()
        {
            try { if (gateway != null) { gateway.Dispose(); gateway = null; } }
            catch (Exception) { }
            // Flush the deferred profile before the process goes away.
            try { if (store != null) { store.StopDeferredWrites(); store = null; } }
            catch (Exception error) { Log(DefaultData(), "final flush failed: " + error.Message); }
        }
    }
}
