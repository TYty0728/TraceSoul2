using System;
using System.Collections.Generic;
using System.Linq;
using TraceSoul2.Data;
using TraceSoul2.Logic;
using TraceSoul2.Util;
using SQLite;

namespace TraceSoul2.Manager
{
    public sealed partial class SqliteMemoryManager
    {
        public void AppendRuntimeSlice(RuntimeSliceRecord slice)
        {
            if (slice == null || string.IsNullOrWhiteSpace(slice.MomentId) || string.IsNullOrWhiteSpace(slice.SnapshotJson))
                throw new InvalidOperationException("当下切片缺少来源或内容。");
            var moment = connection.Find<MomentRecord>(slice.MomentId);
            var operational = moment == null ? connection.Find<OperationalEventRecord>(slice.MomentId) : null;
            if ((moment?.ConversationId ?? operational?.ConversationId) != slice.ContextConversationId ||
                (slice.ContextConversationId != slice.RootConversationId &&
                 !slice.ContextConversationId.StartsWith(slice.RootConversationId + ":environment:", StringComparison.Ordinal)))
                throw new InvalidOperationException("当下切片必须归属真实来源环境。");
            slice.Id = "runtime:" + slice.MomentId;
            slice.CreatedUnixMs = moment?.CreatedUnixMs ?? operational.OccurredUnixMs;
            slice.DayKey = MemoryDayLogic.CurrentDayKey(DateTimeOffset.FromUnixTimeMilliseconds(slice.CreatedUnixMs));
            slice.MemoryVisibility = moment?.MemoryVisibility ??
                PuzzleViewLogic.Read<EnvironmentSnapshotData>(operational?.EnvironmentJson)?.Visibility ?? "private";
            slice.ReviewId = null;
            connection.RunInTransaction(() =>
            {
                if (connection.Insert(slice, "OR IGNORE") > 0)
                    connection.Execute("UPDATE runtime_day_summaries SET IsCurrent=0 WHERE RootConversationId=? AND ContextConversationId=? AND DayKey=? AND MemoryVisibility=?",
                        slice.RootConversationId, slice.ContextConversationId, slice.DayKey, slice.MemoryVisibility);
            }); // 同轮重放保留最初切片；新来源使旧成稿失效。
        }

        public List<RuntimeSliceRecord> GetRuntimeSlices(string root, string day, bool pendingOnly = false) =>
            connection.Table<RuntimeSliceRecord>().Where(x => x.RootConversationId == root && x.DayKey == day)
                .OrderBy(x => x.CreatedUnixMs).ThenBy(x => x.Id).ToList()
                .Where(x => !pendingOnly || string.IsNullOrEmpty(x.ReviewId)).ToList();

        public List<RuntimeSliceRecord> GetRuntimeSlicesForMoments(IEnumerable<string> ids) =>
            ids.Distinct().Select(x => connection.Find<RuntimeSliceRecord>("runtime:" + x)).Where(x => x != null).ToList();

        public List<string> GetUnreviewedRuntimeDayKeysBefore(long endMs) => connection.QueryScalars<string>(
            "SELECT DISTINCT DayKey FROM runtime_slices WHERE CreatedUnixMs<? AND (ReviewId IS NULL OR ReviewId='') " +
            "UNION SELECT DayKey FROM runtime_day_summaries WHERE FirstUnixMs<? AND IsCurrent=0 ORDER BY DayKey", endMs, endMs);

        public List<RuntimeDayReviewRecord> GetRuntimeDayReviews(string root, string day) =>
            connection.Table<RuntimeDayReviewRecord>().Where(x => x.RootConversationId == root && x.DayKey == day)
                .OrderBy(x => x.FirstUnixMs).ThenBy(x => x.Id).ToList();

