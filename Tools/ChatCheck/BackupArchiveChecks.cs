using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SQLite;
using TraceSoul2.Host;

internal static partial class Program
{
    private static async Task RunBackupArchiveChecksAsync()
    {
        var temp = Path.Combine(Path.GetTempPath(), "tracesoul-archive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var root = Path.Combine(temp, "Data"); var souls = Path.Combine(root, "souls");
            var soul = Path.Combine(souls, "main"); var plugins = Path.Combine(temp, "Plugins");
            var pluginData = Path.Combine(temp, "plugins_data"); var extra = Path.Combine(temp, "voices");
            foreach (var dir in new[] { root, soul, plugins, pluginData, extra }) Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(root, "home.json"), "{\"activeSoul\":\"main\",\"pluginsDirectory\":\"/old/plugins\"}");
            File.WriteAllText(Path.Combine(root, "control-auth.json"), "{\"testOnly\":true}");
            File.WriteAllText(Path.Combine(soul, "llm-providers.json"), "{\"synthetic\":\"private-test-key\"}");
            File.WriteAllText(Path.Combine(plugins, "test.dll"), "custom-plugin");
            File.WriteAllText(Path.Combine(pluginData, "config.json"), "{\"test\":true}");
            File.WriteAllText(Path.Combine(pluginData, "tsconfig.json"), "{ // JSONC dependency config\n\"compilerOptions\": {},\n}");
            File.WriteAllBytes(Path.Combine(extra, "reference.wav"), RandomNumberGenerator.GetBytes(1200000));
            Directory.CreateDirectory(Path.Combine(root, "updates")); File.WriteAllText(Path.Combine(root, "updates", "not-included"), "cache");
            var dbPath = Path.Combine(soul, "tracesoul2-brainframe.sqlite3");
            using var live = new SQLiteConnection(dbPath);
            live.ExecuteScalar<string>("PRAGMA journal_mode=WAL"); live.ExecuteScalar<int>("PRAGMA wal_autocheckpoint=0");
            live.Execute("CREATE TABLE items (id INTEGER PRIMARY KEY, value TEXT)");
            live.Execute("INSERT INTO items(value) VALUES ('synthetic-identity')");
            var layout = new TraceHomeLayout { Root = root, SoulsDirectory = souls, SoulDirectory = soul, SoulId = "main", PluginsDirectory = plugins, PluginsDataDirectory = pluginData };
            var oldArchive = Path.Combine(soul, "old-archives"); Directory.CreateDirectory(oldArchive);
            File.WriteAllText(Path.Combine(oldArchive, "original.tsbackup"), "retained encrypted package fixture");
            var pathReport = new RuntimePathReport();
            RuntimePathRepair.CopyVerified(oldArchive, Path.Combine(root, "retained-archives"), "archives", pathReport);
            File.WriteAllText(Path.Combine(root, RuntimePathRepair.ReportName), JsonSerializer.Serialize(pathReport, BackupArchiveFormat.Json));
            using var service = new BackupArchiveService(layout);
            var settings = new BackupSettings { Enabled = true, IntervalHours = 168, KeepCount = 1, Extras = new Dictionary<string, string> { ["voice"] = extra } };
            service.Configure(settings);
            var keyFile = Path.Combine(temp, "recovery.key"); File.WriteAllBytes(keyFile, service.ExportKey());
            var key = Convert.FromBase64String(File.ReadAllText(keyFile).Trim());
            Require(!service.Start(true), "保存计划不会立即执行或重复触发");
            string OriginalStamp(string path)
            {
                using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
                return Convert.ToHexString(SHA256.HashData(stream))+new FileInfo(path).LastWriteTimeUtc.Ticks;
            }
            var originalDbFiles=Directory.GetFiles(soul,"*.sqlite3*").ToDictionary(p=>p,OriginalStamp);
            Require(service.Start(), "可手动生成");
            await service.WaitAsync();
            var status = JsonSerializer.SerializeToElement(service.Status());
            Require(status.GetProperty("phase").GetString() == "completed", "存档成功：" + service.LastFailure);
            Require(originalDbFiles.Count==Directory.GetFiles(soul,"*.sqlite3*").Length&&originalDbFiles.All(p=>p.Value==OriginalStamp(p.Key)),"采集不修改源库/WAL/SHM或创建旁文件");
            var packages = Path.Combine(root, "archives", "packages");
            Require(live.ExecuteScalar<string>("PRAGMA journal_mode") == "wal", "原数据库仍使用WAL模式");
            var package = Directory.GetFiles(packages, "*.tsbackup").Single();
            Require(!System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(package)).Contains("private-test-key"), "成品不包含明文配置");
            var id = Path.GetFileNameWithoutExtension(package);
            Require(JsonSerializer.SerializeToElement(service.Verify(id)).GetProperty("valid").GetBoolean(), "成品恢复预检");
            var restored = Path.Combine(temp, "restored");
            var manifest = BackupArchiveFormat.Extract(package, keyFile, restored, CancellationToken.None);
            using (var restoredDb = new SQLiteConnection(Path.Combine(restored, "Data", "souls", "main", "tracesoul2-brainframe.sqlite3")))
                Require(restoredDb.ExecuteScalar<string>("SELECT value FROM items") == "synthetic-identity", "WAL中的已提交数据进入独立副本");
            Require(File.ReadAllText(Path.Combine(restored, "Plugins", "test.dll")) == "custom-plugin", "自定义插件可恢复");
            Require(File.ReadAllText(Path.Combine(restored,"plugins_data","tsconfig.json"))==File.ReadAllText(Path.Combine(pluginData,"tsconfig.json")),"JSONC依赖配置原样保留");
            Require(File.Exists(Path.Combine(restored, "Extras", "voice", "reference.wav")), "外部音色资料可恢复");
            Require(!manifest.Files.Any(f => f.Path.Contains("archive-recovery") || f.Path.Contains("updates/") || f.Path.EndsWith("-wal")), "恢复密钥、缓存与WAL不装入成品");
            Require(!manifest.Files.Any(f => f.Path.Contains("old-archives/") || f.Path.EndsWith(RuntimePathRepair.ReportName)), "保留的迁移原件不重复进入存档或带入旧路径别名");
            Require(File.Exists(Path.Combine(oldArchive,"original.tsbackup")),"生成存档不清理旧迁移目录");
            var closedDir=Path.Combine(temp,"closed-db");Directory.CreateDirectory(closedDir);
            var closed=Path.Combine(closedDir,"closed.sqlite3");
            using(var closedDb=new SQLiteConnection(closed)){closedDb.Execute("CREATE TABLE example(id INTEGER)");closedDb.ExecuteScalar<string>("PRAGMA journal_mode=WAL");}
            var closedHash=BackupArchiveFormat.Hash(closed);
            BackupArchiveService.CopyDatabaseSnapshot(closed,Path.Combine(temp,"closed-copy.sqlite3"));
            Require(Directory.GetFiles(closedDir).Length==1&&BackupArchiveFormat.Hash(closed)==closedHash,"关闭的WAL库采集不在原目录生成SHM/WAL");
            MustReject(()=>BackupArchiveService.CopyDatabaseSnapshot(closed,Path.Combine(temp,"closed-copy.sqlite3")),"数据库副本不覆盖现有文件");
            var rollback=Path.Combine(closedDir,"rollback.sqlite3");
            using(var transactionDb=new SQLiteConnection(rollback))
            {
                transactionDb.Execute("CREATE TABLE entries(value TEXT)");transactionDb.Execute("INSERT INTO entries VALUES ('committed')");
                transactionDb.Execute("PRAGMA cache_size=5");transactionDb.BeginTransaction();
                for(var i=0;i<64;i++)transactionDb.Execute("INSERT INTO entries VALUES (?)",new string('x',8000));
                Require(File.Exists(rollback+"-journal"),"构造仍有未提交内容的回滚日志");
                var rollbackCopy=Path.Combine(temp,"rollback-copy.sqlite3");BackupArchiveService.CopyDatabaseSnapshot(rollback,rollbackCopy);
                using(var recovered=new SQLiteConnection(rollbackCopy))Require(recovered.ExecuteScalar<int>("SELECT count(*) FROM entries")==1,"暂存副本恢复回滚日志，不纳入未提交写入");
                Require(transactionDb.ExecuteScalar<int>("SELECT count(*) FROM entries")==65,"源事务不受副本恢复影响");transactionDb.Rollback();
            }
            using (var portableHome = JsonDocument.Parse(File.ReadAllText(Path.Combine(restored, "Data", "home.json"))))
                Require(portableHome.RootElement.GetProperty("pluginsDirectory").GetString() == "../Plugins", "只在恢复副本改写相对路径");
            Require(File.ReadAllText(Path.Combine(root, "home.json")).Contains("/old/plugins"), "原配置未改动");
            MustReject(() => BackupArchiveFormat.Extract(package, keyFile, restored, CancellationToken.None), "拒绝覆盖已有恢复目录");
            var zip = Path.Combine(temp, "verified.zip"); BackupArchiveFormat.Decrypt(package, zip, key, CancellationToken.None);
            var tampered = Path.Combine(temp, "tampered.tsbackup");
            var bytes = File.ReadAllBytes(package); bytes[40] ^= 1; File.WriteAllBytes(tampered, bytes);
            MustReject(() => BackupArchiveFormat.Extract(tampered, keyFile, Path.Combine(temp, "bad"), CancellationToken.None), "篡改被拒绝");
            Require(!Directory.Exists(Path.Combine(temp, "bad")), "篡改失败不留下恢复目录");
            File.WriteAllBytes(tampered, File.ReadAllBytes(package).SkipLast(20).ToArray());
            MustReject(() => BackupArchiveFormat.Extract(tampered, keyFile, Path.Combine(temp, "truncated"), CancellationToken.None), "缺失终止块被拒绝");
            File.WriteAllBytes(tampered, File.ReadAllBytes(package).Concat(new byte[] { 1 }).ToArray());
            MustReject(() => BackupArchiveFormat.Extract(tampered, keyFile, Path.Combine(temp, "trailing"), CancellationToken.None), "尾随数据被拒绝");
            var wrongKey = Path.Combine(temp, "wrong.key"); File.WriteAllText(wrongKey, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            MustReject(() => BackupArchiveFormat.Extract(package, wrongKey, Path.Combine(temp, "wrong"), CancellationToken.None), "错误密钥被拒绝");
            foreach (var badPath in new[] { "../escape", "Data/../escape", "/absolute", "Data/colon:bad", "Data\\bad", "Data/CON.txt" })
                MustReject(() => BackupArchiveFormat.SafeName(badPath), "拒绝路径穿越");
            var malicious = Path.Combine(temp, "malicious.zip");
            using (var badZip = ZipFile.Open(malicious, ZipArchiveMode.Create)) { using var w = new StreamWriter(badZip.CreateEntry("../escape").Open()); w.Write("bad"); }
            MustReject(() => BackupArchiveFormat.ValidateZip(malicious, CancellationToken.None), "校验先拒绝不安全ZIP条目");
            MustReject(() => service.Configure(new BackupSettings { Directory = soul }), "拒绝把存档放进角色目录");
            MustReject(() => service.Configure(new BackupSettings { Directory = temp }), "拒绝把存档放在家目录上级");
            MustReject(() => service.Configure(new BackupSettings { IntervalHours = 0 }), "拒绝无效周期");
            MustReject(() => service.OpenPackage("../escape"), "下载仅接受已有存档ID");
            // A blocked snapshot proves a duplicate trigger cannot start another job or change retention.
            var entered = new TaskCompletionSource<bool>(); var release = new TaskCompletionSource<bool>();
            using var blocked = new BackupArchiveService(layout, async (action, token) => { entered.SetResult(true); await release.Task; action(); });
            Require(blocked.Start(), "首次触发"); await entered.Task;
            Require(!blocked.Start(), "并发触发被去重");
            MustReject(() => blocked.Configure(new BackupSettings()), "执行中拒绝变更目录");
            release.SetResult(true); await blocked.WaitAsync();
            using (var reopened = new BackupArchiveService(layout))
            {
                Require(!reopened.Start(true), "重启保留最后尝试时间");
                var lastAttempt=JsonSerializer.SerializeToElement(reopened.Status()).GetProperty("settings").GetProperty("LastAttemptUtc").GetDateTimeOffset();
                Require(!reopened.Start(true,lastAttempt.AddHours(167)),"每周计划未满168小时不触发");
                Require(reopened.Start(true,lastAttempt.AddHours(168)), "满168小时到期只补一次"); await reopened.WaitAsync();
                Require(Directory.GetFiles(packages, "*.tsbackup").Length == 1, "新包完整校验成功后只保留一份");
                var before = Directory.GetFiles(packages, "*.tsbackup").OrderBy(x => x).ToArray();
                File.WriteAllText(Path.Combine(pluginData, "config.json"), "invalid JSON");
                Require(reopened.Start(), "失败用例启动"); await reopened.WaitAsync();
                Require(Directory.GetFiles(packages, "*.tsbackup").OrderBy(x => x).SequenceEqual(before), "失败不清理已有成品");
                Require(!reopened.Start(true), "失败也记录尝试，避免每分钟重复执行");
                Require(!Directory.EnumerateFileSystemEntries(Path.Combine(root, "archives", "work")).Any(), "成功与失败均清理明文暂存");
                File.WriteAllText(Path.Combine(pluginData,"config.json"),"{}");
                var futureMetadata=Directory.GetFiles(packages,"*.tsbackup.json").Single();
                var future=JsonSerializer.Deserialize<BackupPackage>(File.ReadAllText(futureMetadata),BackupArchiveFormat.Json);
                future.CreatedUtc=DateTimeOffset.UtcNow.AddYears(1);File.WriteAllText(futureMetadata,JsonSerializer.Serialize(future,BackupArchiveFormat.Json));
                Require(reopened.Start(),"系统时钟回拨后仍能创建新包");await reopened.WaitAsync();
                Require(!File.Exists(futureMetadata)&&Directory.GetFiles(packages,"*.tsbackup").Length==1,"保留实际新验证包，不按错误的未来时间保留旧包");
                var good = Directory.GetFiles(packages,"*.tsbackup").Single(); var goodHash = BackupArchiveFormat.Hash(good);
                reopened.CandidateWritten = candidate => { using var file = new FileStream(candidate,FileMode.Open,FileAccess.ReadWrite);file.Position=40;var value=file.ReadByte();file.Position=40;file.WriteByte((byte)(value^1)); };
                Require(reopened.Start(),"模拟新加密包写坏");await reopened.WaitAsync();
                Require(JsonSerializer.SerializeToElement(reopened.Status()).GetProperty("phase").GetString()=="failed"&&File.Exists(good)&&BackupArchiveFormat.Hash(good)==goodHash,"新加密包损坏不替换唯一旧包");
                Require(Directory.GetFiles(packages,"*.tsbackup").Length==1&&Directory.GetFiles(packages,"*.tsbackup.json").Length==1,"损坏候选及其元数据不作为成品保留");
            }
            File.WriteAllText(Path.Combine(pluginData, "config.json"), "{}");
            var keyPath = Path.Combine(root, "archive-recovery.key"); var savedKey = File.ReadAllText(keyPath);
            File.Delete(keyPath);
            using (var missingKey = new BackupArchiveService(layout))
                MustReject(() => missingKey.Start(), "密钥丢失时不生成替代密钥");
            Require(!File.Exists(keyPath), "保留旧存档的原密钥关系");
            File.WriteAllText(keyPath, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            using (var replacedKey = new BackupArchiveService(layout))
                MustReject(() => replacedKey.Start(), "更换密钥被拒绝");
            File.WriteAllText(keyPath, savedKey);
            var custom = Path.Combine(temp, "custom-archives");
            service.Configure(new BackupSettings { Directory = custom });
            Require(service.Start(), "支持自定义持久目录"); await service.WaitAsync();
            Require(Directory.GetFiles(Path.Combine(custom,"packages"),"*.tsbackup").Length == 1, "新存档进入指定目录");
            Require(Directory.GetFiles(packages,"*.tsbackup").Length == 1, "修改保存目录不移动或清理原存档");
            var legacyLayout = new TraceHomeLayout { Root = root, SoulsDirectory = root, SoulDirectory = soul, PluginsDirectory = plugins, PluginsDataDirectory = pluginData, UsedLegacyDataEnv = true };
            using (var legacy = new BackupArchiveService(legacyLayout))
            {
                legacy.Configure(new BackupSettings()); Require(legacy.Start(), "旧DATA布局支持默认存档目录"); await legacy.WaitAsync();
                Require(JsonSerializer.SerializeToElement(legacy.Status()).GetProperty("phase").GetString() == "completed", "旧布局不递归收集家目录");
            }
            foreach (var target in new[] { extra, Path.Combine(temp,"missing-link-target") })
            {
                var link=Path.Combine(temp,Guid.NewGuid().ToString("N"));
                try { Directory.CreateSymbolicLink(link,target); }
                catch (UnauthorizedAccessException) { continue; }
                catch (PlatformNotSupportedException) { continue; }
                catch (IOException) { continue; }
                MustReject(()=>service.Configure(new BackupSettings { Directory=link }), "拒绝正常和悬空符号链接");
            }
            CryptographicOperations.ZeroMemory(key);
            await RunSingleArchiveUpdateProtectionChecksAsync(temp,plugins,pluginData);
            Console.WriteLine("Backup archive checks passed: single verified package, failed/corrupt candidate preservation, WAL snapshots, recovery, scheduling and update protection/confirmation.");
        }
        finally { if (Directory.Exists(temp)) Directory.Delete(temp, true); }
    }

