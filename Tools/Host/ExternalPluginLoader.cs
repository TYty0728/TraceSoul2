using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using TraceSoul2.Manager;
using TraceSoul2.Plugins;

namespace TraceSoul2.Host
{
    /// <summary>一个外部插件包：一个文件夹 = 一个包（plugin.json + 程序集）。</summary>
    public sealed class ExternalPluginPackage
    {
        public string Folder;
        public string Path;
        public string Id;
        public string AssemblyFile;
        public bool Loaded;
        public bool Enabled;
        public string DisplayName;
        public string Version;
        public string Error;
        internal AssemblyLoadContext Context;
    }

    /// <summary>
    /// 外部插件加载器（AstrBot 式插拔）：
    /// - 插件包目录在家目录 plugins/（TRACESOUL2_HOME / TRACESOUL2_PLUGINS）；
    /// - 每个包在自己的可回收 AssemblyLoadContext 里加载，卸载即释放，坏插件不影响宿主；
    /// - TraceSoul2.PluginApi 共享契约回落默认上下文，与宿主共用同一份类型；
    /// - 安装 = 丢一个文件夹进去；卸载 = 移到同级 plugins-uninstalled 以便恢复；重扫即时生效。
    /// </summary>
    public sealed class ExternalPluginLoader : IDisposable
    {
        public const string ApiAssemblyName = "TraceSoul2.PluginApi";

        private readonly string directory;
        private readonly List<ExternalPluginPackage> packages = new List<ExternalPluginPackage>();

        public string DirectoryPath { get { return directory; } }
        public string UninstalledDirectory
        {
            get
            {
                var parent = Path.GetDirectoryName(directory) ?? directory;
                return Path.Combine(parent, Path.GetFileName(directory) + "-uninstalled");
            }
        }

        public ExternalPluginLoader(string directory)
        {
            if (directory == null) throw new ArgumentNullException("directory");
            var normalized = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            this.directory = Path.GetFullPath(normalized.Length == 0 ? directory : normalized);
            Directory.CreateDirectory(this.directory);
        }

        private sealed class PluginLoadContext : AssemblyLoadContext
        {
            private readonly AssemblyDependencyResolver resolver;
            public PluginLoadContext(string dllPath) : base(isCollectible: true)
            {
                resolver = new AssemblyDependencyResolver(dllPath);
            }
            protected override Assembly Load(AssemblyName name)
            {
                // 共享契约：回落默认上下文，保证插件里的 ITracePlugin 等类型与宿主一致。
                if (string.Equals(name.Name, ApiAssemblyName, StringComparison.OrdinalIgnoreCase))
                    return null;
                var path = resolver.ResolveAssemblyToPath(name);
                return path == null ? null : LoadFromAssemblyPath(path);
            }
        }

        public List<ExternalPluginPackage> ScanAndLoad(TracePluginManager manager, Action<string> log)
        {
            UnloadAll(manager);
            foreach (var folder in Directory.GetDirectories(directory).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var package = new ExternalPluginPackage
                {
                    Folder = Path.GetFileName(folder),
                    Path = folder
                };
                packages.Add(package);
                PluginLoadContext context = null;
                var registered = false;
                try
                {
                    string dllName = null;
                    var manifestPath = Path.Combine(folder, "plugin.json");
                    if (File.Exists(manifestPath))
                    {
                        using (var doc = JsonDocument.Parse(File.ReadAllText(manifestPath)))
                        {
                            if (doc.RootElement.TryGetProperty("dll", out var dll)) dllName = dll.GetString();
                        }
                    }
                    if (string.IsNullOrWhiteSpace(dllName))
                        dllName = Directory.GetFiles(folder, "*.dll").Select(Path.GetFileName).FirstOrDefault();
                    if (string.IsNullOrWhiteSpace(dllName))
                        throw new InvalidOperationException("包里没有可加载的程序集（plugin.json 里用 dll 指定）。");

                    var dllPath = Path.Combine(folder, dllName);
                    package.AssemblyFile = dllName;
                    context = new PluginLoadContext(dllPath);
                    // 从内存流加载：不占用 dll 文件句柄，替换插件文件后重扫即可热更新。
                    var assemblyBytes = File.ReadAllBytes(dllPath);
                    Assembly assembly;
                    using (var stream = new MemoryStream(assemblyBytes))
                        assembly = context.LoadFromStream(stream);
                    var pluginType = SafeTypes(assembly).FirstOrDefault(x =>
                        x != null && !x.IsAbstract && !x.IsInterface &&
                        typeof(ITracePlugin).IsAssignableFrom(x));
                    if (pluginType == null)
                        throw new InvalidOperationException("程序集里没有 ITracePlugin 实现。");
                    var instance = (ITracePlugin)Activator.CreateInstance(pluginType);
                    package.Id = instance.Metadata.Id;
                    package.DisplayName = instance.Metadata.DisplayName;
                    package.Version = instance.Metadata.Version;
                    manager.RegisterExternal(instance, folder);
                    registered = true;
                    package.Loaded = true;
                    package.Enabled = instance.Metadata.Enabled;
                    package.Error = instance.Metadata.LoadError;
                    package.Context = context;
                    log?.Invoke("外部插件已加载：" + package.Id + "（" + package.Folder + "）");
                }
                catch (Exception exception)
                {
                    if (registered && !string.IsNullOrWhiteSpace(package.Id))
                    {
                        try { manager?.Unregister(package.Id); } catch { /* 注册未完成 */ }
                    }
                    try { context?.Unload(); } catch { /* 加载失败也释放上下文 */ }
                    package.Context = null;
                    package.Error = exception.Message;
                    log?.Invoke("外部插件加载失败：" + package.Folder + " / " + exception.Message);
                }
            }
            return packages;
        }