        public List<RuntimeDayReviewRecord> GetRuntimeReviewCandidates(string root, string publicContext = null)
        {
            var summaryQuery = connection.Table<RuntimeDaySummaryRow>().Where(x => x.RootConversationId == root);
            var reviewQuery = connection.Table<RuntimeDayReviewRecord>().Where(x => x.RootConversationId == root);
            if (publicContext != null)
            {
                summaryQuery = summaryQuery.Where(x => x.ContextConversationId == publicContext && x.MemoryVisibility == "public");
                reviewQuery = reviewQuery.Where(x => x.ContextConversationId == publicContext && x.MemoryVisibility == "public");
            }
            var summaries = summaryQuery.ToList();
            var managed = summaries.Select(x => (x.DayKey, x.ContextConversationId, x.MemoryVisibility)).ToHashSet();
            // 旧版历史仍可查证和召回；一个环境开始成稿流程后，只提供其有效成稿。
            var legacy = reviewQuery.OrderByDescending(x => x.FirstUnixMs).Take(1000).ToList()
                .Where(x => !managed.Contains((x.DayKey, x.ContextConversationId, x.MemoryVisibility)));
            return summaries.Where(x => x.IsCurrent).Select(ToReview).Concat(legacy)
                .Where(x => publicContext == null || (x.ContextConversationId == publicContext && x.MemoryVisibility == "public"))
                .OrderByDescending(x => x.FirstUnixMs).Take(1000).ToList();
        }

        public List<RuntimeDayReviewRecord> GetRuntimeDaySummaries(string root, string day) =>
            connection.Table<RuntimeDaySummaryRow>().Where(x => x.RootConversationId == root && x.DayKey == day && x.IsCurrent)
                .OrderBy(x => x.FirstUnixMs).ToList().Select(ToReview).ToList();

        public bool HasPendingRuntimeDaySummary(string root, string day) =>
            connection.Table<RuntimeDaySummaryRow>().Any(x => x.RootConversationId == root && x.DayKey == day && !x.IsCurrent);

        public void BeginRuntimeDaySummary(RuntimeDayReviewRecord review)
        {
            if (connection.Table<RuntimeDaySummaryRow>().Any(x => x.RootConversationId == review.RootConversationId &&
                x.ContextConversationId == review.ContextConversationId && x.DayKey == review.DayKey && x.MemoryVisibility == review.MemoryVisibility)) return;
            connection.Insert(new RuntimeDaySummaryRow { Id = Guid.NewGuid().ToString("N"), RootConversationId = review.RootConversationId,
                ContextConversationId = review.ContextConversationId, DayKey = review.DayKey, MemoryVisibility = review.MemoryVisibility,
                FirstUnixMs = review.FirstUnixMs });
        }

        public void CommitRuntimeDaySummary(IEnumerable<string> reviewIds, string summary)
        {
            if (string.IsNullOrWhiteSpace(summary) || summary.Length > RuntimeSliceLogic.DaySummaryLimit)
                throw new InvalidOperationException("整天回望须为1～1200字。");
            connection.RunInTransaction(() =>
            {
                var ids = reviewIds.Distinct().ToList();
                var reviews = ids.Select(id => connection.Find<RuntimeDayReviewRecord>(id)).ToList();
                if (reviews.Count == 0 || reviews.Any(x => x == null) ||
                    reviews.Select(x => (x.RootConversationId, x.ContextConversationId, x.DayKey, x.MemoryVisibility)).Distinct().Count() != 1)
                    throw new InvalidOperationException("整天回望的来源须属于同一天、同一环境。");
                var first = reviews[0];
                var current = GetRuntimeDayReviews(first.RootConversationId, first.DayKey)
                    .Where(x => x.ContextConversationId == first.ContextConversationId && x.MemoryVisibility == first.MemoryVisibility).ToList();
                if (!current.Select(x => x.Id).ToHashSet().SetEquals(ids) || GetRuntimeSlices(first.RootConversationId, first.DayKey, true)
                    .Any(x => x.ContextConversationId == first.ContextConversationId && x.MemoryVisibility == first.MemoryVisibility))
                    throw new InvalidOperationException("整天回望期间出现新材料，保留分段整理等待再次汇总。");
                BeginRuntimeDaySummary(first);
                var row = connection.Table<RuntimeDaySummaryRow>().First(x => x.RootConversationId == first.RootConversationId &&
                    x.ContextConversationId == first.ContextConversationId && x.DayKey == first.DayKey && x.MemoryVisibility == first.MemoryVisibility);
                row.Summary = summary.Trim(); row.SourceReviewIdsJson = TraceJson.ToJson(ids);
                row.SliceIdsJson = TraceJson.ToJson(reviews.SelectMany(x => TraceJson.FromJson<List<string>>(x.SliceIdsJson)).Distinct().ToList());
                row.MomentIdsJson = TraceJson.ToJson(reviews.SelectMany(x => TraceJson.FromJson<List<string>>(x.MomentIdsJson)).Distinct().ToList());
                row.FirstUnixMs = reviews.Min(x => x.FirstUnixMs); row.CreatedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                row.IsCurrent = true; connection.Update(row);
            });
        }

