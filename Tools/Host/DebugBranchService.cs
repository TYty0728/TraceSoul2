using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SQLite;
using TraceSoul2.Logic;

namespace TraceSoul2.Host
{
    /// <summary>
    /// 调试分支：从主线当前状态 fork 出一份完整快照（独立目录），隔离运行，销毁无残留。
    /// 主线数据库永不感知分支；分支注册表保存在主线数据目录的 JSON 文件里。
    /// </summary>
    public sealed class DebugBranchService
    {
        private readonly string mainDataDirectory;
        private readonly string registryPath;
        private readonly object gate = new object();

        public DebugBranchService(string mainDataDirectory)
        {
            this.mainDataDirectory = mainDataDirectory ?? throw new ArgumentNullException("mainDataDirectory");
            Directory.CreateDirectory(mainDataDirectory);
            registryPath = Path.Combine(mainDataDirectory, "debug-branches.json");
        }

        public List<DebugBranchRecord> List()
        {
            lock (gate) return Load();
        }

        /// <summary>fork 一份主线快照。freshMemory=true 时清空分支的记忆层（事实/认知/Tag/复盘状态），便于从头回放对比。</summary>
        public DebugBranchRecord Fork(string fromDay, string note, bool freshMemory)
        {
            lock (gate)
            {
                var records = Load();
                var id = "debug-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                var branchDir = Path.Combine(mainDataDirectory, id);
                Directory.CreateDirectory(branchDir);

                var brainframeSource = Path.Combine(mainDataDirectory, "tracesoul2-brainframe.sqlite3");
                var brainframeTarget = Path.Combine(branchDir, "tracesoul2-brainframe.sqlite3");
                if (File.Exists(brainframeSource))
                {
                    VacuumInto(brainframeSource, brainframeTarget);
                    if (freshMemory) WipeMemoryLayer(brainframeTarget);
                }

                var migrationSource = Path.Combine(mainDataDirectory, "migration.sqlite3");
                var migrationTarget = Path.Combine(branchDir, "migration.sqlite3");
                if (File.Exists(migrationSource))
                {
                    VacuumInto(migrationSource, migrationTarget);
                    if (freshMemory) WipeMigrationState(migrationTarget);
                }

                var vectorsSource = Path.Combine(mainDataDirectory, "tracesoul2-vectors.sqlite3");
                if (File.Exists(vectorsSource))
                    VacuumInto(vectorsSource, Path.Combine(branchDir, "tracesoul2-vectors.sqlite3"));

                var providersSource = Path.Combine(mainDataDirectory, "llm-providers.json");
                if (File.Exists(providersSource))
                    File.Copy(providersSource, Path.Combine(branchDir, "llm-providers.json"));
                CopyRoleFile(mainDataDirectory, branchDir, IdentityCardLogic.SeedFileName);

                var record = new DebugBranchRecord
                {
                    id = id,
                    fromDay = string.IsNullOrWhiteSpace(fromDay) ? string.Empty : fromDay.Trim(),
                    createdAt = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    dataDirectory = branchDir,
                    freshMemory = freshMemory,
                    note = (note ?? string.Empty).Trim()
                };
                records.RemoveAll(x => x != null && x.id == id);
                records.Add(record);
                Save(records);
                return record;
            }
        }

        public DebugBranchRecord Get(string id)
        {
            lock (gate) return Load().FirstOrDefault(x => x != null && string.Equals(x.id, id, StringComparison.OrdinalIgnoreCase));
        }

        public bool Destroy(string id)
        {
            lock (gate)
            {
                var records = Load();
                var record = records.FirstOrDefault(x => x != null && string.Equals(x.id, id, StringComparison.OrdinalIgnoreCase));
                if (record == null) return false;
                records.RemoveAll(x => x != null && string.Equals(x.id, id, StringComparison.OrdinalIgnoreCase));
                Save(records);
                if (!string.IsNullOrWhiteSpace(record.dataDirectory) &&
                    Directory.Exists(record.dataDirectory) &&
                    IsInsideMainDirectory(record.dataDirectory))
                    Directory.Delete(record.dataDirectory, recursive: true);
                return true;
            }
        }

