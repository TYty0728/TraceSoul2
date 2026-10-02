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
            if (EnvironmentLogic.IsPublic(turn))
                return RecallPuzzle(turn, turn.Moment?.Content, topK, includeOriginalEvidence: false);
            var storage = turn.Services.Storage;
            var indexes = storage.GetActiveEventIndexes() ?? new List<EventIndexRecord>();
            var query = BuildPreludeQuery(turn);
            var cognitionText = RecallPuzzle(turn, query, topK, includeOriginalEvidence: false);
            if (indexes.Count == 0) return cognitionText;
            var entries = storage.GetEventEntriesByIndexIds(indexes.Select(x => x.Id))
                          ?? new List<EventEntryRecord>();
            if (entries.Count == 0) return cognitionText;

            if (string.IsNullOrWhiteSpace(query)) return string.Empty;
            topK = Math.Max(1, Math.Min(10, topK));
            var scores = new Dictionary<string, float>(StringComparer.Ordinal);
            var picked = PickByMeaning(turn, query, entries, topK, scores);
            if (picked.Count == 0) return cognitionText;
            var indexById = indexes
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Id))
                .GroupBy(x => x.Id, StringComparer.Ordinal)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
            return (FormatEvents(picked, indexById) + "\n" + cognitionText).Trim();
        }

        private static string RecallPublic(TraceTurnContext turn, string query, int topK)
        {
            var items = turn.Services.Storage.GetRecentDialogueMoments(turn.ConversationId, 200)
                .Where(x => x.Id != turn.Moment?.Id && x.ConversationId == turn.ConversationId && !string.IsNullOrWhiteSpace(x.Content))
                .OrderByDescending(x => (query ?? "").Any(c => !char.IsWhiteSpace(c) && x.Content.Contains(c)))
                .ThenByDescending(x => x.CreatedUnixMs).Take(Math.Clamp(topK, 1, 10)).ToList();
            return items.Count == 0 ? "" : "【本环境的真实经历】\n" + string.Join("\n", items.Select(x =>
                "- " + EnvironmentLogic.PublicDialogue(x)));
        }

        public static List<LifeTagRecord> ListTagCandidates(TraceTurnContext turn, int cap)
        {
            if (EnvironmentLogic.IsPublic(turn)) return new List<LifeTagRecord>();
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
            return Assemble(turn, mind, topK, out _);
        }

        public static string Assemble(TraceTurnContext turn, MindDecisionData mind, int topK, out bool hasEvidence)
        {
            hasEvidence = false;
            if (turn == null || turn.Services == null || turn.Services.Storage == null)
                return string.Empty;
            if (EnvironmentLogic.IsPublic(turn))
            {
                var recalled = RecallPublic(turn, mind?.query ?? turn.Moment?.Content, topK) + "\n" +
                    RecallPuzzle(turn, mind?.query ?? turn.Moment?.Content, topK);
                hasEvidence = !string.IsNullOrWhiteSpace(recalled);
                return recalled.Trim();
            }
            var storage = turn.Services.Storage;
            var query = mind == null || string.IsNullOrWhiteSpace(mind.query)
                ? turn.Moment.Content
                : mind.query.Trim();
            if (string.IsNullOrWhiteSpace(query)) query = turn.Moment.Content;

            var cognitionText = RecallPuzzle(turn, query, topK);
            hasEvidence = cognitionText.Length > 0;
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
                return hasEvidence ? cognitionText : "人生记忆还是空的，没有共同经历切片。";

            var filtered = storage.GetEventIndexesByFilter(
                conceptIds, null, null, null, null, null, null, 500);
            if (filtered == null || filtered.Count == 0)
                filtered = indexes.Take(500).ToList();

            var entries = storage.GetEventEntriesByIndexIds(filtered.Select(x => x.Id))
                ?? new List<EventEntryRecord>();
            if (entries.Count == 0)
                return hasEvidence ? cognitionText : "这些标签下还没有可拼装的细节。";

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
            hasEvidence = picked.Count > 0 || cognitionText.Length > 0;
            return (FormatEvents(picked, indexById) + "\n" + cognitionText).Trim();
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
            var runtime = SubjectRuntimeLogic.View(turn);
            var hold = InnerLifeLogic.FormatHold(runtime);
            if (hold.Length > 0) parts.Add("此刻仍关注：" + hold);
            var life = turn.Services.LifeState?.Load(turn.ConversationId);
            var activity = LifeStateLogic.FormatDoing(life);
            if (activity.Length > 0) parts.Add("正在做：" + activity);
            return string.Join("\n", parts.Where(x => !string.IsNullOrWhiteSpace(x)));
        }

        private static string FormatEvents(
            List<EventEntryRecord> entries,
            Dictionary<string, EventIndexRecord> indexById)
        {
            if (entries == null || entries.Count == 0) return string.Empty;
            var builder = new StringBuilder();
            builder.AppendLine(CorePrompts.MemoryRecall.PreviewHeader);
            builder.AppendLine(CorePrompts.MemoryRecall.PreviewHint);
            // 复盘已经保存总述与第一人称细节；按事件归拢选中条目，原样呈现整理正文。
            foreach (var group in entries.Where(x => x != null).GroupBy(x => x.IndexId ?? string.Empty))
            {
                indexById.TryGetValue(group.Key, out var index);
                builder.Append("\n【事件起点 · ").Append(FormatDate(index?.TimeUnixMs ?? 0));
                if (!string.IsNullOrWhiteSpace(index?.TimeLabel)) builder.Append(" · ").Append(index.TimeLabel);
                builder.AppendLine("】");
                if (!string.IsNullOrWhiteSpace(index?.EventSummary)) builder.AppendLine("事件：" + index.EventSummary.Trim());
                var context = new[] { index?.PlaceLabel, index?.PersonLabel }
                    .Where(x => !string.IsNullOrWhiteSpace(x));
                if (context.Any()) builder.AppendLine("情境：" + string.Join(" · ", context));
                if (!string.IsNullOrWhiteSpace(index?.MoodLabel)) builder.AppendLine("当时的感受：" + index.MoodLabel);
                foreach (var entry in group.GroupBy(x => x.Id).Select(x => x.First()))
                {
                    builder.AppendLine("- " + RealmLabel(entry.Realm));
                    if (!string.IsNullOrWhiteSpace(entry.Summary) && entry.Summary.Trim() != index?.EventSummary?.Trim())
                        builder.AppendLine(entry.Summary.Trim());
                    if (!string.IsNullOrWhiteSpace(entry.Detail)) builder.AppendLine(entry.Detail.Trim());
                }
            }
            return builder.ToString().TrimEnd();
        }

        internal static string RecallPuzzle(TraceTurnContext turn, string query, int topK, bool includeOriginalEvidence = true)
        {
            var storage = turn.Services.Storage;
            var tags = EnvironmentLogic.IsPublic(turn) ? new List<LifeTagRecord>() : RankByMoment(turn.Services.Router, query, storage.GetActiveLifeTags(), 8);
            var adapter = new ContextRecallAdapter();
            const string cognitionHeading = "【复盘形成的理解】";
            const string reviewHeading = "【复盘留下的经历与感受】";
            var runtime = SubjectRuntimeLogic.View(turn);
            var related = InnerLifeLogic.LiveAttention(runtime, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                .SelectMany(x => x.source_refs ?? new List<string>()).Where(x => x != null && x.StartsWith("cognition:", StringComparison.Ordinal))
                .Select(x => x.Substring(10)).Distinct().Take(6).ToList();
            var selected = adapter.Recall(new ContextRecallQuery { Text = query, RelatedIds = related,
                Cues = tags.Select(x => x.Id).ToList(), MaxItems = Math.Max(0, Math.Min(10, topK)),
                IncludeOriginalEvidence = includeOriginalEvidence,
                MaxChars = 3200 - cognitionHeading.Length - reviewHeading.Length - 3 * Environment.NewLine.Length },
                new IContextRecallSource[] { new CognitionContextRecallSource(storage, n => PuzzleViewLogic.CanRead(turn, n), m => PuzzleViewLogic.CanReadEvidence(turn, m)),
                    new DelegateContextRecallSource("runtime_day", q => RuntimeSliceLogic.Recall(turn, q)) });
            foreach (var item in selected.Where(x => x.SourceId == "cognition")) turn.Workspace.RecalledCognitionIds.Add(item.Id);
            return string.Join("\n", new[] {
                adapter.RenderNatural(cognitionHeading, selected.Where(x => x.SourceId == "cognition").ToList()),
                adapter.RenderNatural(reviewHeading, selected.Where(x => x.SourceId == "runtime_day").ToList())
            }.Where(x => !string.IsNullOrWhiteSpace(x)));
        }

        private static string RealmLabel(string realm) => realm switch
        {
            TraceRealmValues.ExternalWorld => "外部生活中的经历",
            TraceRealmValues.SharedScene => "共同文字场景中的经历",
            TraceRealmValues.Meta => "关于系统的交流",
            TraceRealmValues.ExplicitFiction => "共同创作的虚构情节",
            _ => "已整理的经历（情境未分类）"
        };

        private static string FormatDate(long unixMs)
        {
            if (unixMs <= 0) return "时间未记录";
            try { return DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime().ToString("yyyy年M月d日"); }
            catch (ArgumentOutOfRangeException) { return "时间未记录"; }
        }
    }
}
