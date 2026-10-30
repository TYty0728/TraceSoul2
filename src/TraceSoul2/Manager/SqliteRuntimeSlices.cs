using System.Runtime.CompilerServices;
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
        [MethodImpl(MethodImplOptions.Synchronized)]
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

        [MethodImpl(MethodImplOptions.Synchronized)]
        public List<RuntimeSliceRecord> GetRuntimeSlices(string root, string day, bool pendingOnly = false) =>
            connection.Table<RuntimeSliceRecord>().Where(x => x.RootConversationId == root && x.DayKey == day)
                .OrderBy(x => x.CreatedUnixMs).ThenBy(x => x.Id).ToList()
                .Where(x => !pendingOnly || string.IsNullOrEmpty(x.ReviewId)).ToList();

        [MethodImpl(MethodImplOptions.Synchronized)]
        public List<RuntimeSliceRecord> GetRuntimeSlicesForMoments(IEnumerable<string> ids) =>
            ids.Distinct().Select(x => connection.Find<RuntimeSliceRecord>("runtime:" + x)).Where(x => x != null).ToList();

        [MethodImpl(MethodImplOptions.Synchronized)]
        public List<RuntimeDayReviewRecord> GetRuntimeDayReviews(string root, string day) =>
            connection.Table<RuntimeDayReviewRecord>().Where(x => x.RootConversationId == root && x.DayKey == day)
                .OrderBy(x => x.FirstUnixMs).ThenBy(x => x.Id).ToList();

        [MethodImpl(MethodImplOptions.Synchronized)]
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

        [MethodImpl(MethodImplOptions.Synchronized)]
        public List<RuntimeDayReviewRecord> GetRuntimeDaySummaries(string root, string day) =>
            connection.Table<RuntimeDaySummaryRow>().Where(x => x.RootConversationId == root && x.DayKey == day && x.IsCurrent)
                .OrderBy(x => x.FirstUnixMs).ToList().Select(ToReview).ToList();

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

    }
}
