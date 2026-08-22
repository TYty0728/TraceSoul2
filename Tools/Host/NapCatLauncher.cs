using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace TraceSoul2.Host
{
    /// <summary>从本机已保存的路径启动 NapCat。只接受本地 exe/bat/cmd，不拼接命令行。</summary>
    public static class NapCatLauncher
    {
        private static readonly string[] LauncherNames =
        {
            "启动NapCat.bat",
            "NapCatWinBootMain.exe",
            Path.Combine("bootmain", "NapCatWinBootMain.exe"),
            Path.Combine("bootmain", "napcat.bat"),
            "napcat.bat"
        };

        public static NapCatLaunchResult Start(string configuredPath)
        {
            var launcher = Resolve(configuredPath);
            var running = Process.GetProcessesByName("NapCatWinBootMain");
            try
            {
                var existing = running.FirstOrDefault(process => !process.HasExited);
                if (existing != null)
                {
                    return new NapCatLaunchResult
                    {
                        started = false,
                        alreadyRunning = true,
                        processId = existing.Id,
                        launcherPath = launcher,
                        message = "NapCat 已经在运行，无需重复启动。"
                    };
                }
            }
            finally
            {
                foreach (var process in running) process.Dispose();
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = launcher,
                WorkingDirectory = Path.GetDirectoryName(launcher) ?? string.Empty,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal
            };
            var started = Process.Start(startInfo);
            if (started == null)
                throw new InvalidOperationException("Windows 没有返回 NapCat 启动进程。");
            try
            {
                return new NapCatLaunchResult
                {
                    started = true,
                    alreadyRunning = false,
                    processId = started.Id,
                    launcherPath = launcher,
                    message = "已提交 NapCat 启动请求；请稍等它登录并回连。"
                };
            }
            finally
            {
                started.Dispose();
            }
        }

        public static List<string> Discover(string configuredPath)
        {
            var results = new List<string>();
            TryAddConfigured(results, configuredPath);

            var roots = new List<string>();
            AddRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            AddRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
            AddRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            AddRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
            AddRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.IsReady && drive.DriveType == DriveType.Fixed)
                        AddRoot(roots, drive.RootDirectory.FullName);
                }
                catch { /* 不可访问的盘忽略 */ }
            }

            foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                AddCandidates(results, Path.Combine(root, "NapCat"));
                AddCandidates(results, Path.Combine(root, "NapCatQQ"));
                AddCandidates(results, Path.Combine(root, "AISoftWare", "NapCat"));
            }
            return results;
        }

        public static string Resolve(string configuredPath)
        {
            if (string.IsNullOrWhiteSpace(configuredPath))
                throw new InvalidOperationException("请先保存本机 NapCat 启动文件或目录。 ");
            var trimmed = configuredPath.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(trimmed))
                throw new InvalidOperationException("NapCat 路径必须是完整的本机绝对路径。");
            var full = Path.GetFullPath(trimmed);
            if (full.StartsWith("\\\\", StringComparison.Ordinal))
                throw new InvalidOperationException("NapCat 路径必须在本机磁盘，不能使用网络共享路径。");
            if (Directory.Exists(full))
            {
                full = LauncherNames.Select(name => Path.Combine(full, name))
                    .FirstOrDefault(File.Exists);
                if (string.IsNullOrWhiteSpace(full))
                    throw new FileNotFoundException("目录里没有找到支持的 NapCat 启动文件。", configuredPath);
            }
            if (!File.Exists(full))
                throw new FileNotFoundException("找不到 NapCat 启动文件。", full);
            var extension = Path.GetExtension(full);
            if (!string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(extension, ".bat", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("NapCat 启动文件只支持 .exe、.bat 或 .cmd。");
            return full;
        }

        private static void TryAddConfigured(List<string> results, string configuredPath)
        {
            try
            {
                var resolved = Resolve(configuredPath);
                if (!results.Contains(resolved, StringComparer.OrdinalIgnoreCase)) results.Add(resolved);
            }
            catch { /* 未配置或旧路径失效，不影响自动发现 */ }
        }

        private static void AddCandidates(List<string> results, string directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
            foreach (var name in LauncherNames)
            {
                var path = Path.Combine(directory, name);
                if (File.Exists(path) && !results.Contains(path, StringComparer.OrdinalIgnoreCase))
                    results.Add(path);
            }
        }

        private static void AddRoot(List<string> roots, string root)
        {
            if (!string.IsNullOrWhiteSpace(root)) roots.Add(root);
        }
    }

    public sealed class NapCatLaunchResult
    {
        public bool started { get; set; }
        public bool alreadyRunning { get; set; }
        public int processId { get; set; }
        public string launcherPath { get; set; }
        public string message { get; set; }
    }
}