        [Table("runtime_day_summaries")]
        private sealed class RuntimeDaySummaryRow
        {
            public RuntimeDaySummaryRow() { }
            [PrimaryKey] public string Id { get; set; }
            public string RootConversationId { get; set; }
            public string ContextConversationId { get; set; }
            public string DayKey { get; set; }
            public string MemoryVisibility { get; set; }
            public string Summary { get; set; }
            public string SourceReviewIdsJson { get; set; }
            public string SliceIdsJson { get; set; }
            public string MomentIdsJson { get; set; }
            public long FirstUnixMs { get; set; }
            public long CreatedUnixMs { get; set; }
            public bool IsCurrent { get; set; }
        }

        private static RuntimeDayReviewRecord ToReview(RuntimeDaySummaryRow row) => new RuntimeDayReviewRecord {
            Id = row.Id, RootConversationId = row.RootConversationId, ContextConversationId = row.ContextConversationId,
            DayKey = row.DayKey, MemoryVisibility = row.MemoryVisibility, Summary = row.Summary,
            SliceIdsJson = row.SliceIdsJson, MomentIdsJson = row.MomentIdsJson, FirstUnixMs = row.FirstUnixMs, CreatedUnixMs = row.CreatedUnixMs };

        public RuntimeDayReviewRecord CommitRuntimeSliceReview(IEnumerable<string> sourceIds, string summary)
        {
            if (string.IsNullOrWhiteSpace(summary) || summary.Length > RuntimeSliceLogic.DaySummaryLimit)
                throw new InvalidOperationException("切片整理须为1～1200字。");
            RuntimeDayReviewRecord result = null;
            connection.RunInTransaction(() =>
            {
                var ids = sourceIds.Distinct().ToList();
                var slices = ids.Select(id => connection.Find<RuntimeSliceRecord>(id)).ToList();
                if (slices.Count == 0 || slices.Any(x => x == null) ||
                    slices.Select(x => (x.RootConversationId, x.ContextConversationId, x.DayKey, x.MemoryVisibility)).Distinct().Count() != 1)
                    throw new InvalidOperationException("切片复盘不能混合日期或环境。");
                if (slices.Any(x => !string.IsNullOrEmpty(x.ReviewId)))
                    throw new InvalidOperationException("切片已被复盘，请重新读取待处理批次。");
                var first = slices[0];
                result = new RuntimeDayReviewRecord { Id = Guid.NewGuid().ToString("N"), RootConversationId = first.RootConversationId,
                    ContextConversationId = first.ContextConversationId, DayKey = first.DayKey, MemoryVisibility = first.MemoryVisibility,
                    Summary = summary.Trim(), SliceIdsJson = TraceJson.ToJson(ids),
                    MomentIdsJson = TraceJson.ToJson(slices.Select(x => x.MomentId).Distinct().ToList()),
                    FirstUnixMs = slices.Min(x => x.CreatedUnixMs), CreatedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
                connection.Insert(result);
                foreach (var slice in slices) { slice.ReviewId = result.Id; connection.Update(slice); }
                BeginRuntimeDaySummary(result);
                connection.Execute("UPDATE runtime_day_summaries SET IsCurrent=0 WHERE RootConversationId=? AND ContextConversationId=? AND DayKey=? AND MemoryVisibility=?",
                    result.RootConversationId, result.ContextConversationId, result.DayKey, result.MemoryVisibility);
            });
            return result;
        }

        public void ReleaseRuntimeDayReviews(IEnumerable<string> reviewIds)
        {
            connection.RunInTransaction(() =>
            {
                foreach (var id in reviewIds.Distinct().Where(x => !string.IsNullOrWhiteSpace(x)))
                {
                    var review = connection.Find<RuntimeDayReviewRecord>(id);
                    if (review == null) continue;
                    foreach (var slice in connection.Table<RuntimeSliceRecord>().Where(x => x.ReviewId == id).ToList())
                    {
                        slice.ReviewId = null;
                        connection.Update(slice);
                    }
                    connection.Delete(review);
                }
            });
        }
    }
}
