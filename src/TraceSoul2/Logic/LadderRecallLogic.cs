using System;
using System.Collections.Generic;
using System.Linq;
using TraceSoul2.Data;
using TraceSoul2.Manager;
using TraceSoul2.Plugins;

namespace TraceSoul2.Logic
{
    /// <summary>
    /// 时间阶梯不进 Prompt。召回时它只降低入选门槛：分数仍是原余弦/置信度，
    /// 上榜条目只要离截断线不太远，可以多进 1-2 条，不能靠加分压过更像的记忆。
    /// </summary>
    public static class LadderRecallLogic
    {
        public const float Ease = 0.15f;
        public const int ExtraCap = 2;

        public static HashSet<string> EventIndexIds(IMemoryStore storage)
        {
            return RefIds(storage, "event_index", "event");
        }

        public static HashSet<string> CognitionIds(IMemoryStore storage)
        {
            return RefIds(storage, "cognition", "cognition");
        }

        public static int PoolSize(int topK)
        {
            topK = Math.Max(1, topK);
            return Math.Max(topK * 4, 12);
        }

        public static List<MemoryRecallHit> AdmitEvents(
            IEnumerable<MemoryRecallHit> ranked,
            IDictionary<string, EventEntryRecord> entries,
            ISet<string> easedIndexIds,
            int topK)
        {
            var ordered = (ranked ?? Enumerable.Empty<MemoryRecallHit>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.EntryId))
                .OrderByDescending(x => x.Score)
                .ToList();
            if (ordered.Count == 0) return ordered;
            topK = Math.Max(1, topK);
            var head = ordered.Take(topK).ToList();
            if (easedIndexIds == null || easedIndexIds.Count == 0 || entries == null)
                return head;
            var cutoff = head[head.Count - 1].Score;
            var floor = cutoff <= 0 ? 0f : cutoff * (1f - Ease);
            var taken = new HashSet<string>(head.Select(x => x.EntryId), StringComparer.Ordinal);
            var extras = 0;
            foreach (var hit in ordered.Skip(topK))
            {
                if (extras >= ExtraCap) break;
                if (hit.Score + 0.0001f < floor) break;
                EventEntryRecord entry;
                if (!entries.TryGetValue(hit.EntryId, out entry) || entry == null) continue;
                if (!easedIndexIds.Contains(entry.IndexId ?? string.Empty)) continue;
                if (!taken.Add(hit.EntryId)) continue;
                head.Add(hit);
                extras += 1;
            }
            return head;
        }

        public static List<CognitionSliceRecord> AdmitCognitions(
            IEnumerable<CognitionSliceRecord> ranked,
            ISet<string> easedIds,
            int topK)
        {
            var ordered = (ranked ?? Enumerable.Empty<CognitionSliceRecord>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Id))
                .OrderByDescending(x => x.Confidence)
                .ThenByDescending(x => x.UpdatedUnixMs)
                .ToList();
            if (ordered.Count == 0) return ordered;
            topK = Math.Max(1, Math.Min(5, topK));
            var head = ordered.Take(topK).ToList();
            if (easedIds == null || easedIds.Count == 0) return head;
            var cutoff = head[head.Count - 1].Confidence;
            var floor = cutoff <= 0 ? 0f : cutoff * (1f - Ease);
            var taken = new HashSet<string>(head.Select(x => x.Id), StringComparer.Ordinal);
            var extras = 0;
            foreach (var item in ordered.Skip(topK))
            {
                if (extras >= ExtraCap) break;
                if (item.Confidence + 0.0001f < floor) break;
                if (!easedIds.Contains(item.Id)) continue;
                if (!taken.Add(item.Id)) continue;
                head.Add(item);
                extras += 1;
            }
            return head;
        }

        private static HashSet<string> RefIds(IMemoryStore storage, string refKind, string listKind)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (storage == null) return ids;
            foreach (var item in storage.GetAllLadderItems() ?? new List<LadderItemRecord>())
            {
                if (item == null || string.IsNullOrWhiteSpace(item.RefId)) continue;
                var kind = item.RefKind ?? string.Empty;
                var list = item.ListKind ?? string.Empty;
                if (string.Equals(kind, refKind, StringComparison.Ordinal) ||
                    string.Equals(list, listKind, StringComparison.Ordinal))
                    ids.Add(item.RefId);
            }
            return ids;
        }
    }
}
