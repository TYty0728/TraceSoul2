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
