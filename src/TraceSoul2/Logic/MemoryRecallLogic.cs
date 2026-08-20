using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TraceSoul2.Data;
using TraceSoul2.Manager;
using TraceSoul2.Plugins;

namespace TraceSoul2.Logic
{
    /// <summary>心智勾标签之后，由代码做向量语义拼装。不再另开一轮总结 LLM。</summary>
    public static class MemoryRecallLogic
    {
        public const float TagMinScore = 0.12f;
        public const float TagWindowFromBest = 0.28f;

        public static List<LifeTagRecord> ListTagCandidates(TraceTurnContext turn, int cap)
        {
            cap = Math.Max(1, cap);
            var storage = turn == null || turn.Services == null ? null : turn.Services.Storage;
            var source = storage == null ? new List<LifeTagRecord>() : storage.GetActiveLifeTags() ?? new List<LifeTagRecord>();
            var query = turn == null || turn.Moment == null ? string.Empty : turn.Moment.Content;
            var ranked = RankByMoment(turn == null || turn.Services == null ? null : turn.Services.Router, query, source, cap);
            if (ranked.Count > 0) return ranked;
            return source.OrderByDescending(x => x.ActivationCount)
                .ThenBy(x => x.Label, StringComparer.Ordinal)
                .Take(cap)
                .ToList();
        }

