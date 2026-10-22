using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SQLite;

namespace TraceSoul2.Host
{
    public sealed class BackupSettings
    {
        public bool Enabled { get; set; }
        public string Directory { get; set; } = "archives";
        public int IntervalHours { get; set; } = 168;
        public int KeepCount { get; set; } = 1;
        public Dictionary<string, string> Extras { get; set; } = new Dictionary<string, string>();
        public DateTimeOffset? LastAttemptUtc { get; set; }
        public string LastResult { get; set; }
        public string RecoveryKeyId { get; set; }
        public string AcceptedHostVersion { get; set; }
        public string ProtectedPackageId { get; set; }
        public string ProtectedFromVersion { get; set; }
        public string PendingUpdateVersion { get; set; }
    }

    public sealed class BackupPackage
    {
        public string Id { get; set; }
        public DateTimeOffset CreatedUtc { get; set; }
        public long Bytes { get; set; }
        public int FileCount { get; set; }
        public string Sha256 { get; set; }
        public string HostVersion { get; set; }
    }

    public sealed partial class BackupArchiveService : IDisposable
    {
        private readonly TraceHomeLayout home;
        private readonly string configPath;
        private readonly string keyPath;
        private readonly object gate = new object();
        private readonly Func<Action, CancellationToken, Task> snapshotScope;
        private readonly Func<string> hostVersion;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private BackupSettings settings;
        private string configError;
        private Task job;
        private string pendingPackageId;
        private HashSet<string> retainedMigrationSources = new HashSet<string>();
        private string phase = "idle";
        private string error;
        internal Exception LastFailure { get; private set; }
        internal Action<string> CandidateWritten { get; set; }
        private static readonly Regex IdPattern = new Regex(@"^archive-[0-9]{8}T[0-9]{6}Z-[a-f0-9]{12}$");
        private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        public BackupArchiveService(TraceHomeLayout layout, Func<Action, CancellationToken, Task> scope = null, Func<string> version = null)
        {
            home = layout;
            hostVersion = version ?? TraceHome.HostVersion;
            configPath = Path.Combine(home.Root, "archive-settings.json");
            keyPath = Path.Combine(home.Root, "archive-recovery.key");
            snapshotScope = scope ?? ((action, token) => { token.ThrowIfCancellationRequested(); action(); return Task.CompletedTask; });
            try
            {
                settings = File.Exists(configPath) ? JsonSerializer.Deserialize<BackupSettings>(File.ReadAllText(configPath), BackupArchiveFormat.Json) : new BackupSettings();
                // Existing multi-package plans adopt one slot on their next successful archive, never on startup.
                if (settings != null && settings.KeepCount >= 1 && settings.KeepCount <= 365) settings.KeepCount = 1;
                ValidateSettings(settings);
                if (settings.LastResult == "执行中")
                { phase = "failed"; error = "上次存档因重启中断，已有成品保留；按原计划继续。"; settings.LastResult = error; }
                if (GuardVersionChange())
                    WritePrivate(configPath, JsonSerializer.Serialize(settings, BackupArchiveFormat.Json));
            }
            catch { settings = new BackupSettings(); configError = "存档配置无法读取或路径不可用，请检查并重新保存。原配置未覆盖。"; }
        }

        public object Status()
        {
            lock (gate) return new
            {
                settings = Clone(settings), directory = ArchiveDirectory(settings), busy = job != null && !job.IsCompleted,
                phase, error = configError ?? error, keyAvailable = File.Exists(keyPath),
                nextUtc = settings.Enabled && configError == null && settings.ProtectedPackageId == null ? (settings.LastAttemptUtc ?? DateTimeOffset.UtcNow).AddHours(settings.IntervalHours) : (DateTimeOffset?)null,
                protection = new { active = settings.ProtectedPackageId != null, packageId = settings.ProtectedPackageId,
                    fromVersion = settings.ProtectedFromVersion, targetVersion = settings.PendingUpdateVersion, currentVersion = hostVersion() },
                packages = ListPackages(settings),
                pathRepairs = RuntimePathRepair.ReadReport(home.Root).Moves,
                sources = new { home = home.Root, souls = home.SoulsDirectory, currentSoul = home.SoulDirectory,
                    plugins = home.PluginsDirectory, pluginsData = home.PluginsDataDirectory }
            };
        }

