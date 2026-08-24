using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TraceSoul2.Data;
using TraceSoul2.Manager;
using TraceSoul2.Plugins;
using TraceSoul2.Prompts;
using TraceSoul2.Util;

namespace TraceSoul2.Logic
{
    /// <summary>心智勾标签之后，由代码做向量语义拼装。不再另开一轮总结 LLM。</summary>
    public static class MemoryRecallLogic
    {
        public const float TagMinScore = 0.12f;
        public const float TagWindowFromBest = 0.28f;

        /// <summary>
        /// 在心智作决定以前，先让与当前语境最贴近的一小片真实过去自然浮起。
        /// 这一步只做本地向量/字符检索，不增加 LLM 轮次，也不要求心智必须采用。
        /// </summary>
        public static string Preview(TraceTurnContext turn, int topK)
        {
            if (turn == null || turn.Services == null || turn.Services.Storage == null)
                return string.Empty;
            var storage = turn.Services.Storage;
            var indexes = storage.GetActiveEventIndexes() ?? new List<EventIndexRecord>();
            if (indexes.Count == 0) return string.Empty;
            var entries = storage.GetEventEntriesByIndexIds(indexes.Select(x => x.Id))
                          ?? new List<EventEntryRecord>();
            if (entries.Count == 0) return string.Empty;

            var query = BuildPreludeQuery(turn);
            if (string.IsNullOrWhiteSpace(query)) return string.Empty;
            topK = Math.Max(1, Math.Min(10, topK));
            var scores = new Dictionary<string, float>(StringComparer.Ordinal);
            var picked = PickByMeaning(turn, query, entries, topK, scores);
            if (picked.Count == 0) return string.Empty;
            var indexById = indexes
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Id))
                .GroupBy(x => x.Id, StringComparer.Ordinal)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
            var cognitions = RecallCognitions(storage, new string[0], query, Math.Min(4, topK));
            return FormatPreview(picked, indexById, cognitions);
        }

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

        private static List<EventEntryRecord> PickByMeaning(
            TraceTurnContext turn,
            string query,
            List<EventEntryRecord> entries,
            int topK,
            Dictionary<string, float> scores)
        {
            var recall = turn.Services.Recall;
            if (recall != null && recall.IsAvailable)
            {
                var byId = entries
                    .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Id))
                    .GroupBy(x => x.Id, StringComparer.Ordinal)
                    .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
                var hits = recall.Search(query, byId.Keys.Take(3000).ToList(),
                    LadderRecallLogic.PoolSize(topK));
                var result = new List<EventEntryRecord>();
                foreach (var hit in hits ?? new List<MemoryRecallHit>())
                {
                    EventEntryRecord entry;
                    if (hit == null || !byId.TryGetValue(hit.EntryId, out entry)) continue;
                    if (result.Any(x => x.Id == entry.Id)) continue;
                    result.Add(entry);
                    scores[entry.Id] = hit.Score;
                    if (result.Count >= topK) break;
                }
                if (result.Count > 0) return result;
            }

            // 记忆神经尚未就绪时仍按语义近似排序，不退化成“最近几条就是相关”。
            var encoder = new BagOfCharsVectorEncoder();
            var queryVector = encoder.Encode(query, VectorTextPurpose.Query);
            VectorMathUtil.NormalizeInPlace(queryVector);
            var ranked = entries
                .Where(x => x != null)
                .Select(x =>
                {
                    var text = (x.Summary ?? string.Empty) + "\n" + (x.Detail ?? string.Empty);
                    var vector = encoder.Encode(text, VectorTextPurpose.Index);
                    VectorMathUtil.NormalizeInPlace(vector);
                    return new KeyValuePair<EventEntryRecord, float>(x,
                        VectorMathUtil.Cosine(queryVector, vector));
                })
                .OrderByDescending(x => x.Value)
                .ThenByDescending(x => x.Key.CreatedUnixMs)
                .Take(topK)
                .ToList();
            foreach (var item in ranked) scores[item.Key.Id] = item.Value;
            return ranked.Select(x => x.Key).ToList();
        }

        private static string BuildPreludeQuery(TraceTurnContext turn)
        {
            var parts = new List<string>();
            if (turn != null && turn.Moment != null &&
                HeartbeatLogic.IsHeartbeatContent(turn.Moment.Content))
            {
                var now = DateTimeOffset.Now;
                var routing = MouthLogic.LoadState(
                    turn.Services == null ? null : turn.Services.DataDirectory);
                var scene = BodySceneValues.Label(routing.scene);
                parts.Add("心跳醒来时的生活环境：" + TimeLanguageUtil.NaturalNow(now) +
                          "，身体场景在" + scene +
                          "。寻找她在这个时段通常做什么、近期计划、可以自然联系的共同经历。");
                var plan = HeartbeatLogic.ExtractPlan(turn.Moment.Content);
                if (!string.IsNullOrWhiteSpace(plan))
                    parts.Add("这次醒来原定重新检查：" + plan);
            }
            if (turn.RawHistoryLimit > 0 && turn.RecentMoments != null &&
                turn.Services != null && turn.Services.Storage != null)
            {
                var pair = turn.Services.Storage.LoadPairIdentity();
                parts.AddRange(turn.RecentMoments
                    .Where(x => x != null &&
                                (pair.IsHumanMoment(x.Role) || pair.IsCompanionMoment(x.Role)) &&
                                !string.IsNullOrWhiteSpace(x.Content))
                    .TakeLast(Math.Min(turn.RawHistoryLimit, 6))
                    .Select(x => x.Content.Trim()));
            }
            if (turn.Moment != null && !string.IsNullOrWhiteSpace(turn.Moment.Content))
                parts.Add(turn.Moment.Content.Trim());
            return string.Join("\n", parts.Where(x => !string.IsNullOrWhiteSpace(x)));
        }

        private static string FormatPreview(
            List<EventEntryRecord> entries,
            Dictionary<string, EventIndexRecord> indexById,
            List<CognitionSliceRecord> cognitions)
        {
            var builder = new StringBuilder();
            builder.AppendLine(CorePrompts.MemoryRecall.PreviewHeader);
            builder.AppendLine(CorePrompts.MemoryRecall.PreviewHint);
            foreach (var entry in entries ?? new List<EventEntryRecord>())
            {
                EventIndexRecord index;
                indexById.TryGetValue(entry.IndexId ?? string.Empty, out index);
                var heading = index == null
                    ? string.Empty
                    : FormatDate(index.TimeUnixMs) +
                      (string.IsNullOrWhiteSpace(index.MoodLabel) ? string.Empty : " · " + index.MoodLabel);
                builder.Append("- ");
                if (heading.Length > 0) builder.Append("[").Append(heading).Append("] ");
                if (!string.IsNullOrWhiteSpace(entry.Summary))
                    builder.Append(entry.Summary.Trim()).Append("｜");
                builder.AppendLine((entry.Detail ?? string.Empty).Trim());
            }
            if (cognitions != null && cognitions.Count > 0)
            {
                builder.AppendLine(CorePrompts.MemoryRecall.PreviewCognitionHeader);
                foreach (var cognition in cognitions)
                    if (cognition != null && !string.IsNullOrWhiteSpace(cognition.Summary))
                        builder.AppendLine("- " + cognition.Summary.Trim());
            }
            return builder.ToString().TrimEnd();
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
            builder.AppendLine(CorePrompts.MemoryRecall.LitHeader);
            if (entries == null || entries.Count == 0)
                builder.AppendLine(CorePrompts.MemoryRecall.EmptyRange);
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
                builder.AppendLine(CorePrompts.MemoryRecall.CognitionHeader);
                foreach (var c in cognitions)
                    builder.AppendLine("- " + c.Summary);
            }
            builder.AppendLine(CorePrompts.MemoryRecall.UseOnlyFacts);
            builder.AppendLine(CorePrompts.MemoryRecall.NotTheTask);
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
