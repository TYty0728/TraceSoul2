using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TraceSoul2.Host
{
    /// <summary>Runs before TraceHome creates directories or any SQLite/plugin service opens files.</summary>
    public static class RuntimePathRepair
    {
        public const string ReportName = "runtime-paths.json";
        private const string HomeHintName = ".tracesoul-home-paths.json";
        private static readonly StringComparison Comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        private static readonly JsonSerializerOptions Json = BackupArchiveFormat.Json;

        public static RuntimePathReport Check(string defaultHome, string applicationDirectory, IDictionary<string, string> environment = null)
        {
            string Read(string name) => environment == null ? Environment.GetEnvironmentVariable(name) : environment.TryGetValue(name, out var value) ? value : null;
            void Set(string name, string value) { if (environment == null) Environment.SetEnvironmentVariable(name, value); else environment[name] = value; }
            var app = Full(applicationDirectory); var parent = Path.GetDirectoryName(app);
            var homeInput = Read("TRACESOUL2_HOME");
            var legacy = string.IsNullOrWhiteSpace(homeInput) && !string.IsNullOrWhiteSpace(Read("TRACESOUL2_DATA"));
            var legacyPluginDefaults = legacy;
            // Only-DATA is an explicitly supported old layout. Do not reinterpret its parent as a new home.
            var rawHome = !string.IsNullOrWhiteSpace(homeInput) ? homeInput : legacy ? Path.GetDirectoryName(Full(Read("TRACESOUL2_DATA"))) : defaultHome;
            var hintPath = Path.Combine(parent, HomeHintName);
            var hints = File.Exists(hintPath) ? ReadJson<Dictionary<string, string>>(hintPath) : new Dictionary<string, string>();
            var rawResolved = Resolve(rawHome, parent);
            if (rawResolved != null && TryHomeRedirect(rawResolved, out var pointer)) hints[rawHome] = pointer;
            var redirectedHome = Alias(hints, rawHome, parent);
            var home = Resolve(redirectedHome, parent);
            if (home == null || !Directory.Exists(home) && InsideApplication(home, app))
            {
                var candidates = new[] { Path.Combine(parent, "Data") }
                    .Concat(OldApplications(app).Select(old => Path.Combine(old, "Data")))
                    .Where(HasHome).Distinct(PathComparer()).ToList();
                if (candidates.Count > 1) throw new IOException("发现多份家目录，已停止自动整理以保留全部资料。");
                if (candidates.Count == 1 && !legacy) { home = Full(candidates[0]); hints[rawHome] = home; }
                else if (home == null) throw new IOException("家目录是其他系统的路径，且未找到唯一的本地资料。");
            }
            // A new installation has no data to move; normal scaffolding remains TraceHome's job.
            if (!Directory.Exists(home)) return new RuntimePathReport();
            BackupArchiveService.RejectLinks(home);
            if (Equal(home, app))
            {
                if (!legacy) throw new IOException("家目录与程序文件混在同一目录，无法可靠区分资料，已停止自动整理。");
                // The parent of an explicit old DATA directory is not itself a home to move.
                home = Path.Combine(parent, "Data"); Set("TRACESOUL2_HOME", home);
            }

            var report = ReadReport(home);
            ValidateRetainedSources(report);
            report.Changed = () => SavePrivate(Path.Combine(home, ReportName), JsonSerializer.Serialize(report, Json));
            var oldHome = home;
            if (InsideApplication(home, app))
            {
                home = Path.Combine(parent, "Data");
                CopyVerified(oldHome, home, "home", report);
                hints[rawHome] = home;
            }
            if (!Equal(oldHome, home) || !string.Equals(rawHome, home, Comparison))
            { Set("TRACESOUL2_HOME", home); legacy = false; }
            var configPath = Path.Combine(home, "home.json");
            var config = File.Exists(configPath) ? JsonNode.Parse(File.ReadAllText(configPath))?.AsObject() : new JsonObject();
            if (config == null) throw new IOException("家目录配置无法读取，已保留原文件。");
            var changed = false;
            var active = String(config, "activeSoul");
            var dataInput = Read("TRACESOUL2_DATA");
            var id = SafeId(!string.IsNullOrWhiteSpace(dataInput) ? dataInput : active);
            var established = File.Exists(Path.Combine(home, "control-auth.json")) ||
                Directory.Exists(Path.Combine(home, "souls")) && Directory.GetDirectories(Path.Combine(home, "souls")).Any(HasSoul);
            var soulInput = !string.IsNullOrWhiteSpace(dataInput) ? dataInput : !string.IsNullOrWhiteSpace(active) ? Path.Combine(home, "souls", active) : null;
            var currentSoul = Resolve(Alias(report.Aliases, soulInput, home, report), home);
            currentSoul = RedirectChild(currentSoul, oldHome, home);
            if (!string.IsNullOrWhiteSpace(soulInput))
            {
                var canonical = Path.Combine(home, "souls", id);
                var roleCandidates = new[] { canonical, Path.Combine(home, id), Path.Combine(app, "souls", id), Path.Combine(app, "Data", "souls", id) }
                    .Concat(OldApplications(app).SelectMany(old => new[] { Path.Combine(old, "souls", id), Path.Combine(old, "Data", "souls", id) }));
                var actual = Repair("soul", soulInput, currentSoul, canonical, roleCandidates, HasSoul, report, app, established);
                if (actual != null)
                {
                    if (!Equal(actual, currentSoul) || !string.IsNullOrWhiteSpace(dataInput)) Set("TRACESOUL2_DATA", actual);
                    var relative = Path.GetRelativePath(Path.Combine(home, "souls"), actual).Replace('\\', '/');
                    if (!relative.StartsWith("..", StringComparison.Ordinal) && active != relative) { config["activeSoul"] = relative; changed = true; }
                }
            }
            var pluginDefault = legacyPluginDefaults ? Path.Combine(defaultHome, "plugins") : Path.Combine(home, "plugins");
            var pluginsInput = First(Read("TRACESOUL2_PLUGINS"), legacyPluginDefaults ? null : String(config, "pluginsDirectory"), pluginDefault);
            var plugins = RedirectChild(Resolve(Alias(report.Aliases, pluginsInput, home, report), home), oldHome, home);
            var pluginCandidates = new[] { pluginDefault, Path.Combine(parent, "Plugins"), Path.Combine(app, "plugins"), Path.Combine(app, "Plugins") }
                .Concat(OldApplications(app).SelectMany(old => new[] { Path.Combine(old, "plugins"), Path.Combine(old, "Plugins") }));
            var actualPlugins = Repair("plugins", pluginsInput, plugins, pluginDefault, pluginCandidates, HasPlugins, report, app,
                established && (!string.IsNullOrWhiteSpace(Read("TRACESOUL2_PLUGINS")) || !string.IsNullOrWhiteSpace(String(config,"pluginsDirectory")))) ?? plugins;
            if (!Equal(plugins, actualPlugins) || !Equal(plugins, Resolve(pluginsInput, home))) changed = true;
            if (actualPlugins == null) throw new IOException("插件目录路径无法解析。");
            Set("TRACESOUL2_PLUGINS", actualPlugins);
            if (changed || report.Aliases.ContainsKey(pluginsInput)) { config["pluginsDirectory"] = Portable(home, actualPlugins); changed = true; }

            var dataDefault = Path.Combine(Path.GetDirectoryName(actualPlugins), "plugins_data");
            var pluginDataInput = First(Read("TRACESOUL2_PLUGINS_DATA"), legacyPluginDefaults ? null : String(config, "pluginsDataDirectory"), dataDefault);
            var pluginData = RedirectChild(Resolve(Alias(report.Aliases, pluginDataInput, home, report), home), oldHome, home);
            var dataCandidates = new[] { dataDefault, Path.Combine(home, "plugins_data"), Path.Combine(home, "plugin-data"), Path.Combine(parent, "plugins_data"),
                    Path.Combine(app, "plugins_data"), Path.Combine(app, "plugin-data") }
                .Concat(OldApplications(app).SelectMany(old => new[] { Path.Combine(old, "plugins_data"), Path.Combine(old, "plugin-data") }));
            var actualData = Repair("plugins_data", pluginDataInput, pluginData, dataDefault, dataCandidates, HasMaterial, report, app,
                established && (!string.IsNullOrWhiteSpace(Read("TRACESOUL2_PLUGINS_DATA")) || !string.IsNullOrWhiteSpace(String(config,"pluginsDataDirectory")))) ?? pluginData;
            if (actualData == null) throw new IOException("插件资料路径无法解析。");
            if (!Equal(pluginData, actualData) || !Equal(pluginData, Resolve(pluginDataInput, home)) || report.Aliases.ContainsKey(pluginDataInput)) { config["pluginsDataDirectory"] = Portable(home, actualData); changed = true; }
            Set("TRACESOUL2_PLUGINS_DATA", actualData);

            // A home relocation changes paths in these two configuration files only; never rewrite credentials or dialogue.
            var archivePath = Path.Combine(home, "archive-settings.json");
            if (File.Exists(archivePath))
            {
                var archive = JsonNode.Parse(File.ReadAllText(archivePath))?.AsObject() ?? throw new IOException("存档计划无法读取。");
                var raw = String(archive, "directory");
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    var location = RedirectChild(Resolve(Alias(report.Aliases, raw, home, report), home), oldHome, home);
                    var target = Path.Combine(home, "archives");
                    var unsafeLocation = location == null || InsideApplication(location, app) ||
                        BackupArchiveService.Inside(location, Path.Combine(home, "souls")) || BackupArchiveService.Inside(location, actualPlugins) || BackupArchiveService.Inside(location, actualData);
                    if (unsafeLocation)
                    {
                        if (location != null && HasArchives(location)) CopyVerified(location, target, "archives", report);
                        else if (location != null && HasMaterial(location)) throw new IOException("存档目录与其他资料混在一起，已保留现场，不能自动覆盖。");
                        else if (location != null && BackupArchiveService.Inside(location, app))
                        {
                            var prior = OldApplications(app).Select(old => Path.Combine(old, Path.GetRelativePath(app, location))).Where(HasArchives).ToList();
                            if (prior.Count == 1) CopyVerified(prior[0], target, "archives", report);
                        }
                        Remember(report, raw, target); location = target;
                    }
                    if (!Equal(location, Resolve(raw, home))) { archive["directory"] = Portable(home, location); UpdateConfig(archivePath, archive.ToJsonString(Json), home, report); }
                }
            }
            if (changed) UpdateConfig(configPath, config.ToJsonString(Json), home, report);
            RewritePathSettings(home, report);
            report.CheckedUtc = DateTimeOffset.UtcNow;
            try { SavePrivate(Path.Combine(home, ReportName), JsonSerializer.Serialize(report, Json)); }
            catch when (report.Moves.Count == 0 && report.Aliases.Count == 0) { /* A read-only, otherwise valid layout requires no mutation. */ }
            if (hints.Count > 0) SavePrivate(hintPath, JsonSerializer.Serialize(hints, Json));
            return report;
        }

        private static void RewritePathSettings(string home, RuntimePathReport report)
        {
            if (report.Aliases.Count == 0) return;
            var roots = report.Moves.Where(m => m.Kind != "archives").SelectMany(m => m.Kind == "home"
                ? new[] { Path.Combine(m.Destination, "souls"), Path.Combine(m.Destination, "plugins"), Path.Combine(m.Destination, "plugins_data") }
                : new[] { m.Destination }).Where(Directory.Exists).Distinct(PathComparer()).ToList();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "config.json", "onebot.json", "speech.json", "wake.json", "runtime-settings.json" };
            var aliases = report.Aliases.Where(p => Path.IsPathFullyQualified(p.Key) || Regex.IsMatch(p.Key, @"^[a-zA-Z]:[\\/]"))
                .OrderByDescending(p => p.Key.Length).ToList();
            var seen = new HashSet<string>(PathComparer());
            foreach (var root in roots)
            foreach (var file in SettingsFiles(root, names))
            {
                if (!seen.Add(file) || file.Split(Path.DirectorySeparatorChar).Any(p => p.Contains(".tracesoul-moved-") || p == "backups" || p == "updates" || p == ".path-repair-configs")) continue;
                BackupArchiveService.RejectLinks(file);
                var original = File.ReadAllText(file);
                var document = JsonNode.Parse(original);
                var changed = false;
                void Walk(JsonNode node, string field)
                {
                    if (node is JsonObject obj)
                    {
                        foreach (var key in obj.Select(p => p.Key).ToList())
                        {
                            var value = obj[key];
                            if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text) && IsPathField(key))
                            {
                                var updated = Rewrite(text); if (updated != text) { obj[key] = updated; changed = true; }
                            }
                            else Walk(value, key);
                        }
                    }
                    else if (node is JsonArray array)
                        for (var i = 0; i < array.Count; i++)
                        {
                            if (array[i] is JsonValue scalar && scalar.TryGetValue<string>(out var text) && IsPathField(field))
                            { var updated = Rewrite(text); if (updated != text) { array[i] = updated; changed = true; } }
                            else Walk(array[i], field);
                        }
                }
                string Rewrite(string text)
                {
                    if (string.IsNullOrWhiteSpace(text)) return text;
                    foreach (var alias in aliases)
                    {
                        var value = text.Replace('\\', '/'); var prefix = alias.Key.Replace('\\', '/').TrimEnd('/');
                        var compare = Regex.IsMatch(prefix, @"^[a-zA-Z]:/") ? StringComparison.OrdinalIgnoreCase : Comparison;
                        if (!value.Equals(prefix, compare) && !value.StartsWith(prefix + "/", compare)) continue;
                        var tail = value.Substring(prefix.Length).TrimStart('/');
                        if (tail.Split('/').Any(p => p == ".." || p == ".")) continue;
                        return tail.Length == 0 ? alias.Value : Path.Combine(alias.Value, tail.Replace('/', Path.DirectorySeparatorChar));
                    }
                    return text;
                }
                Walk(document, "");
                if (!changed) continue;
                UpdateConfig(file, document.ToJsonString(Json), home, report);
            }
        }

        private static IEnumerable<string> SettingsFiles(string root, HashSet<string> names)
        {
            BackupArchiveService.RejectLinks(root);
            foreach (var entry in Directory.EnumerateFileSystemEntries(root))
            {
                var name = Path.GetFileName(entry);
                if (name.Contains(".tracesoul-moved-") || name == "backups" || name == "updates" || name == ".path-repair-configs") continue;
                BackupArchiveService.RejectLinks(entry);
                if (Directory.Exists(entry)) { foreach (var file in SettingsFiles(entry, names)) yield return file; }
                else if (names.Contains(name)) yield return entry;
            }
        }

        private static void UpdateConfig(string path, string text, string home, RuntimePathReport report)
        {
            if (File.Exists(path))
            {
                var original = File.ReadAllText(path); if (original == text) return;
                var backup = Path.Combine(home, ".path-repair-configs", Guid.NewGuid().ToString("N"), Path.GetFileName(path) + ".before");
                Directory.CreateDirectory(Path.GetDirectoryName(backup)); BackupArchiveService.PrivateDirectory(Path.GetDirectoryName(backup));
                SavePrivate(backup, original); report.ConfigBackups[path] = backup;
            }
            SavePrivate(path, text); report.Changed?.Invoke();
        }

        private static bool IsPathField(string name) => Regex.IsMatch(name ?? "", @"(path|directory|folder|_dir|_file)$|^(file|reference_images?|reference_audio)$", RegexOptions.IgnoreCase);

        private static string Repair(string kind, string raw, string configured, string canonical, IEnumerable<string> candidates,
            Func<string, bool> valid, RuntimePathReport report, string app, bool established = false)
        {
            if (configured != null && Directory.Exists(configured))
            {
                BackupArchiveService.RejectLinks(configured);
                if (!InsideApplication(configured, app)) return configured; // custom directories and mounted volumes are valid
                if (Equal(configured, app)) throw new IOException("运行资料与程序混在同一目录，已停止自动整理。");
                CopyVerified(configured, canonical, kind, report); Remember(report, raw, canonical); return canonical;
            }
            var found = candidates.Where(valid).Select(Full).Distinct(PathComparer()).ToList();
            if (found.Count > 1) throw new IOException("发现多份" + kind + "资料，已停止自动整理，未覆盖任何一份。");
            if (found.Count == 1)
            {
                var source = found[0];
                if (InsideApplication(source, app) || !Equal(source, canonical)) CopyVerified(source, canonical, kind, report);
                Remember(report, raw, canonical); return canonical;
            }
            if (configured == null) throw new IOException("旧机器路径无法使用，且没有找到可确认的本地资料。");
            if (established) throw new IOException("已有实例指定的" + kind + "目录丢失，且未找到可确认的资料；已停止，未创建替代空目录。");
            return configured;
        }

        // Copy -> inventory/hash verification -> publish. The source stays at its original path, byte for byte.
        // Cleanup is a separate, explicitly confirmed operation, never a startup side effect.
        public static void CopyVerified(string source, string target, string kind, RuntimePathReport report)
        {
            source = Full(source); target = Full(target);
            if (Equal(source, target)) return;
            BackupArchiveService.RejectLinks(source); BackupArchiveService.RejectLinks(target);
            if (BackupArchiveService.Inside(target, source) || BackupArchiveService.Inside(source, target))
                throw new IOException("迁移来源与目标相互包含，已拒绝。");
            var inventory = Inventory(source);
            var token = Guid.NewGuid().ToString("N");
            var stage = target + ".tracesoul-stage-" + token;
            string empty = null;
            try
            {
                if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
                {
                    if (!Same(inventory, Inventory(target))) throw new IOException("迁移目标已有不同资料，已保留两边。");
                }
                else
                {
                    Directory.CreateDirectory(stage); BackupArchiveService.PrivateDirectory(stage);
                    foreach (var item in inventory)
                    {
                        var destination = Path.Combine(stage, item.Key);
                        if (item.Value == "<directory>") Directory.CreateDirectory(destination);
                        else { Directory.CreateDirectory(Path.GetDirectoryName(destination)); File.Copy(Path.Combine(source, item.Key), destination); }
                    }
                    if (!Same(inventory, Inventory(stage)) || !Same(inventory, Inventory(source))) throw new IOException("迁移期间文件变化或校验不一致。");
                    if (Directory.Exists(target)) { empty = target + ".tracesoul-empty-" + token; Directory.Move(target, empty); }
                    Directory.Move(stage, target);
                }
                if (!Same(inventory, Inventory(source))) throw new IOException("原资料仍在变化，已保留原目录和副本。");
                report.Moves.Add(new RuntimePathMove { Kind = kind, Source = source, Destination = target, OriginalBackup = source,
                    SourceFingerprint = Fingerprint(inventory), CopyVerifiedUtc = DateTimeOffset.UtcNow });
                Remember(report, source, target);
            }
            finally
            {
                if (Directory.Exists(stage))
                {
                    if (!BackupArchiveService.Inside(stage, Path.GetDirectoryName(target))) throw new IOException("迁移暂存目录越界，未清理。");
                    BackupArchiveService.RejectLinks(stage); Directory.Delete(stage, true);
                }
                if (empty != null && Directory.Exists(empty) && !Directory.Exists(target)) Directory.Move(empty, target);
            }
        }

        private static Dictionary<string, string> Inventory(string directory)
        {
            BackupArchiveService.RejectLinks(directory);
            var files = new Dictionary<string, string>(PathComparer());
            void Walk(string current)
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(current))
                {
                    BackupArchiveService.RejectLinks(entry);
                    if (files.Count >= 200000) throw new IOException("迁移文件数量超限。");
                    if (Directory.Exists(entry)) { files.Add(Path.GetRelativePath(directory, entry), "<directory>"); Walk(entry); }
                    else
                    {
                        // FileShare.None catches an old Windows Host holding these files. Hashes detect concurrent changes elsewhere.
                        using (new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.None)) { }
                        files.Add(Path.GetRelativePath(directory, entry), BackupArchiveFormat.Hash(entry));
                    }
                }
            }
            Walk(directory); return files;
        }
        private static bool Same(Dictionary<string, string> a, Dictionary<string, string> b) => a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var value) && value == p.Value);
        private static string Fingerprint(Dictionary<string, string> inventory) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(inventory.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new[] { p.Key.Replace('\\', '/'), p.Value })))));
        private static void ValidateRetainedSources(RuntimePathReport report)
        {
            foreach (var copy in report.Moves.Where(m => !string.IsNullOrEmpty(m.SourceFingerprint)))
            {
                if (!Directory.Exists(copy.Source)) throw new IOException("复制迁移的旧目录丢失；尚未确认清理，请核对原件和挂载。");
                if (Fingerprint(Inventory(copy.Source)) != copy.SourceFingerprint)
                    throw new IOException("复制迁移后旧目录又发生变化，已保留两边并停止自动选择，请先核对新增资料。");
                if (!Directory.Exists(copy.Destination)) throw new IOException("复制迁移的目标丢失，已保留旧目录，请先核对挂载。");
            }
        }
        private static void Remember(RuntimePathReport report, string source, string destination)
        { if (!string.IsNullOrWhiteSpace(source)) { report.Aliases[source] = Full(destination); report.Changed?.Invoke(); } }
        private static bool HasSoul(string path) => path != null && File.Exists(Path.Combine(path, "tracesoul2-brainframe.sqlite3"));
        private static bool HasHome(string path) => path != null && Directory.Exists(path) &&
            (File.Exists(Path.Combine(path, "home.json")) || Directory.Exists(Path.Combine(path, "souls")));
        private static bool HasPlugins(string path) => path != null && Directory.Exists(path) && Directory.GetDirectories(path).Any(p => File.Exists(Path.Combine(p, "plugin.json")));
        private static bool HasMaterial(string path) => path != null && Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any();
        private static bool HasArchives(string path) => path != null && Directory.Exists(Path.Combine(path, "packages")) &&
            Directory.GetFiles(Path.Combine(path, "packages")).All(p => p.EndsWith(".tsbackup", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".tsbackup.json", StringComparison.OrdinalIgnoreCase)) &&
            Directory.GetFileSystemEntries(path).All(p => Path.GetFileName(p) == "packages" || Path.GetFileName(p) == "work");
        private static IEnumerable<string> OldApplications(string app)
        {
            var parent = Path.GetDirectoryName(app);
            if (!Directory.Exists(parent)) return Array.Empty<string>();
            // One newest updater-owned backup only. Never search the workspace or combine old generations.
            return Directory.GetDirectories(parent, "." + Path.GetFileName(app) + ".tracesoul2-backup-*")
                .OrderByDescending(p => Regex.Match(Path.GetFileName(p), @"[0-9]{8}-[0-9]{6}").Value, StringComparer.Ordinal).Take(1);
        }
        private static bool InsideApplication(string path, string app) => path != null && (BackupArchiveService.Inside(path, app) || OldApplications(app).Any(old => BackupArchiveService.Inside(path, old)));
        private static string Alias(Dictionary<string, string> aliases, string raw, string root, RuntimePathReport report = null)
        {
            if (raw == null) return null;
            var match = aliases.FirstOrDefault(p => string.Equals(p.Key, raw, Comparison));
            if (match.Key == null && Path.IsPathFullyQualified(raw))
                match = aliases.Where(p => Path.IsPathFullyQualified(p.Key) && BackupArchiveService.Inside(raw, p.Key))
                    .OrderByDescending(p => p.Key.Length).FirstOrDefault();
            if (match.Key == null) return raw;
            var destination = string.Equals(match.Key, raw, Comparison) ? match.Value : Path.Combine(match.Value, Path.GetRelativePath(match.Key, raw));
            if (!Directory.Exists(destination)) throw new IOException("已迁移目录的目标丢失，请检查挂载或找回原资料。");
            var original = Resolve(raw, root);
            if (report == null)
            {
                report = ReadReport(match.Value); // Home hints point to the retained-source record.
                ValidateRetainedSources(report);
            }
            var retained = report.Moves.Any(m => Equal(m.Source, Resolve(match.Key, root)) && Equal(m.Destination, match.Value) && !string.IsNullOrEmpty(m.SourceFingerprint));
            if (original != null && Directory.Exists(original) && !Equal(original, destination) && Directory.EnumerateFileSystemEntries(original).Any() &&
                !retained &&
                !(TryHomeRedirect(original, out var redirected) && Equal(redirected, destination)))
                throw new IOException("旧路径重新出现资料，已保留两边并停止自动选择。");
            return destination;
        }
        private static string Resolve(string raw, string root)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            if (!OperatingSystem.IsWindows() && Regex.IsMatch(raw, @"^[a-zA-Z]:[\\/]")) return null;
            if (OperatingSystem.IsWindows() && raw.StartsWith('/') && !raw.StartsWith("//")) return null;
            return Full(Path.IsPathRooted(raw) ? raw : Path.Combine(root, raw));
        }
        private static string RedirectChild(string path, string oldHome, string home) => path != null && !Equal(oldHome, home) && BackupArchiveService.Inside(path, oldHome)
            ? Path.Combine(home, Path.GetRelativePath(oldHome, path)) : path;
        private static string Portable(string root, string path) => BackupArchiveService.Inside(path, root) ? Path.GetRelativePath(root, path) : path;
        private static bool TryHomeRedirect(string path, out string target)
        {
            target = null;
            if (!Directory.Exists(path)) return false;
            BackupArchiveService.RejectLinks(path);
            var files = Directory.GetFileSystemEntries(path);
            if (files.Length != 1 || Path.GetFileName(files[0]) != "home.json") return false;
            try
            {
                var value = JsonNode.Parse(File.ReadAllText(files[0]))?.AsObject();
                if (value == null || value.Count != 1 || !value.ContainsKey("redirectHome")) return false;
                target = value["redirectHome"].GetValue<string>(); return Path.IsPathFullyQualified(target);
            }
            catch { throw new IOException("家目录重定向记录无法读取。"); }
        }
        private static string String(JsonObject value, string name) => value[name]?.GetValue<string>();
        private static string First(params string[] values) => values.First(v => !string.IsNullOrWhiteSpace(v));
        private static string SafeId(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "default";
            var id = value.Replace('\\', '/').Split('/').Last();
            BackupArchiveFormat.SafeName(id); return id;
        }
        private static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        private static bool Equal(string a, string b) => a != null && b != null && string.Equals(Full(a), Full(b), Comparison);
        private static StringComparer PathComparer() => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        private static T ReadJson<T>(string path)
        { BackupArchiveService.RejectLinks(path); return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) ?? throw new IOException("目录迁移记录无法读取。"); }
        public static RuntimePathReport ReadReport(string root) => File.Exists(Path.Combine(root, ReportName)) ? ReadJson<RuntimePathReport>(Path.Combine(root, ReportName)) : new RuntimePathReport();
        private static void SavePrivate(string path, string text) => BackupArchiveService.WritePrivate(path, text);
    }

    public sealed class RuntimePathReport
    {
        [System.Text.Json.Serialization.JsonIgnore] internal Action Changed { get; set; }
        public DateTimeOffset? CheckedUtc { get; set; }
        public Dictionary<string, string> Aliases { get; set; } = new Dictionary<string, string>();
        public List<RuntimePathMove> Moves { get; set; } = new List<RuntimePathMove>();
        public Dictionary<string, string> ConfigBackups { get; set; } = new Dictionary<string, string>();
    }
    public sealed class RuntimePathMove
    {
        public string Kind { get; set; }
        public string Source { get; set; }
        public string Destination { get; set; }
        public string OriginalBackup { get; set; }
        public string SourceFingerprint { get; set; }
        public DateTimeOffset? CopyVerifiedUtc { get; set; }
    }
}