        public object Configure(BackupSettings incoming)
        {
            lock (gate)
            {
                if (job != null && !job.IsCompleted) throw new InvalidOperationException("存档正在执行，请完成后再修改设置。");
                ValidateSettings(incoming);
                GuardVersionChange();
                if (settings.ProtectedPackageId != null && !string.Equals(ArchiveDirectory(incoming), ArchiveDirectory(settings), PathComparison))
                    throw new InvalidOperationException("更新前的唯一存档正在保护，请先确认当前版本正常，再更换存档目录。");
                var changedSchedule = incoming.Enabled != settings.Enabled || incoming.IntervalHours != settings.IntervalHours;
                incoming.LastAttemptUtc = changedSchedule ? DateTimeOffset.UtcNow : settings.LastAttemptUtc;
                incoming.LastResult = settings.LastResult;
                // Settings never accept a recovery key from HTTP. One per instance, independent of the destination.
                EnsureKey();
                incoming.RecoveryKeyId = settings.RecoveryKeyId;
                incoming.AcceptedHostVersion = settings.AcceptedHostVersion;
                incoming.ProtectedPackageId = settings.ProtectedPackageId;
                incoming.ProtectedFromVersion = settings.ProtectedFromVersion;
                incoming.PendingUpdateVersion = settings.PendingUpdateVersion;
                WritePrivate(configPath, JsonSerializer.Serialize(incoming, BackupArchiveFormat.Json));
                settings = Clone(incoming); configError = null; error = null;
                return Status();
            }
        }

        public bool Start(bool scheduled = false, DateTimeOffset? now = null)
            => StartCore(scheduled, now, null);

        private bool GuardVersionChange()
        {
            var previous = ListPackages(settings).FirstOrDefault();
            settings.AcceptedHostVersion ??= previous == null ? hostVersion() : previous.HostVersion ?? "未记录的旧版本";
            if (previous == null || settings.ProtectedPackageId != null || settings.AcceptedHostVersion == hostVersion()) return false;
            settings.ProtectedPackageId = previous.Id;
            settings.ProtectedFromVersion = settings.AcceptedHostVersion;
            settings.PendingUpdateVersion = hostVersion();
            return true;
        }

        private bool StartCore(bool scheduled, DateTimeOffset? now, string updateVersion)
        {
            lock (gate)
            {
                var utc = now ?? DateTimeOffset.UtcNow;
                if (configError != null) throw new InvalidOperationException(configError);
                if (job != null && !job.IsCompleted) return false;
                if (settings.ProtectedPackageId != null)
                {
                    if (scheduled) return false;
                    throw new InvalidOperationException("更新前的唯一存档已锁定；请确认当前版本正常后再允许替换。");
                }
                if (scheduled && (!settings.Enabled || (settings.LastAttemptUtc.HasValue && utc < settings.LastAttemptUtc.Value.AddHours(settings.IntervalHours)))) return false;
                ValidateSettings(settings);
                EnsureKey();
                settings.LastAttemptUtc = utc; settings.LastResult = "执行中";
                WritePrivate(configPath, JsonSerializer.Serialize(settings, BackupArchiveFormat.Json));
                var copy = Clone(settings); phase = "snapshot"; error = null; LastFailure = null;
                job = Task.Run(() => CreateAsync(copy, lifetime.Token, updateVersion));
                return true;
            }
        }