        public void UnloadAll(TracePluginManager manager)
        {
            foreach (var package in packages.Where(x => x.Loaded).ToList())
            {
                try { manager?.Unregister(package.Id); } catch { /* 已卸载 */ }
                try { package.Context?.Unload(); } catch { /* 释放失败不影响其它包 */ }
            }
            packages.Clear();
        }

        public string Uninstall(string folderOrId, TracePluginManager manager)
        {
            var package = packages.FirstOrDefault(x =>
                string.Equals(x.Folder, folderOrId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(x.Id, folderOrId, StringComparison.OrdinalIgnoreCase));
            if (package == null) return null;
            if (package.Loaded)
            {
                try { manager?.Unregister(package.Id); } catch { /* 已卸载 */ }
                try { package.Context?.Unload(); } catch { /* 忽略 */ }
            }
            package.Loaded = false;
            package.Enabled = false;

            if (!Directory.Exists(package.Path))
            {
                packages.Remove(package);
                return string.Empty;
            }
            var quarantine = UninstalledDirectory;
            Directory.CreateDirectory(quarantine);
            var suffix = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
            var destination = Path.Combine(quarantine, package.Folder + "-" + suffix);
            var counter = 1;
            while (Directory.Exists(destination))
                destination = Path.Combine(quarantine, package.Folder + "-" + suffix + "-" + counter++);
            try
            {
                Directory.Move(package.Path, destination);
                packages.Remove(package);
            }
            catch (Exception exception)
            {
                package.Error = "插件已停止，但移动到可恢复区失败：" + exception.Message;
                throw;
            }
            return destination;
        }

        public void SetEnabled(string pluginId, bool enabled)
        {
            var package = packages.FirstOrDefault(x =>
                string.Equals(x.Id, pluginId, StringComparison.OrdinalIgnoreCase));
            if (package != null)
            {
                package.Enabled = enabled;
                if (enabled) package.Error = string.Empty;
            }
        }

        public ExternalPluginPackage Find(string folderOrId)
        {
            return packages.FirstOrDefault(x =>
                string.Equals(x.Folder, folderOrId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(x.Id, folderOrId, StringComparison.OrdinalIgnoreCase));
        }

        public object Status()
        {
            return new
            {
                directory,
                uninstalledDirectory = UninstalledDirectory,
                packages = packages.Select(x => new
                {
                    x.Folder,
                    x.Path,
                    x.Id,
                    x.AssemblyFile,
                    x.Loaded,
                    x.Enabled,
                    x.DisplayName,
                    x.Version,
                    x.Error
                }).ToList()
            };
        }

        public void Dispose()
        {
            UnloadAll(null);
        }

        private static IEnumerable<Type> SafeTypes(Assembly assembly)
        {
            try { return assembly.GetTypes(); }
            catch (ReflectionTypeLoadException exception) { return exception.Types.Where(x => x != null); }
            catch { return new Type[0]; }
        }
    }
}
