using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SQLite;
using TraceSoul2.Host;

internal static partial class Program
{
    private static async Task RunDailyBuildPreflightChecksAsync()
    {
        await RunReviewOutputChecksAsync();
        var states = new[]
        {
            new DailyReviewState { DayKey = "2026-09-17", Status = "done" },
            new DailyReviewState { DayKey = "2026-09-18", Status = "failed", Error = "语言模型 API 402: secret" },
            new DailyReviewState { DayKey = "2026-09-19", Status = "running" }
        };
        var plan = DailyBuildPreflight.Plan("2026-09-20", "2026-09-20",
            new[] { "2026-09-17", "2026-09-19", "2026-09-21" }, states, Array.Empty<DailyBuildAttempt>());
        Require(plan.Days.SequenceEqual(new[] { "2026-09-18", "2026-09-19", "2026-09-20" }),
            "历史失败/中断日即使没有 live Moment 也必须排在目标日前；完成日和未结束日不能重复构建");
        Require(plan.HistoricalFailures.Count == 2 && plan.HistoricalFailures[0].Reason.Contains("402") &&
                !plan.HistoricalFailures[0].Reason.Contains("secret"), "检查应展示旧日原因并去除原始敏感错误正文");
        var called = new List<string>();
        var success = await DailyBuildPreflight.RunInOrderAsync(plan.Days, day =>
        { called.Add(day); return Task.FromResult(day == "2026-09-18" ? 1 : 0); });
        Require(!success && called.SequenceEqual(new[] { "2026-09-18" }), "历史失败时不允许执行当前日或后续日期");
        called.Clear();
        success = await DailyBuildPreflight.RunInOrderAsync(plan.Days.AsEnumerable().Reverse().Concat(plan.Days), day =>
        { called.Add(day); return Task.FromResult(0); });
        Require(success && called.SequenceEqual(plan.Days), "历史解决后才按顺序执行目标日，重复日期只执行一次");
        var empty = DailyBuildPreflight.Plan("2026-09-17", "2026-09-20", Array.Empty<string>(), states, Array.Empty<DailyBuildAttempt>());
        Require(empty.Days.Count == 0 && !empty.Ready, "已经完成的目标不能再次产生模型请求");
        var publicOnly = DailyBuildPreflight.Plan("2026-09-17", "2026-09-20", Array.Empty<string>(), states,
            Array.Empty<DailyBuildAttempt>(), new[] { "2026-09-17" });
        Require(publicOnly.Days.SequenceEqual(new[] { "2026-09-17" }),
            "私密已完成日仍能单独补建后来到达的公开经历，不重跑已完成私密复盘");
        var slicesOnly = DailyBuildPreflight.Plan("2026-09-17", "2026-09-20", Array.Empty<string>(), states,
            Array.Empty<DailyBuildAttempt>(), pendingSlices: new[] { "2026-09-17" });
        Require(slicesOnly.Days.SequenceEqual(new[] { "2026-09-17" }), "没有新聊天也要补齐待复盘切片，完成日的迟到切片不会丢失");
        var derived = new[] { new DailyReviewState { DayKey = "2026-09-19", Status = "pending", Error = "当天回望待整理" } };
        var automatic = DailyBuildPreflight.Plan("2026-09-20", "2026-09-20", Array.Empty<string>(), derived,
            Array.Empty<DailyBuildAttempt>(), pendingSlices: new[] { "2026-09-19" }, includePendingReviews: false);
        Require(automatic.HistoricalFailures.Count == 0 && automatic.PendingReviews.Count == 1 &&
            automatic.Days.SequenceEqual(new[] { "2026-09-20" }),
            "待整理正文不变成历史致命错误，未完成切片也不触发自动反复付费，下一日可继续");
        var manual = DailyBuildPreflight.Plan("2026-09-20", "2026-09-20", Array.Empty<string>(), derived,
            Array.Empty<DailyBuildAttempt>());
        Require(manual.Days.SequenceEqual(new[] { "2026-09-19", "2026-09-20" }), "显式启动仍列出并续做待整理日期，不冒充全部成功");
        try
        {
            DailyBuildPreflight.Plan("2026-09-21", "2026-09-20", Array.Empty<string>(), states, Array.Empty<DailyBuildAttempt>());
            throw new Exception("Expected invalid open day");
        }
        catch (ArgumentException) { }

        var root = Path.Combine(Path.GetTempPath(), "daily-preflight-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "migration.sqlite3");
        try
        {
            Require(DailyBuildHistory.ReadStates(path).Count == 0 && !File.Exists(path), "预检不能创建或修改迁移库");
            var brain = Path.Combine(root, "brain.sqlite3");
            Require(DailyBuildPreflight.LegacyRecoveryBlocker(brain, "2026-09-18") == null && !File.Exists(brain),
                "恢复预检不创建主库");
            var started = new DateTimeOffset(2026, 9, 18, 4, 0, 0, TimeSpan.FromHours(8)).ToUnixTimeMilliseconds();
            using (var db = new SQLiteConnection(brain))
            {
                db.Execute("CREATE TABLE moments (Id TEXT PRIMARY KEY, CreatedUnixMs INTEGER, MemoryVisibility TEXT, MemoryStatus TEXT)");
                db.Execute("CREATE TABLE event_entries (Id TEXT PRIMARY KEY, SourceMomentId TEXT)");
                db.Execute("CREATE TABLE plugin_documents (PluginId TEXT, DocumentKey TEXT, Json TEXT)");
                db.Execute("INSERT INTO moments VALUES ('saved',?,'private','live')", started);
                db.Execute("INSERT INTO event_entries VALUES ('legacy-partial','saved')");
            }
            var bytes = File.ReadAllBytes(brain);
            Require(DailyBuildPreflight.LegacyRecoveryBlocker(brain, "2026-09-18")?.Contains("直接重跑仍会被拦住") == true &&
                DailyBuildPreflight.LegacyRecoveryBlocker(brain, "2026-09-19") == null && bytes.SequenceEqual(File.ReadAllBytes(brain)),
                "旧版部分事件在预检即阻断，日期隔离且主库字节不变");
            using (var db = new SQLiteConnection(brain))
                db.Execute("INSERT INTO plugin_documents VALUES ('daily-core-progress-v1','source:2026-09-18','{}')");
            Require(DailyBuildPreflight.LegacyRecoveryBlocker(brain, "2026-09-18") == null,
                "已有逐批进度不误当旧版数据，来源内容仍交迁移器严格核验");
            using (var db = new SQLiteConnection(brain))
            {
                db.Execute("DELETE FROM plugin_documents");
                db.Execute("INSERT INTO plugin_documents VALUES ('daily-review-recovery-v1','core:2026-09-18',?)", "{\"MomentIds\":[\"saved\"]}");
            }
            Require(DailyBuildPreflight.LegacyRecoveryBlocker(brain, "2026-09-18") == null,
                "已有核心检查点覆盖的事件不误拦身份或排序续做");
            using (var db = new SQLiteConnection(brain))
            {
                db.Execute("INSERT INTO moments VALUES ('late',?,'private','live')", started + 1);
                db.Execute("INSERT INTO event_entries VALUES ('late-partial','late')");
            }
            Require(DailyBuildPreflight.LegacyRecoveryBlocker(brain, "2026-09-18") != null,
                "核心之外的迟到来源已有事件却无进度仍须阻断，不能由旧核心绕过");
            using (var db = new SQLiteConnection(brain)) db.Execute("UPDATE moments SET MemoryVisibility='public' WHERE Id='late'");
            Require(DailyBuildPreflight.LegacyRecoveryBlocker(brain, "2026-09-18") == null,
                "独立公开整理不误当私密日终旧版部分事件");
            using (var db = new SQLiteConnection(brain))
            {
                db.Execute("UPDATE moments SET MemoryVisibility='private',MemoryStatus='built' WHERE Id='late'");
                db.Execute("DELETE FROM plugin_documents");
                db.Execute("UPDATE moments SET MemoryStatus='operational' WHERE Id='saved'");
            }
            Require(DailyBuildPreflight.LegacyRecoveryBlocker(brain, "2026-09-18") == null,
                "已归档和操作记录不会触发未完成来源保护");
            using (var db = new SQLiteConnection(path))
            {
                db.Execute("CREATE TABLE migration_review_state (DayKey TEXT PRIMARY KEY, Status TEXT, Error TEXT, UpdatedUnixMs INTEGER)");
                db.Execute("INSERT INTO migration_review_state VALUES (?,?,?,?)", "2026-09-18", "failed", "API 500: upstream", 1);
                var legacy = DailyBuildHistory.ReadStates(path).Single();
                var first = DailyBuildHistory.Start(db, "2026-09-18", legacy);
                DailyBuildHistory.Stage(db, first, "事件观察（第 2 批）");
                DailyBuildHistory.Finish(db, first, "failed", "模型输出缺少 perception_summary");
                var second = DailyBuildHistory.Start(db, "2026-09-18", legacy);
                DailyBuildHistory.Stage(db, second, "细节生成");
                DailyBuildHistory.Finish(db, second, "failed", "API 402: Insufficient Balance");
                var attempts = DailyBuildHistory.ReadAttempts(path, "2026-09-20");
                Require(attempts.Count == 3 && attempts.Count(x => x.Error.Contains("500")) == 1 &&
                        attempts.Any(x => x.Id == first && x.Stage.Contains("第 2 批") && x.Error.Contains("perception_summary")) &&
                        attempts.Any(x => x.Id == second && x.Error.Contains("402")),
                    "旧版错误和每次新失败及阶段必须独立保存，后来的余额错误不能覆盖首次格式错误");
                var interrupted = DailyBuildHistory.Start(db, "2026-09-18", legacy);
                var final = DailyBuildHistory.Start(db, "2026-09-18", legacy);
                DailyBuildHistory.Finish(db, final, "done", null);
                attempts = DailyBuildHistory.ReadAttempts(path, "2026-09-20");
                Require(attempts.Single(x => x.Id == interrupted).Status == "interrupted" &&
                        attempts.Single(x => x.Id == final).Status == "done", "异常退出记录也应保留，成功只完成当前尝试");
            }
            var before = File.GetLastWriteTimeUtc(path);
            DailyBuildHistory.ReadStates(path);
            DailyBuildHistory.ReadAttempts(path, "2026-09-20");
            Require(File.GetLastWriteTimeUtc(path) == before, "日构建检查必须只读，不清空错误或完成标记");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine("Daily build preflight checks passed: history-first, stop-on-failure, read-only inspection and attempt history.");
    }
}
