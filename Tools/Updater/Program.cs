using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace TraceSoul2.Updater
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            var options = ParseArguments(args);
            var source = FullRequired(options, "source");
            var target = FullRequired(options, "target");
            var home = FullRequired(options, "home");
            var version = Required(options, "version");
            if (!int.TryParse(Required(options, "pid"), out var processId) || processId <= 0)
                throw new InvalidOperationException("无效的宿主 PID。");

            var logDirectory = Path.Combine(home, "updates");
            Directory.CreateDirectory(logDirectory);
            var logPath = Path.Combine(logDirectory, "update.log");
            try
            {
                ValidatePaths(source, target, home, version);
                Log(logPath, "等待旧宿主退出，目标版本 v" + version);
                WaitForExit(processId, TimeSpan.FromMinutes(2));
                Apply(source, target, home, version, logPath);
                return 0;
            }
            catch (Exception exception)
            {
                Log(logPath, "更新失败：" + exception);
                return 1;
            }
        }

        private static void Apply(string source, string target, string home, string version, string logPath)
        {
            var parent = Directory.GetParent(target)?.FullName
                         ?? throw new InvalidOperationException("应用目录没有父目录。");
            var targetName = Path.GetFileName(target);
            var oldVersion = ReadManifestVersion(Path.Combine(target, "tracesoul2.install.json"));
            var backup = UniqueDirectory(parent,
                "." + targetName + ".tracesoul2-backup-" + oldVersion + "-" +
                DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss"));
            var failed = UniqueDirectory(parent,
                "." + targetName + ".tracesoul2-failed-" + version + "-" +
                DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss"));
            var movedOld = false;
            var movedNew = false;
            try
            {
                Directory.Move(target, backup);
                movedOld = true;
                Directory.Move(source, target);
                movedNew = true;
                File.WriteAllText(Path.Combine(home, "updates", "last-update.json"),
                    JsonSerializer.Serialize(new
                    {
                        fromVersion = oldVersion,
                        toVersion = version,
                        installedUtc = DateTimeOffset.UtcNow.ToString("O"),
                        backupDirectory = backup
                    }, new JsonSerializerOptions { WriteIndented = true }));
                Log(logPath, "应用目录替换完成；旧版保留于 " + backup);
                RestartUnlessSupervised(target, logPath);
            }
            catch
            {
                if (movedNew && Directory.Exists(target)) Directory.Move(target, failed);
                if (movedOld && Directory.Exists(backup) && !Directory.Exists(target))
                    Directory.Move(backup, target);
                Log(logPath, "已回滚旧版；失败的新目录保留于 " + failed);
                RestartUnlessSupervised(target, logPath);
                throw;
            }
        }

        private static void RestartUnlessSupervised(string target, string logPath)
        {
            var restartMode = Environment.GetEnvironmentVariable("TRACESOUL2_RESTART_MODE");
            if (string.Equals(restartMode, "supervisor", StringComparison.OrdinalIgnoreCase))
            {
                Log(logPath, "由外部守护进程重新启动宿主。");
                return;
            }
            var exe = Path.Combine(target, "TraceSoul2.Host.exe");
            var dll = Path.Combine(target, "TraceSoul2.Host.dll");
            var start = new ProcessStartInfo
            {
                FileName = File.Exists(exe) ? exe : "dotnet",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = target
            };
            if (!File.Exists(exe)) start.ArgumentList.Add(dll);
            if (Process.Start(start) == null) throw new InvalidOperationException("新版宿主启动失败。");
            Log(logPath, "新版宿主已启动。");
        }

        private static void ValidatePaths(string source, string target, string home, string version)
        {
            if (!Directory.Exists(source) || !Directory.Exists(target) || !Directory.Exists(home))
                throw new DirectoryNotFoundException("更新源、应用目录或家目录不存在。");
            var targetParent = Directory.GetParent(target)?.FullName
                               ?? throw new InvalidOperationException("应用目录没有父目录。");
            if (!string.Equals(Directory.GetParent(source)?.FullName, targetParent,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("准备目录必须与应用目录位于同一父目录，才能原子替换。");
            var expectedPrefix = "." + Path.GetFileName(target) + ".tracesoul2-update-";
            if (!Path.GetFileName(source).StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("准备目录名称不符合 TraceSoul2 更新约定。");
            var targetPrefix = target.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (home.StartsWith(targetPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("家目录位于应用目录内，拒绝更新以免覆盖角色数据。");
            if (!string.Equals(ReadManifestVersion(Path.Combine(source, "tracesoul2.install.json")),
                    version, StringComparison.Ordinal))
                throw new InvalidOperationException("准备目录版本与更新计划不一致。");
            if (!File.Exists(Path.Combine(source, "TraceSoul2.Host.dll")))
                throw new InvalidOperationException("准备目录缺少 TraceSoul2.Host.dll。");
        }

        private static void WaitForExit(int processId, TimeSpan timeout)
        {
            try
            {
                using (var process = Process.GetProcessById(processId))
                {
                    if (!process.WaitForExit((int)timeout.TotalMilliseconds))
                        throw new TimeoutException("旧宿主在两分钟内没有退出。");
                }
            }
            catch (ArgumentException)
            {
                // 宿主已退出。
            }
        }

        private static string ReadManifestVersion(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("缺少安装标记。", path);
            using (var document = JsonDocument.Parse(File.ReadAllText(path)))
            {
                var root = document.RootElement;
                var product = root.TryGetProperty("product", out var p) ? p.GetString() : string.Empty;
                var version = root.TryGetProperty("version", out var v) ? v.GetString() : string.Empty;
                if (!string.Equals(product, "TraceSoul2", StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(version))
                    throw new InvalidOperationException("安装标记不是 TraceSoul2。");
                return version;
            }
        }

        private static string UniqueDirectory(string parent, string name)
        {
            var candidate = Path.Combine(parent, name);
            var counter = 1;
            while (Directory.Exists(candidate)) candidate = Path.Combine(parent, name + "-" + counter++);
            return candidate;
        }

        private static Dictionary<string, string> ParseArguments(string[] args)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < (args?.Length ?? 0); i += 2)
            {
                if (i + 1 >= args.Length || !args[i].StartsWith("--", StringComparison.Ordinal))
                    throw new InvalidOperationException("更新器参数必须成对出现。");
                result[args[i].Substring(2)] = args[i + 1];
            }
            return result;
        }

        private static string Required(Dictionary<string, string> options, string key)
        {
            if (!options.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException("缺少参数 --" + key + "。");
            return value.Trim();
        }

        private static string FullRequired(Dictionary<string, string> options, string key)
        {
            return Path.GetFullPath(Required(options, key))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static void Log(string path, string message)
        {
            File.AppendAllText(path,
                DateTimeOffset.Now.ToString("O") + " " + message + Environment.NewLine);
        }
    }
}
