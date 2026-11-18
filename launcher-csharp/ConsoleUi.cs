using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace KartDriftLocal
{
    // Console replacement for the former WinForms front end. When settings.json
    // already holds a verified game directory the launcher starts immediately,
    // so double-clicking the exe never depends on interactive input. `--select`
    // brings the prompt back.
    static partial class Launcher
    {
        static readonly Dictionary<string, string> EnglishMessages = new Dictionary<string, string> {
            {"正在核对客户端和运行库…", "Checking the game client and runtime…"},
            {"正在本地登录…", "Signing in locally…"},
            {"本地游戏运行中，退出游戏后再关闭启动器。", "Game running. Exit the game before closing this launcher."},
            {"游戏已退出，存档保留。", "Game closed. Your save data has been kept."},
            {"本地登录验证超时。", "Local sign-in timed out."},
            {"游戏尚未完成本地登录和兼容检查就退出，请查看 logs/launcher.log。", "The game closed before local sign-in and compatibility checks completed. See logs/launcher.log."},
            {"游戏或第三方启动器正在运行，请先正常退出。", "The game or another launcher is running. Please close it first."},
            {"没有找到游戏，请选择包含 KartDrift 文件夹的游戏目录。", "Game not found. Select the directory containing the KartDrift folder."},
            {"客户端版本不匹配，未启动游戏或修改配置。", "Client version mismatch. The game was not started and settings were not changed."},
            {"游戏内的 nexon_api_x64.dll 缺失或版本不匹配，请使用同版本完整客户端；无需安装 Nexon 启动器。", "The game's nexon_api_x64.dll is missing or incompatible. Use the complete matching client; Nexon Launcher is not required."},
            {"原生兼容模块缺失或校验失败，请重新解压完整目录。", "The native compatibility module is missing or failed verification. Extract the complete package again."},
            {"旧启动器仍有待恢复的连接配置，请先运行旧版完成恢复。", "The previous launcher has pending connection settings. Run it to finish recovery first."},
            {"游戏连接配置为只读，请先恢复可写权限。", "Game connection settings are read-only. Restore write access first."},
            {"配置恢复路径不一致。", "The settings recovery path does not match."},
            {"连接配置被其他程序修改，已保留恢复备份，请处理冲突后重试。", "Another program changed the connection settings. The recovery backup has been kept; resolve the conflict and retry."}
        };
        internal static string UiText(string message, bool english)
        { string translated; return english && EnglishMessages.TryGetValue(message, out translated) ? translated : message; }

        // The launcher has no log before Run() starts, so a failure while
        // resolving the game directory used to leave no trace at all.
        internal static void Note(string text)
        {
            try
            {
                string directory = System.IO.Path.Combine(Root, "logs");
                Directory.CreateDirectory(directory);
                File.AppendAllText(System.IO.Path.Combine(directory, "launcher.log"), J.Now() + " " + text + Environment.NewLine, Encoding.UTF8);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        static string Ask(string prompt, string fallback)
        {
            Console.Write(prompt + (String.IsNullOrEmpty(fallback) ? ": " : " [" + fallback + "]: "));
            string line = Console.ReadLine();
            if (line == null) return null;
            line = line.Trim();
            if (line.Length >= 2 && line[0] == '"' && line[line.Length - 1] == '"') line = line.Substring(1, line.Length - 2).Trim();
            return line.Length == 0 ? fallback : line;
        }
        static bool CanStart(string game)
        { try { CheckGame(game); return true; } catch (Exception) { return false; } }
        static void Pause(bool english)
        {
            Console.WriteLine();
            Console.WriteLine(english ? "Press Enter to close." : "按回车键退出。");
            try { Console.ReadLine(); } catch (IOException) { }
        }
        static void Start(string game, int culture, bool english)
        {
            Console.WriteLine((english ? "Game directory: " : "游戏目录: ") + game);
            Console.WriteLine();
            Action<string> status = message => Console.WriteLine("  " + UiText(message, english));
            try { Run(game, Root, false, status, GameLanguage.Codes[culture]); }
            catch (Exception error)
            {
                Note("run failed: " + error.GetType().Name + ": " + error.Message);
                Console.WriteLine();
                Console.WriteLine("  " + UiText(error.Message, english));
                Console.Error.WriteLine(error);
            }
            Pause(english);
        }
        public static void Ui(bool select = false)
        {
            string settingsPath = System.IO.Path.Combine(Root, "settings.json"); var settings = J.M();
            if (File.Exists(settingsPath)) try { settings = J.D(J.Read(File.ReadAllText(settingsPath))); } catch (Exception) { }
            bool english = J.Same(J.Get(settings, "language"), "en");
            string game = J.S(J.Get(settings, "game", ""));
            int culture = Math.Max(0, Array.FindIndex(GameLanguage.Codes, v => v.Equals(J.S(J.Get(settings, "gameLanguage", "zh-Hans")), StringComparison.OrdinalIgnoreCase)));

            Console.WriteLine(english ? "KartDrift Offline Launcher" : "KartDrift 离线启动器");
            Console.WriteLine(english ? "Save data: local-data beside the launcher." : "存档在启动器同级 local-data 目录");
            Note("ui start; select=" + select + "; game=" + game);

            if (!select && CanStart(game)) { Start(game, culture, english); return; }

            Console.WriteLine(english ? "Enter the folder that CONTAINS the KartDrift folder, not Binaries\\Win64." : "填包含 KartDrift 文件夹的那一级目录，不要填 Binaries\\Win64。");
            Console.WriteLine();

            for (int attempt = 0; attempt < 3; attempt++)
            {
                string selected = Ask(english ? "Game directory (the folder containing KartDrift)" : "游戏目录（包含 KartDrift 文件夹）", game);
                if (selected == null) { Note("no input on game directory"); Console.WriteLine(english ? "No input available. Run this from a console window." : "没有可读输入，请在控制台窗口中运行。"); return; }
                try { CheckGame(selected); game = selected; break; }
                catch (Exception error) { Note("game directory rejected: " + selected + " -> " + error.Message); Console.WriteLine("  " + UiText(error.Message, english)); game = selected; if (attempt == 2) { Note("giving up after 3 attempts"); return; } }
            }

            Console.WriteLine(english ? "Game language:" : "游戏语言:");
            for (int i = 0; i < GameLanguage.Names.Length; i++) Console.WriteLine("  " + (i + 1) + ") " + GameLanguage.Names[i]);
            string answer = Ask(english ? "Choose a number" : "选择编号", (culture + 1).ToString());
            if (answer == null) { Note("no input on language"); Console.WriteLine(english ? "No input available. Run this from a console window." : "没有可读输入，请在控制台窗口中运行。"); return; }
            int picked; if (int.TryParse(answer, out picked) && picked >= 1 && picked <= GameLanguage.Codes.Length) culture = picked - 1;

            settings["game"] = game; settings["gameLanguage"] = GameLanguage.Codes[culture]; settings["language"] = english ? "en" : "zh";
            try { AtomicJson(settingsPath, settings); Note("settings saved; game=" + game + "; language=" + GameLanguage.Codes[culture]); }
            catch (Exception error) { Note("settings write failed: " + error.Message); Console.WriteLine("  " + error.Message); }

            Console.WriteLine();
            Start(game, culture, english);
        }
    }
}
