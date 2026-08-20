using System;
using System.IO;
using System.Text.Json;

namespace TraceSoul2.Host
{
    /// <summary>
    /// 家目录：软件安装目录之外的整合文件夹。
    /// 角色库在 souls/&lt;id&gt;/，已装器官在 plugins/，机器设置在 home.json。
    /// </summary>
    public sealed class TraceHomeLayout
    {
        public string Root { get; set; }
        public string SoulsDirectory { get; set; }
        public string PluginsDirectory { get; set; }
        public string SoulId { get; set; }
        public string SoulDirectory { get; set; }
        public string UpdatesDirectory { get; set; }
        public string UpdateRepository { get; set; }
        public bool UpdateRepositoryFromEnvironment { get; set; }
        public string Urls { get; set; }
        public bool UsedLegacyDataEnv { get; set; }
    }

    public static class TraceHome
    {
        public const string EnvHome = "TRACESOUL2_HOME";
        public const string EnvData = "TRACESOUL2_DATA";
        public const string EnvPlugins = "TRACESOUL2_PLUGINS";
        public const string EnvUrls = "TRACESOUL2_URLS";
        public const string EnvUpdateRepository = "TRACESOUL2_UPDATE_REPOSITORY";
        public const string FileName = "home.json";
        public const string DefaultUrls = "http://127.0.0.1:5080";

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        public static TraceHomeLayout Current { get; private set; }

        public static string DefaultRoot()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TraceSoul2");
        }

        /// <summary>解析家目录与当前角色。启动时调用一次，并写回环境变量给子进程继承。</summary>
        public static TraceHomeLayout Resolve()
        {
            var homeEnv = ReadEnv(EnvHome);
            var dataEnv = ReadEnv(EnvData);
            var pluginsEnv = ReadEnv(EnvPlugins);
            var urlsEnv = ReadEnv(EnvUrls);
            var updateRepositoryEnv = ReadEnv(EnvUpdateRepository);

            var layout = new TraceHomeLayout();
            if (!string.IsNullOrWhiteSpace(homeEnv))
            {
                layout.Root = Path.GetFullPath(homeEnv);
                EnsureScaffold(layout.Root);
                var file = ReadFile(layout.Root);
                layout.SoulsDirectory = Path.Combine(layout.Root, "souls");
                layout.PluginsDirectory = ResolveDirectory(
                    layout.Root, FirstNonEmpty(pluginsEnv, file.pluginsDirectory), "plugins");
                Directory.CreateDirectory(layout.PluginsDirectory);
                if (!string.IsNullOrWhiteSpace(dataEnv))
                {
                    layout.SoulDirectory = Path.GetFullPath(dataEnv);
                    layout.UsedLegacyDataEnv = true;
                    layout.SoulId = InferSoulId(layout.SoulsDirectory, layout.SoulDirectory)
                        ?? Path.GetFileName(layout.SoulDirectory);
                }
                else
                {
                    layout.SoulId = PickSoulId(layout.SoulsDirectory, file.activeSoul);
                    layout.SoulDirectory = Path.Combine(layout.SoulsDirectory, layout.SoulId);
                    Directory.CreateDirectory(layout.SoulDirectory);
                    if (!string.Equals(file.activeSoul, layout.SoulId, StringComparison.OrdinalIgnoreCase))
                        WriteFile(layout.Root, layout.SoulId, file.urls);
                }
                layout.Urls = FirstNonEmpty(urlsEnv, file.urls, DefaultUrls);
                layout.UpdateRepository = FirstNonEmpty(updateRepositoryEnv, file.updateRepository);
            }
            else if (!string.IsNullOrWhiteSpace(dataEnv))
            {
                layout.UsedLegacyDataEnv = true;
                layout.SoulDirectory = Path.GetFullPath(dataEnv);
                layout.SoulId = Path.GetFileName(layout.SoulDirectory);
                layout.Root = Path.GetDirectoryName(layout.SoulDirectory);
                layout.SoulsDirectory = layout.Root;
                layout.PluginsDirectory = string.IsNullOrWhiteSpace(pluginsEnv)
                    ? Path.Combine(DefaultRoot(), "plugins")
                    : Path.GetFullPath(pluginsEnv);
                Directory.CreateDirectory(layout.PluginsDirectory);
                layout.Urls = FirstNonEmpty(urlsEnv, DefaultUrls);
                layout.UpdateRepository = updateRepositoryEnv;
            }
            else
            {
                layout.Root = DefaultRoot();
                EnsureScaffold(layout.Root);
                var file = ReadFile(layout.Root);
                layout.SoulsDirectory = Path.Combine(layout.Root, "souls");
                layout.PluginsDirectory = ResolveDirectory(
                    layout.Root, FirstNonEmpty(pluginsEnv, file.pluginsDirectory), "plugins");
                Directory.CreateDirectory(layout.PluginsDirectory);
                layout.SoulId = PickSoulId(layout.SoulsDirectory, file.activeSoul);
                layout.SoulDirectory = Path.Combine(layout.SoulsDirectory, layout.SoulId);
                Directory.CreateDirectory(layout.SoulDirectory);
                layout.Urls = FirstNonEmpty(urlsEnv, file.urls, DefaultUrls);
                layout.UpdateRepository = FirstNonEmpty(updateRepositoryEnv, file.updateRepository);
                WriteFile(layout.Root, layout.SoulId, layout.Urls);
            }

            Directory.CreateDirectory(layout.SoulDirectory);
            layout.UpdatesDirectory = Path.Combine(layout.Root, "updates");
            Directory.CreateDirectory(layout.UpdatesDirectory);
            layout.UpdateRepositoryFromEnvironment = !string.IsNullOrWhiteSpace(updateRepositoryEnv);
            Current = layout;
            ApplyToEnvironment(layout);
            return layout;
        }

