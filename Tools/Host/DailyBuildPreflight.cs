using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using SQLite;

namespace TraceSoul2.Host
{
    [Table("migration_review_attempts")]
    public sealed class DailyBuildAttempt
    {
        [PrimaryKey] public string Id { get; set; }
        public string DayKey { get; set; }
        public string Status { get; set; }
        public string Stage { get; set; }
        public string Error { get; set; }
        public long StartedUnixMs { get; set; }
        public long FinishedUnixMs { get; set; }
    }

    public sealed class DailyReviewState
    {
        public string DayKey { get; set; }
        public string Status { get; set; }
        public string Error { get; set; }
        public long UpdatedUnixMs { get; set; }
    }

    // 与日构建共用 migration.sqlite3。读取不建表、不清状态，不调用模型。
    public static class DailyBuildHistory
    {
        public static List<DailyReviewState> ReadStates(string path)
        {
            if (!File.Exists(path)) return new List<DailyReviewState>();
            using var db = new SQLiteConnection(path, SQLiteOpenFlags.ReadOnly);
            db.BusyTimeout = TimeSpan.FromSeconds(5);
            if (!HasTable(db, "migration_review_state")) return new List<DailyReviewState>();
            return db.Query<DailyReviewState>("SELECT DayKey,Status,Error,UpdatedUnixMs FROM migration_review_state ORDER BY DayKey");
        }

        public static List<DailyBuildAttempt> ReadAttempts(string path, string target)
        {
            if (!File.Exists(path)) return new List<DailyBuildAttempt>();
            using var db = new SQLiteConnection(path, SQLiteOpenFlags.ReadOnly);
            db.BusyTimeout = TimeSpan.FromSeconds(5);
            if (!HasTable(db, "migration_review_attempts")) return new List<DailyBuildAttempt>();
            return db.Query<DailyBuildAttempt>("SELECT * FROM migration_review_attempts WHERE DayKey<=? ORDER BY StartedUnixMs DESC LIMIT 30", target);
        }

        private static bool HasTable(SQLiteConnection db, string name)
            => db.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=?", name) > 0;

        public static string Start(SQLiteConnection db, string day, DailyReviewState previous)
        {
            db.CreateTable<DailyBuildAttempt>();
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var id = Guid.NewGuid().ToString("N");
            db.RunInTransaction(() =>
            {
                // 首次升级时，把即将被 running 覆盖的旧错误也保留下来。
                if (previous != null && previous.Status == "failed" &&
                    db.ExecuteScalar<int>("SELECT COUNT(*) FROM migration_review_attempts WHERE DayKey=?", day) == 0)
                    db.Insert(new DailyBuildAttempt { Id = "legacy:" + day, DayKey = day, Status = "failed",
                        Stage = "旧版本未记录阶段", Error = previous.Error,
                        StartedUnixMs = previous.UpdatedUnixMs, FinishedUnixMs = previous.UpdatedUnixMs });
                db.Execute("UPDATE migration_review_attempts SET Status='interrupted', FinishedUnixMs=? WHERE DayKey=? AND Status='running'", now, day);
                db.Insert(new DailyBuildAttempt { Id = id, DayKey = day, Status = "running", Stage = "初始化", StartedUnixMs = now });
            });
            return id;
        }

        public static void Stage(SQLiteConnection db, string id, string stage)
        {
            if (id != null) db.Execute("UPDATE migration_review_attempts SET Stage=? WHERE Id=? AND Status='running'", stage, id);
        }

        public static void Finish(SQLiteConnection db, string id, string status, string error)
        {
            if (id != null) db.Execute("UPDATE migration_review_attempts SET Status=?, Error=?, FinishedUnixMs=? WHERE Id=? AND Status='running'",
                status, error ?? "", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), id);
        }
    }

    public sealed class DailyBuildIssue
    {
        public string Day { get; set; }
        public string Status { get; set; }
        public string Stage { get; set; }
        public string Reason { get; set; }
    }

    public sealed class DailyBuildPlan
    {
        public string Day { get; set; }
        public List<string> Days { get; set; } = new List<string>();
        public List<DailyBuildIssue> HistoricalFailures { get; set; } = new List<DailyBuildIssue>();
        public List<DailyBuildIssue> RecentAttempts { get; set; } = new List<DailyBuildIssue>();
        public List<string> Blockers { get; set; } = new List<string>();
        public string ProviderId { get; set; }
        public string Model { get; set; }
        public float ReviewTemperature { get; set; }
        public bool ProviderOverride { get; set; }
        public bool Busy { get; set; }
        public bool Ready => !Busy && Blockers.Count == 0 && Days.Count > 0;
    }

    public static class DailyBuildPreflight
    {
        public static bool ValidDay(string day) => DateTime.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out _);

        public static DailyBuildPlan Plan(string target, string closedDay, IEnumerable<string> unbuilt,
            IEnumerable<DailyReviewState> states, IEnumerable<DailyBuildAttempt> attempts)
        {
            if (!ValidDay(target) || string.CompareOrdinal(target, closedDay) > 0)
                throw new ArgumentException("请选择已结束的记忆日（yyyy-MM-dd），最晚为 " + closedDay + "。");
            var result = new DailyBuildPlan { Day = target };
            var rows = states.ToList();
            if (rows.Any(x => !ValidDay(x.DayKey))) result.Blockers.Add("历史构建记录包含异常日期，请先检查迁移数据库。");
            rows = rows.Where(x => ValidDay(x.DayKey) && string.CompareOrdinal(x.DayKey, target) <= 0).ToList();
            var done = rows.Where(x => x.Status == "done").Select(x => x.DayKey).ToHashSet(StringComparer.Ordinal);
            var unfinished = rows.Where(x => x.Status != "done").ToList();
            var history = attempts.OrderByDescending(x => x.StartedUnixMs).ToList();
            result.HistoricalFailures = unfinished.Select(x => new DailyBuildIssue
            {
                Day = x.DayKey, Status = x.Status,
                Stage = history.FirstOrDefault(a => a.DayKey == x.DayKey)?.Stage ?? "旧版本未记录阶段",
                Reason = x.Status == "running" ? "上次构建未正常结束" : FailureProtection.SafeReason(new InvalidOperationException(x.Error))
            }).ToList();
            result.RecentAttempts = history.Select(x => new DailyBuildIssue
            {
                Day = x.DayKey, Status = x.Status, Stage = x.Stage,
                Reason = x.Status == "done" ? "完成" : x.Status == "running" || x.Status == "interrupted"
                    ? "未正常结束或仍在运行" : FailureProtection.SafeReason(new InvalidOperationException(x.Error))
            }).ToList();
            result.Days = unbuilt.Concat(unfinished.Select(x => x.DayKey)).Append(target)
                .Where(x => ValidDay(x) && string.CompareOrdinal(x, target) <= 0 && !done.Contains(x))
                .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
            return result;
        }

        // 手动与自动构建共用同一个顺序执行器；先完成旧日，任一天失败立刻收口。
        public static async System.Threading.Tasks.Task<bool> RunInOrderAsync(IEnumerable<string> days,
            Func<string, System.Threading.Tasks.Task<int>> run)
        {
            foreach (var day in days.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal))
                if (await run(day) != 0) return false;
            return true;
        }
    }
}