        public Task WaitAsync() { lock (gate) return job ?? Task.CompletedTask; }
        public async Task PrepareForUpdateAsync(string targetVersion, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(targetVersion)) throw new ArgumentException("更新目标版本不能为空。");
            Task archive;
            lock (gate)
            {
                if (configError != null) throw new InvalidOperationException(configError);
                if (job != null && !job.IsCompleted) throw new InvalidOperationException("存档正在执行，请完成后再安装更新。");
                if (settings.ProtectedPackageId != null)
                {
                    // A corrective update must retain the original recovery point, even after several failed updates.
                    Verify(settings.ProtectedPackageId);
                    settings.PendingUpdateVersion = targetVersion;
                    WritePrivate(configPath, JsonSerializer.Serialize(settings, BackupArchiveFormat.Json));
                    return;
                }
                if (!StartCore(false, null, targetVersion)) throw new InvalidOperationException("更新前存档未能启动。");
                archive = job;
            }
            await archive.WaitAsync(token);
            lock (gate)
                if (phase != "completed" || settings.ProtectedPackageId == null)
                    throw new InvalidOperationException("更新前存档未通过完整校验，已停止更新并保留原存档。" + (error ?? string.Empty));
        }

        public object ConfirmVersion(string version, string packageId)
        {
            lock (gate)
            {
                if (job != null && !job.IsCompleted) throw new InvalidOperationException("请等待存档结束。");
                if (settings.ProtectedPackageId == null || packageId != settings.ProtectedPackageId || version != hostVersion())
                    throw new InvalidOperationException("存档或程序版本已变化，请刷新后重新核对。");
                Verify(packageId);
                var confirmed = Clone(settings);
                confirmed.AcceptedHostVersion = hostVersion();
                confirmed.ProtectedPackageId = confirmed.ProtectedFromVersion = confirmed.PendingUpdateVersion = null;
                confirmed.LastAttemptUtc = DateTimeOffset.UtcNow;
                confirmed.LastResult = "已确认当前版本正常，原存档保留到下一份校验成功后替换。";
                WritePrivate(configPath, JsonSerializer.Serialize(confirmed, BackupArchiveFormat.Json));
                settings = confirmed; error = null;
                return Status();
            }
        }
        public byte[] ExportKey()
        {
            lock (gate) { EnsureKey(); return System.Text.Encoding.ASCII.GetBytes(Convert.ToBase64String(ReadKey()) + "\n"); }
        }
        public Stream OpenPackage(string id)
        {
            lock (gate)
            {
                var root = ArchiveDirectory(settings); RejectLinks(root);
                if (!ListPackages(settings).Any(p => p.Id == id)) throw new FileNotFoundException("存档不存在。");
                return new FileStream(PackagePath(root, id), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            }
        }

        public object Verify(string id)
        {
            lock (gate)
            {
                if (job != null && !job.IsCompleted) throw new InvalidOperationException("请等待当前存档完成。");
                var root = ArchiveDirectory(settings); RejectLinks(root);
                var item = ListPackages(settings).FirstOrDefault(p => p.Id == id) ?? throw new FileNotFoundException("存档不存在。");
                var work = NewWork(root);
                try
                {
                    var manifest = ValidatePackage(PackagePath(root, id), item, work, lifetime.Token);
                    return new { valid = true, manifest.HostVersion, currentHostVersion = hostVersion(),
                        versionMatches = manifest.HostVersion == hostVersion(), manifest.ActiveSoul,
                        fileCount = manifest.Files.Count, manifest.Notes, manifest.Consistency };
                }
                finally { DeleteWork(root, work); }
            }
        }

        private BackupManifest ValidatePackage(string path, BackupPackage item, string work, CancellationToken token)
        {
            if (BackupArchiveFormat.Hash(path) != item.Sha256) throw new InvalidDataException("存档成品校验失败。");
            var zip = Path.Combine(work, "verify.zip");
            var key = ReadKey();
            try { BackupArchiveFormat.Decrypt(path, zip, key, token); }
            finally { CryptographicOperations.ZeroMemory(key); }
            var manifest = BackupArchiveFormat.ValidateZip(zip, token);
            using var archive = ZipFile.OpenRead(zip);
            foreach (var file in manifest.Files.Where(f => f.Database))
            {
                token.ThrowIfCancellationRequested();
                var db = Path.Combine(work, "check.sqlite3");
                archive.GetEntry(file.Path).ExtractToFile(db); CheckDatabase(db); File.Delete(db);
            }
            return manifest;
        }

        private async Task CreateAsync(BackupSettings config, CancellationToken token, string updateVersion)
        {
            string root = null, work = null, published = null;
            var id = "archive-" + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'") + "-" + Guid.NewGuid().ToString("N").Substring(0, 12);
            lock (gate) pendingPackageId = id;
            try
            {
                root = ArchiveDirectory(config); RejectLinks(root);
                Directory.CreateDirectory(root); PrivateDirectory(root);
                work = NewWork(root);
                var content = Path.Combine(work, "content"); Directory.CreateDirectory(content);
                var manifest = new BackupManifest { HostVersion = hostVersion(), StartedUtc = DateTimeOffset.UtcNow };
                manifest.Notes.Add("包含所有角色、外置当前角色、插件程序与持久资料；不含登录会话、历史备份、更新缓存和调试dump。");
                manifest.Notes.Add("独立语音和硬件服务只包含明确配置的附加目录；请核对Extras范围。");
                await snapshotScope(() => Snapshot(content, manifest, config, token), token);
                manifest.FinishedUtc = DateTimeOffset.UtcNow;
                File.WriteAllText(Path.Combine(content, "manifest.json"), JsonSerializer.Serialize(manifest, BackupArchiveFormat.Json));
                SetPhase("packing");
                var zip = Path.Combine(work, "archive.zip"); ZipFile.CreateFromDirectory(content, zip, CompressionLevel.Fastest, false);
                BackupArchiveFormat.ValidateZip(zip, token);
                SetPhase("encrypting");
                var encrypted = Path.Combine(work, id + ".tsbackup");
                var key = ReadKey();
                try { BackupArchiveFormat.Encrypt(zip, encrypted, key, token); }
                finally { CryptographicOperations.ZeroMemory(key); }
                var package = new BackupPackage { Id = id, CreatedUtc = manifest.FinishedUtc, Bytes = new FileInfo(encrypted).Length,
                    FileCount = manifest.Files.Count, Sha256 = BackupArchiveFormat.Hash(encrypted), HostVersion = manifest.HostVersion };
                var packages = Path.Combine(root, "packages"); RejectLinks(packages); Directory.CreateDirectory(packages);
                published = PackagePath(root, id); File.Move(encrypted, published);
                WritePrivate(published + ".json", JsonSerializer.Serialize(package, BackupArchiveFormat.Json));
                CandidateWritten?.Invoke(published);
                SetPhase("verifying");
                ValidatePackage(published, package, work, token);
                token.ThrowIfCancellationRequested();
                lock (gate)
                {
                    var committed = Clone(settings);
                    committed.AcceptedHostVersion = hostVersion();
                    if (updateVersion != null)
                    {
                        committed.ProtectedPackageId = id;
                        committed.ProtectedFromVersion = hostVersion();
                        committed.PendingUpdateVersion = updateVersion;
                    }
                    committed.LastResult = updateVersion == null ? "成功" : "更新前存档已校验并锁定，等待确认更新后的版本正常。";
                    WritePrivate(configPath, JsonSerializer.Serialize(committed, BackupArchiveFormat.Json));
                    settings = committed; published = null;
                    // Keep the newly verified ID, even if the wall clock moved backwards.
                    foreach (var old in ListPackages(config).Where(p => p.Id != id))
                    {
                        var oldPath = PackagePath(root, old.Id); RejectLinks(oldPath);
                        File.Delete(oldPath); File.Delete(oldPath + ".json");
                    }
                    phase = "completed";
                }
            }
            catch (Exception ex)
            {
                lock (gate)
                {
                    LastFailure = ex;
                    // Source files may contain credentials. Never return raw SQLite/JSON errors or contents.
                    var step = phase == "snapshot" ? "收集资料" : phase == "packing" || phase == "verifying" ? "打包校验" : "加密保存";
                    error = ex is OperationCanceledException ? "存档已中断，已有成品保留。"
                        : ex is InvalidDataException ? "存档失败（" + step + "）：数据库或存档完整性校验未通过；原存档保留，请先检查数据。"
                        : "存档失败（" + step + "）：请检查目录权限、剩余空间、数据库和正在变更的文件。";
                    settings.LastResult = error; phase = "failed";
                    try { WritePrivate(configPath, JsonSerializer.Serialize(settings, BackupArchiveFormat.Json)); } catch { }
                }
            }
            finally
            {
                if (published != null)
                {
                    RejectLinks(published);
                    if (File.Exists(published)) File.Delete(published);
                    if (File.Exists(published + ".json")) { RejectLinks(published + ".json"); File.Delete(published + ".json"); }
                }
                if (work != null) DeleteWork(root, work);
                lock (gate) pendingPackageId = null;
            }
        }

        private void Snapshot(string target, BackupManifest manifest, BackupSettings config, CancellationToken token)
        {
            retainedMigrationSources = new HashSet<string>(RuntimePathRepair.ReadReport(home.Root).Moves
                .Where(m => !string.IsNullOrEmpty(m.SourceFingerprint)).Select(m => Path.GetFullPath(m.Source)),
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            if (!File.Exists(Path.Combine(home.SoulDirectory, "tracesoul2-brainframe.sqlite3"))) throw new IOException("当前Soul主库不存在。");
            RejectLinks(home.Root);
            var relative = Path.GetRelativePath(home.SoulsDirectory, home.SoulDirectory).Replace('\\', '/');
            var legacySingle = Path.GetFullPath(home.SoulsDirectory).Equals(Path.GetFullPath(home.Root), PathComparison);
            manifest.ActiveSoul = !legacySingle && Inside(home.SoulDirectory, home.SoulsDirectory) ? BackupArchiveFormat.SafeName(relative) : "_external_current";
            foreach (var file in Directory.GetFiles(home.Root).Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && f != configPath))
                CopyFile(file, "Data/" + Path.GetFileName(file), target, manifest, token);
            if (!legacySingle) CopyTree(home.SoulsDirectory, "Data/souls", target, manifest, token);
            if (manifest.ActiveSoul == "_external_current") CopyTree(home.SoulDirectory, "Data/souls/_external_current", target, manifest, token);
            CopyTree(home.PluginsDirectory, "Plugins", target, manifest, token);
            CopyTree(home.PluginsDataDirectory, "plugins_data", target, manifest, token);
            foreach (var extra in config.Extras) CopyTree(extra.Value, "Extras/" + extra.Key, target, manifest, token);
            foreach (var name in new[] { "identity_cards.json", "emotion_tags.json", "expression_semantics.json", "appsettings.json" })
            {
                var source = Path.Combine(AppContext.BaseDirectory, name);
                if (File.Exists(source)) CopyFile(source, "AppDefaults/" + name, target, manifest, token);
            }
        }

        private void CopyTree(string source, string prefix, string target, BackupManifest manifest, CancellationToken token)
        {
            if (!Directory.Exists(source)) throw new DirectoryNotFoundException("缺少存档来源目录。");
            RejectLinks(source);
            foreach (var entry in Directory.EnumerateFileSystemEntries(source))
            {
                token.ThrowIfCancellationRequested();
                RejectLinks(entry);
                var name = Path.GetFileName(entry);
                if (retainedMigrationSources.Contains(Path.GetFullPath(entry))) continue;
                if (new[] { "auth-keys", "archives", "backups", "updates", "diagnostics", "dev", ".path-repair-configs" }
                    .Any(folder => Path.GetFullPath(entry).Equals(Path.Combine(home.Root, folder), PathComparison))) continue;
                if (name.Contains(".tracesoul-moved-") || name.Contains(".tracesoul-stage-") || name.Contains(".tracesoul-empty-") ||
                    name == ".git" || name == "__pycache__" || name == "debug-active" || name.StartsWith("debug-", StringComparison.OrdinalIgnoreCase) && Directory.Exists(entry)) continue;
                if (Directory.Exists(entry)) CopyTree(entry, prefix + "/" + name, target, manifest, token);
                else if (!name.EndsWith("-wal") && !name.EndsWith("-shm") && !name.EndsWith("-journal") && !name.EndsWith(".tmp"))
                    CopyFile(entry, prefix + "/" + name, target, manifest, token);
            }
        }

        private void CopyFile(string source, string relative, string target, BackupManifest manifest, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); RejectLinks(source); BackupArchiveFormat.SafeName(relative);
            if (Path.GetFullPath(source).Equals(keyPath, PathComparison) || Path.GetFullPath(source).Equals(configPath, PathComparison) ||
                Path.GetFullPath(source).Equals(Path.Combine(home.Root, RuntimePathRepair.ReportName), PathComparison)) return;
            if (manifest.Files.Count >= 200000) throw new IOException("存档文件数量超限。");
            var destination = Path.Combine(target, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            if (File.Exists(destination)) throw new IOException("存档来源目录重复。");
            var extension = Path.GetExtension(source).ToLowerInvariant();
            var database = extension == ".sqlite3" || extension == ".sqlite" || extension == ".db";
            if (database)
            {
                CopyDatabaseSnapshot(source, destination, token);
            }
            else
            {
                var copied = false;
                for (var attempt = 0; attempt < 3 && !copied; attempt++)
                {
                    var before = new FileInfo(source); var length = before.Length; var time = before.LastWriteTimeUtc;
                    File.Copy(source, destination, true);
                    var after = new FileInfo(source);
                    copied = length == after.Length && time == after.LastWriteTimeUtc && BackupArchiveFormat.Hash(source) == BackupArchiveFormat.Hash(destination);
                }
                if (!copied) throw new IOException("文件在存档期间持续变化。");
                if (extension == ".json")
                {
                    // TypeScript configuration is JSONC, including packaged plugin dependencies.
                    var jsonc = Path.GetFileName(source).StartsWith("tsconfig", StringComparison.OrdinalIgnoreCase);
                    using var json = JsonDocument.Parse(File.ReadAllText(destination), new JsonDocumentOptions
                    { AllowTrailingCommas = jsonc, CommentHandling = jsonc ? JsonCommentHandling.Skip : JsonCommentHandling.Disallow });
                }
            }
            manifest.Files.Add(new BackupFile { Path = relative, Bytes = new FileInfo(destination).Length,
                Sha256 = BackupArchiveFormat.Hash(destination), Database = database });
        }

        public static void CheckDatabase(string path)
        {
            using var db = new SQLiteConnection(path, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex);
            if (db.ExecuteScalar<string>("PRAGMA quick_check") != "ok") throw new InvalidDataException("数据库完整性检查失败。");
        }

        // Opening even a ReadOnly WAL connection can create WAL/SHM or modify its read marks.
        // Capture only stable bytes first; all SQLite recovery/backup operations run on that private copy.
        private static void CopyDatabaseSnapshotCore(string source, string destination, CancellationToken token)
        {
            RejectLinks(source); RejectLinks(destination);
            if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("数据库副本目标已存在，未覆盖。");
            var parts = new[] { source, source + "-wal", source + "-journal" };
            Dictionary<string, (string Hash, long Length, long Written)> State() => parts.Where(File.Exists).ToDictionary(path => path, path =>
            {
                RejectLinks(path); var info = new FileInfo(path);
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return (Convert.ToHexString(SHA256.HashData(stream)), info.Length, info.LastWriteTimeUtc.Ticks);
            });
            for (var attempt = 0; attempt < 3; attempt++)
            {
                token.ThrowIfCancellationRequested();
                var capture = Path.Combine(Path.GetDirectoryName(destination), ".db-snapshot-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(capture); PrivateDirectory(capture);
                try
                {
                    var before = State();
                    if (!before.ContainsKey(source)) throw new FileNotFoundException("数据库来源丢失。");
                    var clone = Path.Combine(capture, "database.sqlite3");
                    foreach (var part in before.Keys)
                    {
                        using var input = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        using var output = new FileStream(clone + part.Substring(source.Length), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                        input.CopyTo(output);
                    }
                    var after = State();
                    if (before.Count != after.Count || before.Any(p => !after.TryGetValue(p.Key, out var stamp) || stamp != p.Value ||
                        !string.Equals(BackupArchiveFormat.Hash(clone + p.Key.Substring(source.Length)), p.Value.Hash, StringComparison.OrdinalIgnoreCase))) continue;
                    using (var db = new SQLiteConnection(clone))
                    { db.BusyTimeout = TimeSpan.FromSeconds(15); db.Backup(destination); }
                    using (var standalone = new SQLiteConnection(destination))
                        if (standalone.ExecuteScalar<string>("PRAGMA journal_mode=DELETE") != "delete")
                            throw new InvalidDataException("数据库副本未能独立保存。");
                    CheckDatabase(destination); return;
                }
                finally
                {
                    if (!Inside(capture, Path.GetDirectoryName(destination)) || !Path.GetFileName(capture).StartsWith(".db-snapshot-", StringComparison.Ordinal))
                        throw new IOException("数据库暂存目录越界，未清理。");
                    RejectLinks(capture); Directory.Delete(capture, true);
                }
            }
            throw new IOException("数据库或事务日志在存档期间持续变化，已保留来源和已有成品。");
        }

        private void ValidateSettings(BackupSettings value)
        {
            if (value == null || value.IntervalHours < 1 || value.IntervalHours > 720 || value.KeepCount != 1 || value.Extras == null || value.Extras.Count > 16)
                throw new ArgumentException("存档间隔须为1～720小时，保留1～365份，附加目录最多16个。");
            var root = ArchiveDirectory(value); RejectLinks(root);
            var recursiveSources = new List<string> { home.SoulDirectory, home.PluginsDirectory, home.PluginsDataDirectory, AppContext.BaseDirectory };
            if (!Path.GetFullPath(home.SoulsDirectory).Equals(Path.GetFullPath(home.Root), PathComparison)) recursiveSources.Add(home.SoulsDirectory);
            if (Path.GetPathRoot(root) == root || Inside(home.Root, root) ||
                recursiveSources.Any(s => Inside(root, s) || Inside(s, root)))
                throw new ArgumentException("存档目录须与角色、插件和程序目录分开，不能覆盖源目录或它们的上级。");
            foreach (var extra in value.Extras)
            {
                if (!Regex.IsMatch(extra.Key, @"^[a-zA-Z0-9_-]{1,40}$") || !Path.IsPathFullyQualified(extra.Value) || !Directory.Exists(extra.Value))
                    throw new ArgumentException("附加资料名称须为英文数字或下划线，目录须为已存在的绝对路径。");
                RejectLinks(extra.Value);
                if (Inside(extra.Value, root) || Inside(root, extra.Value) || Inside(home.Root, extra.Value))
                    throw new ArgumentException("附加目录不能包含家目录或与存档目录重叠。");
            }
            if (value.Extras.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != value.Extras.Count)
                throw new ArgumentException("附加资料名称不能重复。");
        }

        private string ArchiveDirectory(BackupSettings value)
        {
            if (string.IsNullOrWhiteSpace(value?.Directory)) throw new ArgumentException("请填写存档目录。");
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value.Directory, home.Root));
        }
        private static string PackagePath(string root, string id)
        { if (!IdPattern.IsMatch(id ?? "")) throw new ArgumentException("无效的存档编号。"); return Path.Combine(root, "packages", id + ".tsbackup"); }
        private static List<BackupPackage> ListPackages(BackupSettings config, string root = null)
        {
            root ??= config.Directory;
            var directory = Path.Combine(root, "packages");
            if (!Directory.Exists(directory)) return new List<BackupPackage>();
            RejectLinks(directory);
            var result = new List<BackupPackage>();
            foreach (var path in Directory.EnumerateFiles(directory, "archive-*.tsbackup.json"))
            {
                try
                {
                    RejectLinks(path);
                    var item = JsonSerializer.Deserialize<BackupPackage>(File.ReadAllText(path), BackupArchiveFormat.Json);
                    if (item != null && IdPattern.IsMatch(item.Id ?? "") && Path.GetFileName(path) == item.Id + ".tsbackup.json" && File.Exists(PackagePath(root, item.Id)))
                    { RejectLinks(PackagePath(root, item.Id)); result.Add(item); }
                }
                catch { /* Malformed/unrelated files never authorize deletion. */ }
            }
            return result.OrderByDescending(p => p.CreatedUtc).ThenByDescending(p => p.Id).ToList();
        }
        private List<BackupPackage> ListPackages(BackupSettings config) => ListPackages(config, ArchiveDirectory(config)).Where(p => p.Id != pendingPackageId).ToList();
        private byte[] ReadKey() { RejectLinks(keyPath); var key = Convert.FromBase64String(File.ReadAllText(keyPath).Trim()); if (key.Length != 32) throw new InvalidDataException("恢复密钥无效。"); return key; }
        private void EnsureKey()
        {
            RejectLinks(keyPath);
            if (File.Exists(keyPath))
            {
                var existing = ReadKey();
                var id = Convert.ToHexString(SHA256.HashData(existing));
                CryptographicOperations.ZeroMemory(existing);
                if (!string.IsNullOrEmpty(settings.RecoveryKeyId) && settings.RecoveryKeyId != id)
                    throw new InvalidDataException("恢复密钥与已有存档计划不符，请找回原密钥。");
                settings.RecoveryKeyId = id;
                return;
            }
            if (!string.IsNullOrEmpty(settings.RecoveryKeyId)) throw new InvalidDataException("恢复密钥丢失，请找回原密钥；不会创建新密钥覆盖旧存档关系。");
            WritePrivate(keyPath, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) + "\n");
            var key = ReadKey(); settings.RecoveryKeyId = Convert.ToHexString(SHA256.HashData(key)); CryptographicOperations.ZeroMemory(key);
        }
        private static BackupSettings Clone(BackupSettings value) => JsonSerializer.Deserialize<BackupSettings>(JsonSerializer.Serialize(value, BackupArchiveFormat.Json), BackupArchiveFormat.Json);
        private void SetPhase(string value) { lock (gate) phase = value; }
        public static bool Inside(string candidate, string root)
        {
            var a = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
            var b = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            return a.Equals(b, PathComparison) || a.StartsWith(b + Path.DirectorySeparatorChar, PathComparison);
        }
        public static void RejectLinks(string path)
        {
            for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            {
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("存档路径不支持符号链接或目录联接，请填写真实目录。");
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
        }
        public static void PrivateDirectory(string path)
        {
            if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); return; }
            var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
            foreach (var user in new[] { WindowsIdentity.GetCurrent().User, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
                acl.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(acl);
        }
        internal static void WritePrivate(string path, string value)
        {
            RejectLinks(path);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var directory = Path.GetDirectoryName(path); Directory.CreateDirectory(directory);
            using (File.Create(temp)) { }
            try
            {
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                else
                {
                    var acl = new FileSecurity(); acl.SetAccessRuleProtection(true, false);
                    foreach (var user in new[] { WindowsIdentity.GetCurrent().User, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
                        acl.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
                    new FileInfo(temp).SetAccessControl(acl);
                }
                File.WriteAllText(temp, value); File.Move(temp, path, true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        private static string NewWork(string root)
        {
            var path = Path.Combine(root, "work", Guid.NewGuid().ToString("N")); RejectLinks(path);
            Directory.CreateDirectory(path); PrivateDirectory(path); return path;
        }
        private static void DeleteWork(string root, string work)
        {
            if (!Inside(work, Path.Combine(root, "work"))) throw new IOException("临时目录越界。");
            RejectLinks(work); if (Directory.Exists(work)) Directory.Delete(work, true);
        }
        public void Dispose() { lifetime.Cancel(); }
    }
}