        /// <summary>控制台切换角色后，把 home.json 的当前角色写成相对 souls/ 的路径。</summary>
        public static void RememberActiveSoul(string soulDirectory)
        {
            if (Current == null || string.IsNullOrWhiteSpace(Current.Root) ||
                string.IsNullOrWhiteSpace(Current.SoulsDirectory))
                return;
            var id = InferSoulId(Current.SoulsDirectory, soulDirectory);
            if (string.IsNullOrWhiteSpace(id)) return;
            var file = ReadFile(Current.Root);
            WriteFile(Current.Root, id, file.urls);
            Current.SoulId = id;
            Current.SoulDirectory = Path.GetFullPath(soulDirectory);
        }

        public static string HostVersion()
        {
            var assembly = typeof(TraceHome).Assembly;
            var informational = assembly.GetCustomAttributes(
                typeof(System.Reflection.AssemblyInformationalVersionAttribute), false);
            if (informational != null && informational.Length > 0)
            {
                var value = ((System.Reflection.AssemblyInformationalVersionAttribute)informational[0])
                    .InformationalVersion;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    var plus = value.IndexOf('+');
                    return plus >= 0 ? value.Substring(0, plus) : value;
                }
            }
            var version = assembly.GetName().Version;
            return version == null ? "0.0.0" : version.ToString(3);
        }

        /// <summary>保存 GitHub owner/repository；环境变量覆盖仍有最高优先级。</summary>
        public static void RememberUpdateRepository(string repository)
        {
            if (Current == null || string.IsNullOrWhiteSpace(Current.Root))
                throw new InvalidOperationException("当前没有可写的 TraceSoul2 家目录。");
            repository = (repository ?? string.Empty).Trim();
            var file = ReadFile(Current.Root);
            WriteFile(Current.Root, file.activeSoul, file.urls, updateRepository: repository);
            Current.UpdateRepository = Current.UpdateRepositoryFromEnvironment
                ? FirstNonEmpty(ReadEnv(EnvUpdateRepository), repository)
                : repository;
        }

        private static void ApplyToEnvironment(TraceHomeLayout layout)
        {
            if (!string.IsNullOrWhiteSpace(layout.Root))
                Environment.SetEnvironmentVariable(EnvHome, layout.Root);
            Environment.SetEnvironmentVariable(EnvData, layout.SoulDirectory);
            Environment.SetEnvironmentVariable(EnvPlugins, layout.PluginsDirectory);
            if (string.IsNullOrWhiteSpace(ReadEnv(EnvUrls)) && !string.IsNullOrWhiteSpace(layout.Urls))
                Environment.SetEnvironmentVariable(EnvUrls, layout.Urls);
        }

        private static void EnsureScaffold(string root)
        {
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(Path.Combine(root, "souls"));
            Directory.CreateDirectory(Path.Combine(root, "plugins"));
            var path = Path.Combine(root, FileName);
            if (!File.Exists(path))
                WriteFile(root, "", DefaultUrls, pluginsDirectory: "plugins", updateRepository: "");
        }

        private static string PickSoulId(string soulsDirectory, string preferred)
        {
            if (!string.IsNullOrWhiteSpace(preferred))
            {
                var candidate = Path.GetFullPath(Path.Combine(soulsDirectory, preferred.Replace('/', Path.DirectorySeparatorChar)));
                if (Directory.Exists(candidate)) return preferred.Replace('\\', '/').Trim();
            }
            if (Directory.Exists(soulsDirectory))
            {
                foreach (var dir in Directory.GetDirectories(soulsDirectory))
                {
                    if (File.Exists(Path.Combine(dir, "tracesoul2-brainframe.sqlite3")))
                        return Path.GetFileName(dir);
                }
                var first = Directory.GetDirectories(soulsDirectory);
                if (first.Length > 0) return Path.GetFileName(first[0]);
            }
            return "default";
        }

        private static string InferSoulId(string soulsDirectory, string soulDirectory)
        {
            if (string.IsNullOrWhiteSpace(soulsDirectory) || string.IsNullOrWhiteSpace(soulDirectory))
                return null;
            var root = Path.GetFullPath(soulsDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var full = Path.GetFullPath(soulDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase)) return null;
            var prefix = root + Path.DirectorySeparatorChar;
            if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return full.Substring(prefix.Length).Replace('\\', '/');
            return null;
        }

        private static HomeFileData ReadFile(string root)
        {
            var path = Path.Combine(root, FileName);
            if (!File.Exists(path)) return new HomeFileData();
            try
            {
                var parsed = JsonSerializer.Deserialize<HomeFileData>(File.ReadAllText(path), JsonOptions);
                return parsed ?? new HomeFileData();
            }
            catch
            {
                return new HomeFileData();
            }
        }

        private static void WriteFile(
            string root,
            string activeSoul,
            string urls,
            string pluginsDirectory = null,
            string updateRepository = null)
        {
            Directory.CreateDirectory(root);
            var data = File.Exists(Path.Combine(root, FileName))
                ? ReadFile(root)
                : new HomeFileData();
            data.activeSoul = activeSoul ?? "";
            data.urls = string.IsNullOrWhiteSpace(urls) ? DefaultUrls : urls;
            if (pluginsDirectory != null) data.pluginsDirectory = pluginsDirectory.Trim();
            if (updateRepository != null) data.updateRepository = updateRepository.Trim();
            File.WriteAllText(Path.Combine(root, FileName), JsonSerializer.Serialize(data, JsonOptions));
        }

        private static string ResolveDirectory(string root, string configured, string fallbackName)
        {
            configured = string.IsNullOrWhiteSpace(configured) ? fallbackName : configured.Trim();
            var expanded = Environment.ExpandEnvironmentVariables(configured);
            return Path.GetFullPath(Path.IsPathRooted(expanded)
                ? expanded
                : Path.Combine(root, expanded));
        }

        private static string ReadEnv(string name)
        {
            var value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static string FirstNonEmpty(params string[] values)
        {
            if (values == null) return null;
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            }
            return null;
        }

        private sealed class HomeFileData
        {
            public string activeSoul { get; set; }
            public string urls { get; set; }
            public string pluginsDirectory { get; set; }
            public string updateRepository { get; set; }
        }
    }
}
