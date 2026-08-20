using System;
using System.IO;
using System.Text.Json;

namespace TraceSoul2.Host
{
    /// <summary>
    /// 分支切换标记：主数据目录下的 debug-mode.json。
    /// - 主线运行：<主目录>/debug-mode.json 存在 ⇒ 曾切到调试（或正准备切回清理）
    /// - 调试运行：DataDirectory = <主目录>/debug-active，标记由主目录承载
    /// </summary>
    public sealed class DebugModeState
    {
        public string mainDir { get; set; }
        public string debugDir { get; set; }
    }

    public static class DebugMode
    {
        private const string ActiveDirName = "debug-active";
        private const string MarkerFileName = "debug-mode.json";

        public static string ActiveDir(string mainDir)
        {
            return Path.Combine(mainDir, ActiveDirName);
        }

        /// <summary>从正在运行的宿主视角读取模式：调试中返回状态，主线返回 null。</summary>
        public static DebugModeState Read(string hostDataDir)
        {
            hostDataDir = Path.GetFullPath(hostDataDir);
            var markerPath = Path.Combine(hostDataDir, MarkerFileName);
            if (File.Exists(markerPath))
            {
                // 主线正在运行且存在标记（上次调试还没切回清理）→ 视为主线 + 待清理
                return null;
            }
            var parent = Directory.GetParent(hostDataDir);
            if (parent != null && Path.GetFileName(hostDataDir) == ActiveDirName)
            {
                var parentMarker = Path.Combine(parent.FullName, MarkerFileName);
                if (File.Exists(parentMarker))
                {
                    try
                    {
                        var state = JsonSerializer.Deserialize<DebugModeState>(
                            File.ReadAllText(parentMarker));
                        if (state != null && string.Equals(
                                Path.GetFullPath(state.debugDir ?? string.Empty),
                                hostDataDir, StringComparison.OrdinalIgnoreCase))
                            return state;
                    }
                    catch
                    {
                        /* 标记损坏按主线处理 */
                    }
                }
            }
            return null;
        }

        public static void Write(string mainDir, string debugDir)
        {
            mainDir = Path.GetFullPath(mainDir);
            var state = new DebugModeState
            {
                mainDir = mainDir,
                debugDir = Path.GetFullPath(debugDir)
            };
            File.WriteAllText(Path.Combine(mainDir, MarkerFileName),
                JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }

        /// <summary>切回主线后的清理：删除标记与整个调试目录（调用方需保证旧进程已释放文件句柄）。</summary>
        public static void Clear(string mainDir)
        {
            mainDir = Path.GetFullPath(mainDir);
            var marker = Path.Combine(mainDir, MarkerFileName);
            if (File.Exists(marker)) File.Delete(marker);
            var debugDir = Path.Combine(mainDir, ActiveDirName);
            if (Directory.Exists(debugDir)) Directory.Delete(debugDir, recursive: true);
        }
    }
}
