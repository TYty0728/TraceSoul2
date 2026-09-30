using System;
using System.Collections.Generic;
using System.Linq;
using TraceSoul2.Data;
using TraceSoul2.Logic;
using TraceSoul2.Util;

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
            connection.Insert(slice, "OR IGNORE"); // 同轮重放保留最初切片，不覆盖已复盘依据。
        }

        public List<RuntimeSliceRecord> GetRuntimeSlices(string root, string day, bool pendingOnly = false) =>
            connection.Table<RuntimeSliceRecord>().Where(x => x.RootConversationId == root && x.DayKey == day)
                .OrderBy(x => x.CreatedUnixMs).ThenBy(x => x.Id).ToList()
                .Where(x => !pendingOnly || string.IsNullOrEmpty(x.ReviewId)).ToList();

        public List<RuntimeSliceRecord> GetRuntimeSlicesForMoments(IEnumerable<string> ids) =>
            ids.Distinct().Select(x => connection.Find<RuntimeSliceRecord>("runtime:" + x)).Where(x => x != null).ToList();

        public List<string> GetUnreviewedRuntimeDayKeysBefore(long endMs) => connection.QueryScalars<string>(
            "SELECT DISTINCT DayKey FROM runtime_slices WHERE CreatedUnixMs<? AND (ReviewId IS NULL OR ReviewId='') ORDER BY DayKey", endMs);

        public List<RuntimeDayReviewRecord> GetRuntimeDayReviews(string root, string day) =>
            connection.Table<RuntimeDayReviewRecord>().Where(x => x.RootConversationId == root && x.DayKey == day)
                .OrderBy(x => x.FirstUnixMs).ThenBy(x => x.Id).ToList();

        public List<RuntimeDayReviewRecord> GetRuntimeReviewCandidates(string root, string publicContext = null) =>
            publicContext == null
                ? connection.Table<RuntimeDayReviewRecord>().Where(x => x.RootConversationId == root)
                    .OrderByDescending(x => x.FirstUnixMs).Take(1000).ToList()
                : connection.Table<RuntimeDayReviewRecord>().Where(x => x.RootConversationId == root &&
                    x.ContextConversationId == publicContext && x.MemoryVisibility == "public")
                    .OrderByDescending(x => x.FirstUnixMs).Take(1000).ToList();

        public RuntimeDayReviewRecord CommitRuntimeSliceReview(IEnumerable<string> sourceIds, string summary)
        {
            if (string.IsNullOrWhiteSpace(summary) || summary.Length > 3000)
                throw new InvalidOperationException("切片复盘须为 1～3000 字。");
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
            });
            return result;
        }
    }
}