        private bool IsInsideMainDirectory(string path)
        {
            var root = Path.GetFullPath(mainDataDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var candidate = Path.GetFullPath(path);
            return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>分支切换用：把主目录的全部数据（主库/迁移库/提供商配置）平移到目标目录。</summary>
        public void ForkTo(string mainDir, string targetDir)
        {
            Directory.CreateDirectory(targetDir);
            var brainframeSource = Path.Combine(mainDir, "tracesoul2-brainframe.sqlite3");
            if (File.Exists(brainframeSource))
                VacuumInto(brainframeSource, Path.Combine(targetDir, "tracesoul2-brainframe.sqlite3"));
            var migrationSource = Path.Combine(mainDir, "migration.sqlite3");
            if (File.Exists(migrationSource))
                VacuumInto(migrationSource, Path.Combine(targetDir, "migration.sqlite3"));
            var vectorsSource = Path.Combine(mainDir, "tracesoul2-vectors.sqlite3");
            if (File.Exists(vectorsSource))
                VacuumInto(vectorsSource, Path.Combine(targetDir, "tracesoul2-vectors.sqlite3"));
            var providersSource = Path.Combine(mainDir, "llm-providers.json");
            if (File.Exists(providersSource))
                File.Copy(providersSource, Path.Combine(targetDir, "llm-providers.json"), overwrite: true);
            CopyRoleFile(mainDir, targetDir, IdentityCardLogic.SeedFileName);
        }

        private static void CopyRoleFile(string sourceDirectory, string targetDirectory, string fileName)
        {
            var source = Path.Combine(sourceDirectory, fileName);
            if (File.Exists(source))
                File.Copy(source, Path.Combine(targetDirectory, fileName), overwrite: true);
        }

        private static void VacuumInto(string sourcePath, string targetPath)
        {
            using (var connection = new SQLiteConnection(sourcePath))
            {
                var escaped = targetPath.Replace("'", "''");
                connection.Execute("VACUUM INTO '" + escaped + "'");
            }
        }

        private static void WipeMemoryLayer(string brainframePath)
        {
            using (var connection = new SQLiteConnection(brainframePath))
            {
                connection.RunInTransaction(() =>
                {
                    connection.Execute("DELETE FROM fact_wakes");
                    connection.Execute("DELETE FROM fact_tag_links");
                    connection.Execute("DELETE FROM fact_slices");
                    connection.Execute("DELETE FROM cognition_evidence");
                    connection.Execute("DELETE FROM cognition_edges");
                    connection.Execute("DELETE FROM cognition_cues");
                    connection.Execute("DELETE FROM cognition_tag_links");
                    connection.Execute("DELETE FROM cognition_slices");
                    connection.Execute("DELETE FROM life_tag_examples");
                    connection.Execute("DELETE FROM life_tag_routes");
                    connection.Execute("DELETE FROM life_tags WHERE Origin='sensory'");
                    connection.Execute("DELETE FROM memory_observation_runs");
                });
            }
        }

        private static void WipeMigrationState(string migrationPath)
        {
            using (var connection = new SQLiteConnection(migrationPath))
            {
                connection.RunInTransaction(() =>
                {
                    connection.Execute("DELETE FROM migration_review_state");
                    connection.Execute("DELETE FROM ladder_items");
                });
            }
        }

        private List<DebugBranchRecord> Load()
        {
            if (!File.Exists(registryPath)) return new List<DebugBranchRecord>();
            try
            {
                var loaded = JsonSerializer.Deserialize<List<DebugBranchRecord>>(
                    File.ReadAllText(registryPath), JsonOptions);
                return loaded ?? new List<DebugBranchRecord>();
            }
            catch
            {
                return new List<DebugBranchRecord>();
            }
        }

        private void Save(List<DebugBranchRecord> records)
        {
            File.WriteAllText(registryPath, JsonSerializer.Serialize(records, JsonOptions));
        }

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
    }

    public sealed class DebugBranchRecord
    {
        public string id { get; set; }
        public string fromDay { get; set; }
        public string createdAt { get; set; }
        public string dataDirectory { get; set; }
        public bool freshMemory { get; set; }
        public string note { get; set; }
    }
}
