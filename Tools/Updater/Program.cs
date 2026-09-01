using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace TraceSoul2.Updater
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args != null && args.Length == 1 &&
                string.Equals(args[0], "--self-test", StringComparison.OrdinalIgnoreCase))
                return RunSelfTest();
            var options = ParseArguments(args);
            var source = FullRequired(options, "source");
            var target = FullRequired(options, "target");
            var home = FullRequired(options, "home");
            // 0.1.3 及更早的 Host 启动新包里的更新器时尚不会传 --plugins，
            // 但已经会传 TRACESOUL2_PLUGINS。保留环境变量回退，保证原地跨版本升级。
            var plugins = FullOptionOrEnvironment(options, "plugins", "TRACESOUL2_PLUGINS");
            var version = Required(options, "version");
            if (!int.TryParse(Required(options, "pid"), out var processId) || processId <= 0)
                throw new InvalidOperationException("无效的宿主 PID。");

            var logDirectory = Path.Combine(home, "updates");
            Directory.CreateDirectory(logDirectory);
            var logPath = Path.Combine(logDirectory, "update.log");
            try
            {
                ValidatePaths(source, target, home, plugins, version);
                Log(logPath, "等待旧宿主退出，目标版本 v" + version);
                WaitForExit(processId, TimeSpan.FromMinutes(2));
                Apply(source, target, home, plugins, version, logPath);
                return 0;
            }
            catch (Exception exception)
            {
                Log(logPath, "更新失败：" + exception);
                return 1;
            }
        }

        private static void Apply(
            string source,
            string target,
            string home,
            string plugins,
            string version,
            string logPath)
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
            var pluginReplacements = new List<PluginReplacement>();
            try
            {
                Directory.Move(target, backup);
                movedOld = true;
                Directory.Move(source, target);
                movedNew = true;
                ApplyBundledPlugins(target, plugins, version, logPath, pluginReplacements);
                File.WriteAllText(Path.Combine(home, "updates", "last-update.json"),
                    JsonSerializer.Serialize(new
                    {
                        fromVersion = oldVersion,
                        toVersion = version,
                        installedUtc = DateTimeOffset.UtcNow.ToString("O"),
                        backupDirectory = backup,
                        bundledPlugins = pluginReplacements.Select(x => new
                        {
                            x.Name,
                            backupDirectory = x.MovedOld ? x.Backup : string.Empty
                        }).ToList()
                    }, new JsonSerializerOptions { WriteIndented = true }));
                Log(logPath, "应用目录与 " + pluginReplacements.Count +
                    " 个官方插件替换完成；旧版应用保留于 " + backup);
                RestartUnlessSupervised(target, logPath);
            }
            catch
            {
                RollBackPlugins(pluginReplacements, version, logPath);
                if (movedNew && Directory.Exists(target)) Directory.Move(target, failed);
                if (movedOld && Directory.Exists(backup) && !Directory.Exists(target))
                    Directory.Move(backup, target);
                Log(logPath, "已回滚旧版；失败的新目录保留于 " + failed);
                RestartUnlessSupervised(target, logPath);
                throw;
            }
        }

        private static void ApplyBundledPlugins(
            string appRoot,
            string pluginsRoot,
            string version,
            string logPath,
            List<PluginReplacement> replacements)
        {
            var names = ReadBundledPluginNames(Path.Combine(appRoot, "tracesoul2.install.json"));
            if (names.Count == 0)
            {
                Log(logPath, "更新包未声明官方插件，跳过插件升级。");
                return;
            }
            Directory.CreateDirectory(pluginsRoot);
            var pluginParent = Directory.GetParent(pluginsRoot)?.FullName
                               ?? throw new InvalidOperationException("插件目录没有父目录。");
            var pluginRootName = Path.GetFileName(pluginsRoot);
            foreach (var name in names)
            {
                var source = Path.Combine(appRoot, "BundledPlugins", name);
                ValidatePluginPackage(source, name);
                var target = Path.Combine(pluginsRoot, name);
                var stamp = version + "-" + DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
                var staged = UniqueDirectory(pluginParent,
                    "." + pluginRootName + "." + name + ".tracesoul2-update-" + stamp);
                var backup = UniqueDirectory(pluginParent,
                    "." + pluginRootName + "." + name + ".tracesoul2-backup-" + stamp);
                var replacement = new PluginReplacement
                {
                    Name = name,
                    Target = target,
                    Staged = staged,
                    Backup = backup
                };
                replacements.Add(replacement);
                Directory.CreateDirectory(staged);
                CopyDirectory(source, staged);
                ValidatePluginPackage(staged, name);
                if (Directory.Exists(target))
                {
                    Directory.Move(target, backup);
                    replacement.MovedOld = true;
                }
                Directory.Move(staged, target);
                replacement.MovedNew = true;
                Log(logPath, "官方插件已升级：" + name +
                    (replacement.MovedOld ? "；旧包保留于 " + backup : "；此前未安装"));
            }
        }

        private static void RollBackPlugins(
            List<PluginReplacement> replacements,
            string version,
            string logPath)
        {
            foreach (var item in (replacements ?? new List<PluginReplacement>()).AsEnumerable().Reverse())
            {
                try
                {
                    if (item.MovedNew && Directory.Exists(item.Target))
                    {
                        var parent = Directory.GetParent(item.Target)?.Parent?.FullName ??
                                     Directory.GetParent(item.Target)?.FullName;
                        if (string.IsNullOrWhiteSpace(parent))
                            throw new InvalidOperationException("插件目录没有可用的回滚父目录。");
                        var failed = UniqueDirectory(parent,
                            ".plugin.tracesoul2-failed-" + item.Name + "-" + version + "-" +
                            DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss"));
                        Directory.Move(item.Target, failed);
                    }
                    if (item.MovedOld && Directory.Exists(item.Backup) && !Directory.Exists(item.Target))
                        Directory.Move(item.Backup, item.Target);
                    Log(logPath, "官方插件已回滚：" + item.Name);
                }
                catch (Exception exception)
                {
                    Log(logPath, "官方插件回滚失败：" + item.Name + " / " + exception.Message);
                }
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

        private static void ValidatePaths(
            string source,
            string target,
            string home,
            string plugins,
            string version)
        {
            if (!Directory.Exists(source) || !Directory.Exists(target) || !Directory.Exists(home))
                throw new DirectoryNotFoundException("更新源、应用目录或家目录不存在。");
            Directory.CreateDirectory(plugins);
            var targetParent = Directory.GetParent(target)?.FullName
                               ?? throw new InvalidOperationException("应用目录没有父目录。");
            if (!string.Equals(Directory.GetParent(source)?.FullName, targetParent,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("准备目录必须与应用目录位于同一父目录，才能原子替换。");
            var expectedPrefix = "." + Path.GetFileName(target) + ".tracesoul2-update-";
            if (!Path.GetFileName(source).StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("准备目录名称不符合 TraceSoul2 更新约定。");
            if (string.Equals(home, target, StringComparison.OrdinalIgnoreCase) || IsInside(home, target))
                throw new InvalidOperationException("家目录位于应用目录内，拒绝更新以免覆盖角色数据。");
            if (string.Equals(plugins, target, StringComparison.OrdinalIgnoreCase) ||
                IsInside(plugins, target) || IsInside(target, plugins))
                throw new InvalidOperationException("插件目录与应用目录重叠，拒绝更新以免把插件代码卷入应用替换。");
            if (!string.Equals(ReadManifestVersion(Path.Combine(source, "tracesoul2.install.json")),
                    version, StringComparison.Ordinal))
                throw new InvalidOperationException("准备目录版本与更新计划不一致。");
            if (!File.Exists(Path.Combine(source, "TraceSoul2.Host.dll")))
                throw new InvalidOperationException("准备目录缺少 TraceSoul2.Host.dll。");
            foreach (var name in ReadBundledPluginNames(Path.Combine(source, "tracesoul2.install.json")))
                ValidatePluginPackage(Path.Combine(source, "BundledPlugins", name), name);
        }

        private static bool IsInside(string candidate, string parent)
        {
            var prefix = parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                         Path.DirectorySeparatorChar;
            return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static List<string> ReadBundledPluginNames(string manifestPath)
        {
            using (var document = JsonDocument.Parse(File.ReadAllText(manifestPath)))
            {
                if (!document.RootElement.TryGetProperty("bundledPlugins", out var items) ||
                    items.ValueKind == JsonValueKind.Null)
                    return new List<string>();
                if (items.ValueKind != JsonValueKind.Array)
                    throw new InvalidOperationException("安装标记 bundledPlugins 清单格式无效。");
                var names = new List<string>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in items.EnumerateArray())
                {
                    var name = item.ValueKind == JsonValueKind.String ? item.GetString() ?? string.Empty : string.Empty;
                    if (!IsSafePackageName(name) || !seen.Add(name))
                        throw new InvalidOperationException("安装标记包含无效或重复的官方插件名：" + name);
                    names.Add(name);
                }
                return names;
            }
        }

        private static void ValidatePluginPackage(string folder, string name)
        {
            var manifest = Path.Combine(folder, "plugin.json");
            if (!Directory.Exists(folder) || !File.Exists(manifest))
                throw new InvalidOperationException("官方插件目录或 plugin.json 缺失：" + name);
            using (var document = JsonDocument.Parse(File.ReadAllText(manifest)))
            {
                var root = document.RootElement;
                var dll = root.TryGetProperty("dll", out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString() ?? string.Empty
                    : string.Empty;
                if (string.IsNullOrWhiteSpace(dll) ||
                    !string.Equals(Path.GetFileName(dll), dll, StringComparison.Ordinal) ||
                    !File.Exists(Path.Combine(folder, dll)))
                    throw new InvalidOperationException("官方插件包缺少清单指定的 DLL：" + name);
            }
        }

        private static bool IsSafePackageName(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 80 || value.StartsWith(".", StringComparison.Ordinal))
                return false;
            return string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal) &&
                   value.All(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == '.');
        }

        private static void CopyDirectory(string source, string destination)
        {
            foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(source, directory);
                Directory.CreateDirectory(Path.Combine(destination, relative));
            }
            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(source, file);
                var target = Path.Combine(destination, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target, overwrite: false);
            }
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

        private static string FullOptionOrEnvironment(
            Dictionary<string, string> options,
            string key,
            string environmentName)
        {
            string value;
            if (!options.TryGetValue(key, out value) || string.IsNullOrWhiteSpace(value))
                value = Environment.GetEnvironmentVariable(environmentName);
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException(
                    "缺少参数 --" + key + "，且环境变量 " + environmentName + " 未设置。");
            return Path.GetFullPath(value.Trim())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static void Log(string path, string message)
        {
            File.AppendAllText(path,
                DateTimeOffset.Now.ToString("O") + " " + message + Environment.NewLine);
        }

        private static int RunSelfTest()
        {
            var root = Path.Combine(Path.GetTempPath(), "tracesoul2-updater-selftest-" + Guid.NewGuid().ToString("N"));
            var target = Path.Combine(root, "App");
            var source = Path.Combine(root, ".App.tracesoul2-update-0.1.4-selftest");
            var home = Path.Combine(root, "Data");
            var plugins = Path.Combine(root, "Plugins");
            var pluginData = Path.Combine(root, "plugins_data", "qq-test");
            var oldRestartMode = Environment.GetEnvironmentVariable("TRACESOUL2_RESTART_MODE");
            var oldPluginsEnvironment = Environment.GetEnvironmentVariable("TRACESOUL2_PLUGINS");
            try
            {
                Directory.CreateDirectory(target);
                Directory.CreateDirectory(source);
                Directory.CreateDirectory(home);
                Directory.CreateDirectory(Path.Combine(home, "updates"));
                Directory.CreateDirectory(plugins);
                Directory.CreateDirectory(pluginData);
                Environment.SetEnvironmentVariable("TRACESOUL2_PLUGINS", plugins);
                RequireSelfTest(!IsSafePackageName("..") && !IsSafePackageName(".hidden") &&
                                !IsSafePackageName("folder/name") && IsSafePackageName("qq-imagegen"),
                    "官方插件目录名边界失效");
                RequireSelfTest(
                    string.Equals(
                        FullOptionOrEnvironment(new Dictionary<string, string>(), "plugins", "TRACESOUL2_PLUGINS"),
                        Path.GetFullPath(plugins), StringComparison.OrdinalIgnoreCase),
                    "旧 Host 的插件环境变量回退失效");
                WriteInstallManifest(target, "0.1.3", new string[0]);
                File.WriteAllText(Path.Combine(target, "TraceSoul2.Host.dll"), "old-host");
                WriteInstallManifest(source, "0.1.4", new[] { "qq-test" });
                File.WriteAllText(Path.Combine(source, "TraceSoul2.Host.dll"), "new-host");
                var bundled = Path.Combine(source, "BundledPlugins", "qq-test");
                Directory.CreateDirectory(bundled);
                File.WriteAllText(Path.Combine(bundled, "plugin.json"), "{\"dll\":\"Test.Plugin.dll\"}");
                File.WriteAllText(Path.Combine(bundled, "Test.Plugin.dll"), "new-plugin");
                var installed = Path.Combine(plugins, "qq-test");
                Directory.CreateDirectory(installed);
                File.WriteAllText(Path.Combine(installed, "plugin.json"), "{\"dll\":\"Test.Plugin.dll\"}");
                File.WriteAllText(Path.Combine(installed, "Test.Plugin.dll"), "old-plugin");
                File.WriteAllText(Path.Combine(pluginData, "config.json"), "{\"secret\":\"kept\"}");

                ValidatePaths(source, target, home, plugins, "0.1.4");
                Environment.SetEnvironmentVariable("TRACESOUL2_RESTART_MODE", "supervisor");
                Apply(source, target, home, plugins, "0.1.4", Path.Combine(home, "updates", "self-test.log"));

                RequireSelfTest(ReadManifestVersion(Path.Combine(target, "tracesoul2.install.json")) == "0.1.4",
                    "应用版本没有替换");
                RequireSelfTest(File.ReadAllText(Path.Combine(installed, "Test.Plugin.dll")) == "new-plugin",
                    "官方插件没有替换");
                RequireSelfTest(File.ReadAllText(Path.Combine(pluginData, "config.json")).Contains("kept"),
                    "插件数据被改动");
                RequireSelfTest(Directory.GetDirectories(root, ".App.tracesoul2-backup-*", SearchOption.TopDirectoryOnly).Length == 1,
                    "应用备份缺失");
                RequireSelfTest(Directory.GetDirectories(root, ".Plugins.qq-test.tracesoul2-backup-*", SearchOption.TopDirectoryOnly).Length == 1,
                    "插件备份缺失");
                RunRollbackSelfTest(Path.Combine(root, "rollback"));
                Console.WriteLine("Updater self-test passed: App + bundled plugins replaced, config preserved, backups retained, failure rolled back.");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("Updater self-test failed: " + exception);
                return 1;
            }
            finally
            {
                Environment.SetEnvironmentVariable("TRACESOUL2_RESTART_MODE", oldRestartMode);
                Environment.SetEnvironmentVariable("TRACESOUL2_PLUGINS", oldPluginsEnvironment);
                try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
                catch { /* 临时自测目录留给人工诊断。 */ }
            }
        }

        private static void WriteInstallManifest(string directory, string version, IEnumerable<string> plugins)
        {
            File.WriteAllText(Path.Combine(directory, "tracesoul2.install.json"),
                JsonSerializer.Serialize(new
                {
                    product = "TraceSoul2",
                    version,
                    runtime = "self-test",
                    bundledPlugins = plugins == null ? new string[0] : plugins.ToArray()
                }));
        }

        private static void RunRollbackSelfTest(string root)
        {
            var target = Path.Combine(root, "App");
            var source = Path.Combine(root, ".App.tracesoul2-update-0.1.4-rollback");
            var home = Path.Combine(root, "Data");
            var plugins = Path.Combine(root, "Plugins");
            Directory.CreateDirectory(target);
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(Path.Combine(home, "updates"));
            Directory.CreateDirectory(plugins);
            WriteInstallManifest(target, "0.1.3", new string[0]);
            File.WriteAllText(Path.Combine(target, "TraceSoul2.Host.dll"), "old-host");
            WriteInstallManifest(source, "0.1.4", new[] { "qq-first", "qq-blocked" });
            File.WriteAllText(Path.Combine(source, "TraceSoul2.Host.dll"), "new-host");
            WritePluginPackage(Path.Combine(source, "BundledPlugins", "qq-first"), "new-first");
            WritePluginPackage(Path.Combine(source, "BundledPlugins", "qq-blocked"), "new-blocked");
            WritePluginPackage(Path.Combine(plugins, "qq-first"), "old-first");
            File.WriteAllText(Path.Combine(plugins, "qq-blocked"), "阻止目录移动");
            ValidatePaths(source, target, home, plugins, "0.1.4");
            var failed = false;
            try
            {
                Apply(source, target, home, plugins, "0.1.4", Path.Combine(home, "updates", "rollback.log"));
            }
            catch
            {
                failed = true;
            }
            RequireSelfTest(failed, "故障场景没有触发失败");
            RequireSelfTest(ReadManifestVersion(Path.Combine(target, "tracesoul2.install.json")) == "0.1.3",
                "失败后应用没有回滚");
            RequireSelfTest(File.ReadAllText(Path.Combine(plugins, "qq-first", "Test.Plugin.dll")) == "old-first",
                "失败后已替换插件没有回滚");
            RequireSelfTest(File.ReadAllText(Path.Combine(plugins, "qq-blocked")) == "阻止目录移动",
                "失败点原文件被改动");
        }

        private static void WritePluginPackage(string directory, string dllContent)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "plugin.json"), "{\"dll\":\"Test.Plugin.dll\"}");
            File.WriteAllText(Path.Combine(directory, "Test.Plugin.dll"), dllContent);
        }

        private static void RequireSelfTest(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private sealed class PluginReplacement
        {
            public string Name;
            public string Target;
            public string Staged;
            public string Backup;
            public bool MovedOld;
            public bool MovedNew;
        }
    }
}
