using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace TraceSoul2.ExternalPlugins
{
    internal sealed class ReferenceImageData
    {
        public string Category { get; set; }
        public string Role { get; set; }
        public string FileName { get; set; }
        public byte[] Bytes { get; set; }
        public string MimeType { get; set; }
    }

    internal sealed class ReferenceLibrary
    {
        private readonly string directory;
        private readonly Dictionary<string, List<string>> index =
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> roles =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public ReferenceLibrary(string pluginDataDirectory)
        {
            directory = Path.Combine(pluginDataDirectory ?? string.Empty, "ref_images");
            Reload();
        }

        public IReadOnlyList<string> Categories => index.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList();

        public void Reload()
        {
            index.Clear();
            roles.Clear();
            Directory.CreateDirectory(directory);
            LoadRoles(Path.Combine(directory, "roles.json"));
            LoadIndex(Path.Combine(directory, "index.json"));
            DiscoverLooseFiles();
        }

        public string Describe()
        {
            if (index.Count == 0) return "当前没有参考图库。";
            return string.Join("；", index.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x =>
                x.Key + "（" + ResolveRole(x.Key) + "，" + x.Value.Count + "张）"));
        }

        public List<ReferenceImageData> Resolve(
            IEnumerable<string> requestedCategories,
            bool needsCharacter,
            int maxPerCategory,
            int maxTotal)
        {
            var requested = (requestedCategories ?? Enumerable.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (needsCharacter && !requested.Any(IsCharacterCategory))
            {
                var characters = index.Keys.Where(IsCharacterCategory).ToList();
                if (characters.Count == 1) requested.Insert(0, characters[0]);
            }
            var result = new List<ReferenceImageData>();
            foreach (var category in requested)
            {
                if (!index.TryGetValue(category, out var files)) continue;
                foreach (var fileName in files.Take(Math.Max(1, maxPerCategory)))
                {
                    var path = Path.Combine(directory, Path.GetFileName(fileName));
                    if (!File.Exists(path)) continue;
                    var bytes = File.ReadAllBytes(path);
                    if (bytes.Length < 100) continue;
                    result.Add(new ReferenceImageData
                    {
                        Category = category,
                        Role = ResolveRole(category),
                        FileName = Path.GetFileName(path),
                        Bytes = bytes,
                        MimeType = MimeOf(path, bytes)
                    });
                    if (result.Count >= Math.Max(1, maxTotal)) return result;
                }
            }
            return result;
        }

        public bool IsCharacterCategory(string category)
        {
            return string.Equals(ResolveRole(category), "角色", StringComparison.OrdinalIgnoreCase);
        }

        public string ResolveRole(string category)
        {
            return !string.IsNullOrWhiteSpace(category) && roles.TryGetValue(category, out var role) &&
                   !string.IsNullOrWhiteSpace(role) ? role : "辅助";
        }

        private void LoadRoles(string path)
        {
            if (!File.Exists(path)) return;
            try
            {
                using (var doc = JsonDocument.Parse(File.ReadAllText(path)))
                    foreach (var item in doc.RootElement.EnumerateObject())
                        if (item.Value.ValueKind == JsonValueKind.String)
                            roles[item.Name] = item.Value.GetString();
            }
            catch { }
        }

        private void LoadIndex(string path)
        {
            if (!File.Exists(path)) return;
            try
            {
                using (var doc = JsonDocument.Parse(File.ReadAllText(path)))
                {
                    foreach (var category in doc.RootElement.EnumerateObject())
                    {
                        if (category.Value.ValueKind != JsonValueKind.Array) continue;
                        var files = new List<string>();
                        foreach (var item in category.Value.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.String)
                            {
                                files.Add(Path.GetFileName(item.GetString()));
                                continue;
                            }
                            if (item.ValueKind == JsonValueKind.Object &&
                                item.TryGetProperty("filename", out var filename) &&
                                filename.ValueKind == JsonValueKind.String)
                                files.Add(Path.GetFileName(filename.GetString()));
                        }
                        if (files.Count > 0) index[category.Name] = files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    }
                }
            }
            catch { }
        }

        private void DiscoverLooseFiles()
        {
            foreach (var path in Directory.GetFiles(directory))
            {
                var extension = Path.GetExtension(path).ToLowerInvariant();
                if (extension != ".png" && extension != ".jpg" && extension != ".jpeg" &&
                    extension != ".webp" && extension != ".gif") continue;
                var fileName = Path.GetFileName(path);
                if (index.Values.Any(x => x.Contains(fileName, StringComparer.OrdinalIgnoreCase))) continue;
                var category = CategoryFromFileName(fileName);
                if (!index.TryGetValue(category, out var files)) index[category] = files = new List<string>();
                files.Add(fileName);
            }
        }

        private static string CategoryFromFileName(string fileName)
        {
            var stem = Path.GetFileNameWithoutExtension(fileName) ?? "未分类";
            if (stem.StartsWith("ref_", StringComparison.OrdinalIgnoreCase)) stem = stem.Substring(4);
            var last = stem.LastIndexOf('_');
            if (last > 0) stem = stem.Substring(0, last);
            return string.IsNullOrWhiteSpace(stem) ? "未分类" : stem;
        }

        internal static string MimeOf(string path, byte[] bytes = null)
        {
            var extension = Path.GetExtension(path ?? string.Empty).ToLowerInvariant();
            if (extension == ".jpg" || extension == ".jpeg") return "image/jpeg";
            if (extension == ".webp") return "image/webp";
            if (extension == ".gif") return "image/gif";
            if (bytes != null && bytes.Length > 3 && bytes[0] == 0xff && bytes[1] == 0xd8) return "image/jpeg";
            return "image/png";
        }
    }
}