        /// <summary>按这一句 Moment 的向量远近排人生 Tag，再截一段给心智。激活次数不参与加分。</summary>
        public static List<LifeTagRecord> RankByMoment(
            IHierarchicalVectorRouter router,
            string query,
            List<LifeTagRecord> source,
            int cap)
        {
            if (router == null || string.IsNullOrWhiteSpace(query) || source == null || source.Count == 0)
                return new List<LifeTagRecord>();
            IReadOnlyList<VectorRouteHit> hits;
            try
            {
                hits = router.RankConcepts(query, new VectorRouteSettings { ActivationCountBonus = 0f });
            }
            catch
            {
                return new List<LifeTagRecord>();
            }
            if (hits == null || hits.Count == 0) return new List<LifeTagRecord>();

            var byId = source
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Id))
                .GroupBy(x => x.Id, StringComparer.Ordinal)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
            var ordered = new List<KeyValuePair<LifeTagRecord, float>>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var hit in hits)
            {
                if (hit == null || hit.Node == null) continue;
                LifeTagRecord tag;
                if (!byId.TryGetValue(hit.Node.Id, out tag) || tag == null) continue;
                if (!seen.Add(tag.Id)) continue;
                ordered.Add(new KeyValuePair<LifeTagRecord, float>(tag, hit.Score));
            }
            if (ordered.Count == 0) return new List<LifeTagRecord>();
            var best = ordered[0].Value;
            var floor = Math.Max(TagMinScore, best - TagWindowFromBest);
            return ordered
                .Where(x => x.Value + 0.0001f >= floor)
                .Take(cap)
                .Select(x => x.Key)
                .ToList();
        }

        public static string Assemble(
            TraceTurnContext turn,
            MindDecisionData mind,
            int topK)
        {
            if (turn == null || turn.Services == null || turn.Services.Storage == null)
                return string.Empty;
            var storage = turn.Services.Storage;
            var query = mind == null || string.IsNullOrWhiteSpace(mind.query)
                ? turn.Moment.Content
                : mind.query.Trim();
            if (string.IsNullOrWhiteSpace(query)) query = turn.Moment.Content;

            var labels = mind == null ? new List<string>() : mind.ParseTags();
            var idByLabel = (storage.GetActiveLifeTags() ?? new List<LifeTagRecord>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Label))
                .GroupBy(x => x.Label, StringComparer.Ordinal)
                .ToDictionary(x => x.Key, x => x.First().Id, StringComparer.Ordinal);
            var conceptIds = labels
                .Where(x => idByLabel.ContainsKey(x))
                .Select(x => idByLabel[x])
                .Distinct()
                .ToList();

            var indexes = storage.GetActiveEventIndexes() ?? new List<EventIndexRecord>();
            if (indexes.Count == 0)
                return "人生记忆还是空的，没有共同经历切片。";

            var filtered = storage.GetEventIndexesByFilter(
                conceptIds, null, null, null, null, null, null, 500);
            if (filtered == null || filtered.Count == 0)
                filtered = indexes.Take(500).ToList();

            var entries = storage.GetEventEntriesByIndexIds(filtered.Select(x => x.Id))
                ?? new List<EventEntryRecord>();
            if (entries.Count == 0)
                return "这些标签下还没有可拼装的细节。";

            topK = Math.Max(1, Math.Min(10, topK));
            var recall = turn.Services.Recall;
            var easedIndexes = LadderRecallLogic.EventIndexIds(storage);
            List<EventEntryRecord> picked;
            var scores = new Dictionary<string, float>(StringComparer.Ordinal);
            if (recall != null && recall.IsAvailable)
            {
                var byId = entries.ToDictionary(x => x.Id, StringComparer.Ordinal);
                var hits = recall.Search(query, entries.Select(x => x.Id).Take(3000).ToList(),
                    LadderRecallLogic.PoolSize(topK));
                hits = LadderRecallLogic.AdmitEvents(hits, byId, easedIndexes, topK);
                picked = new List<EventEntryRecord>();
                foreach (var hit in hits ?? new List<MemoryRecallHit>())
                {
                    EventEntryRecord entry;
                    if (hit == null || !byId.TryGetValue(hit.EntryId, out entry)) continue;
                    picked.Add(entry);
                    scores[entry.Id] = hit.Score;
                }
            }
            else
            {
                picked = entries.OrderByDescending(x => x.CreatedUnixMs).Take(topK).ToList();
                var taken = new HashSet<string>(picked.Select(x => x.Id), StringComparer.Ordinal);
                foreach (var entry in entries.OrderByDescending(x => x.CreatedUnixMs))
                {
                    if (picked.Count >= topK + LadderRecallLogic.ExtraCap) break;
                    if (!taken.Add(entry.Id)) continue;
                    if (!easedIndexes.Contains(entry.IndexId ?? string.Empty)) continue;
                    picked.Add(entry);
                }
            }

            var indexById = filtered.ToDictionary(x => x.Id, StringComparer.Ordinal);
            var cognitions = RecallCognitions(turn.Services.Storage, conceptIds, query, topK);
            return Format(picked, indexById, scores, cognitions);
        }

        private static List<CognitionSliceRecord> RecallCognitions(
            IMemoryStore storage, IEnumerable<string> conceptIds, string searchText, int topK)
        {
            var map = new Dictionary<string, CognitionSliceRecord>(StringComparer.Ordinal);
            foreach (var c in storage.GetCognitionCandidates(conceptIds, Math.Max(8, topK * 2)) ??
                               new List<CognitionSliceRecord>())
                if (c != null && c.Status == "active") map[c.Id] = c;
            foreach (var cue in storage.FindCognitionsByCue(searchText, 6) ??
                                  new List<CognitionCueRecallData>())
                if (cue != null && cue.Cognition != null && cue.Cognition.Status == "active")
                    map[cue.Cognition.Id] = cue.Cognition;
            return LadderRecallLogic.AdmitCognitions(map.Values, LadderRecallLogic.CognitionIds(storage), topK);
        }

        private static string Format(
            List<EventEntryRecord> entries,
            Dictionary<string, EventIndexRecord> indexById,
            Dictionary<string, float> scores,
            List<CognitionSliceRecord> cognitions)
        {
            var builder = new StringBuilder();
            builder.AppendLine("此刻点亮的共同记忆：");
            if (entries == null || entries.Count == 0)
                builder.AppendLine("（范围内没有足够相近的细节。）");
            else
            {
                foreach (var entry in entries)
                {
                    EventIndexRecord index;
                    indexById.TryGetValue(entry.IndexId, out index);
                    var score = scores != null && scores.ContainsKey(entry.Id)
                        ? "（相似度 " + scores[entry.Id].ToString("0.00") + "）"
                        : string.Empty;
                    builder.AppendLine("◆ " + (index == null ? "索引未知" :
                        FormatDate(index.TimeUnixMs) + " · " +
                        (string.IsNullOrWhiteSpace(index.TimeLabel) ? "时段未知" : index.TimeLabel) +
                        (string.IsNullOrWhiteSpace(index.PersonLabel) ? string.Empty : " · " + index.PersonLabel) +
                        (string.IsNullOrWhiteSpace(index.MoodLabel) ? string.Empty : " · 心情：" + index.MoodLabel)) +
                        score);
                    if (index != null) builder.AppendLine("  事件：" + Limit(index.EventSummary, 80));
                    builder.AppendLine("  - " + Limit(entry.Summary, 60) + "｜" + Limit(entry.Detail, 200));
                }
            }
            if (cognitions != null && cognitions.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine("相关认知：");
                foreach (var c in cognitions)
                    builder.AppendLine("- " + c.Summary);
            }
            builder.AppendLine("共同经历只用这里出现的事实，缺的时间、动作、物品不要补造。");
            builder.AppendLine("若这一拍要当场完成一件事，那件事不是这段材料。");
            return builder.ToString().TrimEnd();
        }

        private static string FormatDate(long unixMs)
        {
            if (unixMs <= 0) return "时间未知";
            try
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(unixMs)
                    .ToOffset(TimeSpan.FromHours(8))
                    .ToString("yyyy年MM月dd日");
            }
            catch
            {
                return "时间未知";
            }
        }

        private static string Limit(string value, int max)
        {
            value = value ?? string.Empty;
            return value.Length <= max ? value : value.Substring(0, max);
        }
    }
}