    private static async Task RunSingleArchiveUpdateProtectionChecksAsync(string temp,string plugins,string pluginData)
    {
        var root=Path.Combine(temp,"update-protection");var soul=Path.Combine(root,"souls","main");Directory.CreateDirectory(soul);
        File.WriteAllText(Path.Combine(root,"home.json"),"{\"activeSoul\":\"main\"}");
        using(var db=new SQLiteConnection(Path.Combine(soul,"tracesoul2-brainframe.sqlite3"))){db.Execute("CREATE TABLE content(value TEXT)");db.Execute("INSERT INTO content VALUES ('good-before-update')");}
        var layout=new TraceHomeLayout{Root=root,SoulsDirectory=Path.Combine(root,"souls"),SoulDirectory=soul,SoulId="main",PluginsDirectory=plugins,PluginsDataDirectory=pluginData};
        var current="1.0.0";
        string pinned,hash;
        using(var old=new BackupArchiveService(layout,version:()=>current))
        {
            old.Configure(new BackupSettings{Enabled=true});
            Require(!old.Start(true,DateTimeOffset.UtcNow.AddDays(6)),"每周默认计划六天时不触发");
            Require(old.Start(true,DateTimeOffset.UtcNow.AddDays(8)),"实际每周计划到期触发单存档");await old.WaitAsync();
            await old.PrepareForUpdateAsync("2.0.0",CancellationToken.None);
            var state=JsonSerializer.SerializeToElement(old.Status());
            Require(state.GetProperty("settings").GetProperty("KeepCount").GetInt32()==1,"计划固定只保留一份");
            var protection=state.GetProperty("protection");Require(protection.GetProperty("active").GetBoolean(),"更新交接前锁定已验证的存档");
            pinned=protection.GetProperty("packageId").GetString();
            var path=Path.Combine(root,"archives","packages",pinned+".tsbackup");hash=BackupArchiveFormat.Hash(path);
            Require(!old.Start(true,DateTimeOffset.UtcNow.AddDays(1)),"更新交接等待期间定时不覆盖");
            MustReject(()=>old.Start(),"手动存档也不能绕过更新保护");
            MustReject(()=>old.Configure(new BackupSettings{Directory="bypass",ProtectedPackageId=null}),"改目录不能绕过更新保护");
        }
        current="2.0.0";
        using(var upgraded=new BackupArchiveService(layout,version:()=>current))
        {
            Require(!upgraded.Start(true,DateTimeOffset.UtcNow.AddDays(2)),"重启后仍保护更新前唯一存档");
            var path=Path.Combine(root,"archives","packages",pinned+".tsbackup");
            using(var db=new SQLiteConnection(Path.Combine(soul,"tracesoul2-brainframe.sqlite3")))db.Execute("DELETE FROM content");
            Require(!upgraded.Start(true,DateTimeOffset.UtcNow.AddDays(3))&&BackupArchiveFormat.Hash(path)==hash,"数据库结构仍好但记录被更新破坏时不覆盖旧包");
            await upgraded.PrepareForUpdateAsync("2.0.1",CancellationToken.None);
            Require(BackupArchiveFormat.Hash(path)==hash,"纠错更新继续沿用原好包，不备份坏状态替换它");
            MustReject(()=>upgraded.ConfirmVersion("1.0.0",pinned),"旧版本页面不能确认新版本");
            MustReject(()=>upgraded.ConfirmVersion(current,"stale-package"),"确认必须绑定当前保护包");
            upgraded.Configure(new BackupSettings{Enabled=true,AcceptedHostVersion=current,ProtectedPackageId=null});
            Require(!upgraded.Start(true,DateTimeOffset.UtcNow.AddDays(4)),"设置写入不清除内部更新保护");
            upgraded.ConfirmVersion(current,pinned);
            Require(File.Exists(path)&&!upgraded.Start(true),"确认不立即删除原包，按间隔再执行");
            using(var db=new SQLiteConnection(Path.Combine(soul,"tracesoul2-brainframe.sqlite3")))db.Execute("INSERT INTO content VALUES ('repaired-and-accepted')");
            Require(!upgraded.Start(true,DateTimeOffset.UtcNow.AddDays(6)),"解除保护后重新等待一周");
            Require(upgraded.Start(true,DateTimeOffset.UtcNow.AddDays(8)),"确认后恢复每周自动计划");await upgraded.WaitAsync();
            Require(!File.Exists(path)&&Directory.GetFiles(Path.Combine(root,"archives","packages"),"*.tsbackup").Length==1,"新版本好包完成验证后替换旧包");
        }
        current="3.0.0";
        using(var manualUpdate=new BackupArchiveService(layout,version:()=>current))
            Require(!manualUpdate.Start(true,DateTimeOffset.UtcNow.AddDays(5))&&JsonSerializer.SerializeToElement(manualUpdate.Status()).GetProperty("protection").GetProperty("active").GetBoolean(),"外部更新版本变化也保护已有旧版本包");
        File.WriteAllText(Path.Combine(root,"archive-settings.json"),"invalid JSON");
        using(var repairedPlan=new BackupArchiveService(layout,version:()=>current))
        {
            repairedPlan.Configure(new BackupSettings{Enabled=true});
            Require(!repairedPlan.Start(true,DateTimeOffset.UtcNow.AddDays(5)),"计划损坏后重新保存也不绕过旧版本包保护");
        }
        var failedRoot=Path.Combine(temp,"failed-update-archive");var badSoul=Path.Combine(failedRoot,"souls","main");Directory.CreateDirectory(badSoul);
        var badLayout=new TraceHomeLayout{Root=failedRoot,SoulsDirectory=Path.Combine(failedRoot,"souls"),SoulDirectory=badSoul,PluginsDirectory=plugins,PluginsDataDirectory=pluginData};
        using(var bad=new BackupArchiveService(badLayout))
        {
            var refused=false;try{await bad.PrepareForUpdateAsync("9.0.0",CancellationToken.None);}catch(InvalidOperationException){refused=true;}
            Require(refused,"更新前缺失主库时不能继续更新");
        }
    }

    private static void MustReject(Action action, string message)
    {
        try { action(); } catch { return; }
        throw new Exception("Backup check failed: " + message);
    }
}
