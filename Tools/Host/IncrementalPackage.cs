using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TraceSoul2.Host
{
    // Materialize a complete new directory; never patch files in the live installation.
    internal static class IncrementalPackage
    {
        internal const string InventoryName = "tracesoul2.files.json";
        private const string DeltaName = "tracesoul2.delta.json";
        internal sealed class Inventory
        {
            public int schema { get; set; }
            public string product { get; set; }
            public string version { get; set; }
            public string runtime { get; set; }
            public List<Entry> files { get; set; }
        }
        internal sealed class Entry
        {
            public string path { get; set; }
            public long size { get; set; }
            public string sha256 { get; set; }
        }
        private static string SafePath(string root, string relative)
        {
            if (string.IsNullOrEmpty(relative) || relative.Length > 512 ||
                relative.Any(c => c < 32 || "\\:*?\"<>|".Contains(c)) ||
                relative.Split('/').Any(p => p.Length == 0 || p == "." || p == ".." || p.EndsWith('.') || p.EndsWith(' ') ||
                    Regex.IsMatch(p, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase)))
                throw new InvalidDataException("增量包包含无效文件路径。");
            root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("增量文件越界。");
            // Never reuse files through a symlink/junction leading outside App.
            for (var part = path; part.Length >= root.Length; part = Path.GetDirectoryName(part))
                if ((File.Exists(part) || Directory.Exists(part)) && (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("增量文件路径包含符号链接。");
            return path;
        }
        private static byte[] ReadMetadata(ZipArchiveEntry entry)
        {
            if (entry == null || entry.Length > 8 * 1024 * 1024) throw new InvalidDataException("增量包缺少有效清单。");
            using var stream = entry.Open();
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        }
        internal static async Task PrepareAsync(string zipPath, string installed, string destination,
            string baseVersion, string targetVersion, string runtime, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
                throw new InvalidDataException("增量更新目标目录必须为空。");
            if ((File.GetAttributes(installed) & FileAttributes.ReparsePoint) != 0 ||
                (Directory.Exists(destination) && (File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0))
                throw new InvalidDataException("安装目录是符号链接，使用完整包更新。");
            using var current = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(installed, "tracesoul2.install.json"), token));
            if (current.RootElement.GetProperty("product").GetString() != "TraceSoul2" ||
                current.RootElement.GetProperty("version").GetString() != baseVersion ||
                current.RootElement.GetProperty("runtime").GetString() != runtime)
                throw new InvalidDataException("本地安装版本与增量基线不符。");
            using var archive = ZipFile.OpenRead(zipPath);
            if (archive.Entries.Count > 20002) throw new InvalidDataException("增量包文件过多。");
            var zipEntries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in archive.Entries)
            {
                SafePath(destination, item.FullName);
                if ((item.ExternalAttributes >> 16 & 0xF000) == 0xA000 || !zipEntries.TryAdd(item.FullName, item))
                    throw new InvalidDataException("增量包包含符号链接或重复路径。");
            }
            zipEntries.TryGetValue(InventoryName, out var inventoryEntry);
            zipEntries.TryGetValue(DeltaName, out var deltaEntry);
            var raw = ReadMetadata(inventoryEntry);
            var manifest = JsonSerializer.Deserialize<Inventory>(raw);
            using var delta = JsonDocument.Parse(ReadMetadata(deltaEntry));
            var header = delta.RootElement;
            if (manifest == null || manifest.schema != 1 || manifest.product != "TraceSoul2" ||
                manifest.version != targetVersion || manifest.runtime != runtime ||
                header.GetProperty("schema").GetInt32() != 1 || header.GetProperty("product").GetString() != "TraceSoul2" ||
                header.GetProperty("version").GetString() != targetVersion || header.GetProperty("baseVersion").GetString() != baseVersion ||
                header.GetProperty("runtime").GetString() != runtime || manifest.files == null || manifest.files.Count == 0 || manifest.files.Count > 20000)
                throw new InvalidDataException("增量包版本、平台或文件清单不匹配。");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { InventoryName, DeltaName };
            long total = 0;
            foreach (var file in manifest.files)
            {
                if (file == null) throw new InvalidDataException("增量文件清单为空。");
                SafePath(destination, file.path);
                if (!names.Add(file.path) || file.size < 0 || file.size > ReleaseDownloader.MaxPackageBytes ||
                    !Regex.IsMatch(file.sha256 ?? "", "^[a-f0-9]{64}$") || (total += file.size) > 4L * 1024 * 1024 * 1024)
                    throw new InvalidDataException("增量文件清单无效。");
            }
            if (zipEntries.Keys.Any(name => !names.Contains(name))) throw new InvalidDataException("增量包包含清单外文件。");
            Directory.CreateDirectory(destination);
            foreach (var file in manifest.files)
            {
                token.ThrowIfCancellationRequested();
                var target = SafePath(destination, file.path);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                if (zipEntries.TryGetValue(file.path, out var entry))
                {
                    if (entry.Length != file.size) throw new InvalidDataException("增量文件长度不符。");
                    using var input = entry.Open();
                    using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    await input.CopyToAsync(output, token);
                }
                else
                {
                    var source = SafePath(installed, file.path);
                    if (!File.Exists(source) || new FileInfo(source).Length != file.size)
                        throw new InvalidDataException("本地可复用文件缺失或已改变。");
                    File.Copy(source, target, false);
                }
                using var check = File.OpenRead(target);
                if (!string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(check, token)), file.sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("增量文件或本地复用文件校验失败。");
            }
            await File.WriteAllBytesAsync(Path.Combine(destination, InventoryName), raw, token);
        }
    }
}
