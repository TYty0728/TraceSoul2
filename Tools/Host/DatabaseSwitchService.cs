using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TraceSoul2.Host
{
    /// <summary>
    /// 控制台「数据库切换」：枚举数据目录、切换、新建。
    /// 数据目录按「同层目录里存在 tracesoul2-brainframe.sqlite3」判定为已初始化数据库。
    /// </summary>
    public sealed class DatabaseSwitchService
    {
        private readonly string root;
        private readonly string current;

        public DatabaseSwitchService(string dataDirectory, string soulsDirectory = null)
        {
            current = Path.GetFullPath(dataDirectory);
            root = string.IsNullOrWhiteSpace(soulsDirectory)
                ? (Path.GetDirectoryName(current) ?? current)
                : Path.GetFullPath(soulsDirectory);
        }

        public string Current { get { return current; } }

        /// <summary>枚举根目录下的全部数据库目录（当前库排第一，其余按名字）。</summary>
        public object List()
        {
            var dirs = new List<DatabaseEntry>();
            if (Directory.Exists(root))
            {
                foreach (var full in Directory.GetDirectories(root))
                {
                    var entry = Describe(Path.GetFileName(full), full);
                    if (entry != null) dirs.Add(entry);
                }
            }
            if (dirs.All(x => !x.IsCurrent) && Directory.Exists(current))
                dirs.Add(Describe(Path.GetFileName(current), current));
            dirs.Sort((a, b) =>
            {
                if (a.IsCurrent != b.IsCurrent) return a.IsCurrent ? -1 : 1;
                return string.Compare(a.Name, b.Name, StringComparison.Ordinal);
            });
            return new { root, current, databases = dirs };
        }

        /// <summary>校验切换目标：目录存在、已初始化、不含会触发清理的调试标记。返回规范化的完整路径。</summary>
        public string ResolveForSwitch(string nameOrPath)
        {
            var raw = (nameOrPath ?? string.Empty).Trim();
            if (raw.Length == 0) throw new InvalidOperationException("没有指定数据库。");
            var full = Path.IsPathRooted(raw)
                ? Path.GetFullPath(raw)
                : Path.GetFullPath(Path.Combine(root, raw));
            if (!Directory.Exists(full))
                throw new InvalidOperationException("目录不存在：" + full);
            var entry = Describe(Path.GetFileName(full), full);
            if (entry != null && entry.Blocked)
                throw new InvalidOperationException(entry.BlockedReason);
            if (!File.Exists(Path.Combine(full, "tracesoul2-brainframe.sqlite3")))
                throw new InvalidOperationException("不是已初始化的数据目录（缺少 tracesoul2-brainframe.sqlite3）：" + full);
            return full;
        }

        /// <summary>在根目录下新建一个空数据库目录（不复制任何旧配置，真正从零开始）。</summary>
        public string Create(string rawName)
        {
            var name = Sanitize(rawName);
            if (name.Length == 0) throw new InvalidOperationException("数据库名字不能为空。");
            var full = Path.Combine(root, name);
            if (Directory.Exists(full))
                throw new InvalidOperationException("这个数据库已存在：" + name);
            Directory.CreateDirectory(full);
            return full;
        }

        private DatabaseEntry Describe(string name, string full)
        {
            var brain = Path.Combine(full, "tracesoul2-brainframe.sqlite3");
            var isCurrent = string.Equals(full, current, StringComparison.OrdinalIgnoreCase);
            if (!File.Exists(brain) && !isCurrent) return null;
            var hasDebugData = File.Exists(Path.Combine(full, "debug-mode.json")) &&
                               Directory.Exists(Path.Combine(full, "debug-active"));
            var vectors = Path.Combine(full, "tracesoul2-vectors.sqlite3");
            var lastWrite = DateTime.MinValue;
            foreach (var file in new[] { brain, vectors })
            {
                if (File.Exists(file))
                {
                    var t = File.GetLastWriteTimeUtc(file);
                    if (t > lastWrite) lastWrite = t;
                }
            }
            return new DatabaseEntry
            {
                Name = name,
                Path = full,
                IsCurrent = isCurrent,
                BrainBytes = File.Exists(brain) ? new FileInfo(brain).Length : 0,
                LastWrite = lastWrite == DateTime.MinValue ? (DateTime?)null : lastWrite,
                HasOnebot = File.Exists(Path.Combine(full, "onebot.json")),
                HasApiKey = HasProviderKey(full),
                Blocked = hasDebugData,
                BlockedReason = hasDebugData
                    ? "含调试模式标记（debug-mode.json + debug-active），启动会清空调试分支，为保护昨天的调试数据已禁止切换。"
                    : null
            };
        }

        private static bool HasProviderKey(string dir)
        {
            var providers = Path.Combine(dir, "llm-providers.json");
            if (!File.Exists(providers)) return false;
            try
            {
                var text = File.ReadAllText(providers);
                return text.IndexOf("\"apiKey\"", StringComparison.Ordinal) >= 0 &&
                       !text.Contains("\"apiKey\": \"\"") &&
                       !text.Contains("\"apiKey\": null");
            }
            catch
            {
                return false;
            }
        }

        private static string Sanitize(string raw)
        {
            var name = (raw ?? string.Empty).Trim();
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Trim().TrimEnd('.');
        }
    }

    public sealed class DatabaseEntry
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public bool IsCurrent { get; set; }
        public long BrainBytes { get; set; }
        public DateTime? LastWrite { get; set; }
        public bool HasOnebot { get; set; }
        public bool HasApiKey { get; set; }
        public bool Blocked { get; set; }
        public string BlockedReason { get; set; }
    }
}
