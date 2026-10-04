using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Manager;
using TraceSoul2.Plugins;
using TraceSoul2.Util;

namespace TraceSoul2.Logic
{
    /// <summary>白天只追加原始切片；整理只由日终入口调用，不增加实时模型阶段。</summary>
    public static class RuntimeSliceLogic
    {
        public const int DaySummaryTarget = 500;
        public const int DaySummaryAccept = 1000;
        public sealed class ReviewOutput { public string summary; }
        public static IReadOnlyList<ContextRecallCandidate> Recall(TraceTurnContext turn, ContextRecallQuery query)
        {
            if (turn.Services.Storage is not SqliteMemoryManager store) return Array.Empty<ContextRecallCandidate>();
            var terms = CognitionContextRecallSource.Terms(query.Text);
            if (terms.Count == 0) return Array.Empty<ContextRecallCandidate>();
            return store.GetRuntimeReviewCandidates(PuzzleViewLogic.Root(turn), EnvironmentLogic.IsPublic(turn) ? turn.ConversationId : null)
                .Select(x =>
                {
                    var other = CognitionContextRecallSource.Terms(x.Summary);
                    var relevance = other.Count == 0 ? 0 : (float)terms.Count(other.Contains) / Math.Min(terms.Count, other.Count);
                    return new ContextRecallCandidate { Id = x.Id, SourceId = "runtime_day", Text = x.Summary,
                        Relevance = relevance, RenderedText = "【" + x.DayKey + " · 当天经历与感受的主观回望】\n" + x.Summary };
                }).Where(x => x.Relevance >= .18f).OrderByDescending(x => x.Relevance).Take(10).ToList();
        }
        public static void Capture(TraceTurnContext turn, AgentStepData step)
        {
            if (step == null || turn.Services.Storage is not SqliteMemoryManager store) return;
            store.AppendRuntimeSlice(new RuntimeSliceRecord { RootConversationId = PuzzleViewLogic.Root(turn),
                ContextConversationId = turn.ConversationId, MomentId = turn.Moment.Id,
                SnapshotJson = TraceJson.ToJson(new { step.inner, step.mood, step.mood_changed, step.affect,
                    step.scene, step.attention, step.today, step.new_fact, step.location, step.activity, step.activity_detail,
                    step.sleep, step.heartbeat_intent, speaker = new { owner = turn.Environment?.SpeakerIsOwner ?? false,
                        platform = turn.Environment?.PlatformId, id = turn.Environment?.SpeakerId } }) });
        }

        public static string EvidenceContext(SqliteMemoryManager store, IEnumerable<MomentRecord> evidence)
        {
            var lines = store.GetRuntimeSlicesForMoments(evidence.Select(x => x.Id))
                .Select(x => x.MomentId + "｜" + SliceFeeling(x.SnapshotJson))
                .Where(x => !x.EndsWith("｜", StringComparison.Ordinal)).ToList();
            if (lines.Count == 0) return "";
            return "\n【这些经历发生时的 runtime 切片】\n切片是当时的主观感受、关注与轨迹，不是外部事实确认；"
                + "只能引用本批真实 Moment 作为认知依据，不将自己的想法当作对方反馈。\n"
                + string.Join("\n", lines);
        }

        private static string SliceFeeling(string json)
        {
            try
            {
                using var document = JsonDocument.Parse(json ?? "");
                var root = document.RootElement;
                var parts = new List<string>();
                foreach (var key in new[] { "inner", "today", "activity" })
                {
                    if (!root.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String) continue;
                    var text = (value.GetString() ?? "").Trim();
                    if (text.Length > 0) parts.Add(text);
                }
                return string.Join("；", parts);
            }
            catch (JsonException) { return ""; }
        }

        public static IEnumerable<List<MomentRecord>> EvidenceBatches(SqliteMemoryManager store, IEnumerable<MomentRecord> moments)
        {
            var sources = moments.Where(x => x != null && x.MemoryStatus != "operational" && !string.IsNullOrWhiteSpace(x.Content))
                .GroupBy(x => x.Id).Select(x => x.First()).OrderBy(x => x.CreatedUnixMs).ToList();
            var slices = store.GetRuntimeSlicesForMoments(sources.Select(x => x.Id)).ToDictionary(x => x.MomentId);
            foreach (var scope in sources.GroupBy(x => x.ConversationId))
            {
                var batch = new List<MomentRecord>();
                var chars = 0;
                foreach (var source in scope)
                {
                    var cost = source.Content.Length + source.Id.Length + 256 +
                        (slices.TryGetValue(source.Id, out var slice) ? slice.SnapshotJson.Length : 0);
                    if (cost > 24000) throw new InvalidOperationException("单条经历与当下切片超过夜间证据预算，原件保留，不能截断后标记完成。");
                    if (chars + cost > 24000 || batch.Count == 200)
                    { yield return batch; batch = new(); chars = 0; }
                    batch.Add(source); chars += cost;
                }
                if (batch.Count > 0) yield return batch;
            }
        }

        public static async Task<int> ReviewDayAsync(SqliteMemoryManager store, ILlmClient llm, string root,
            string day, CancellationToken token = default)
        {
            var count = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var pending = store.GetRuntimeSlices(root, day, true);
                if (pending.Count == 0)
                {
                    await PublishDayAsync(store, llm, root, day, token);
                    return count;
                }
                count += await WriteNextPassageAsync(store, llm, root, day, pending, token);
            }
        }

        private static async Task PublishDayAsync(SqliteMemoryManager store, ILlmClient llm, string root, string day, CancellationToken token)
        {
            var pending = store.GetRuntimeSlices(root, day, true);
            foreach (var scope in store.GetRuntimeDayReviews(root, day).GroupBy(x => (x.ContextConversationId, x.MemoryVisibility)))
            {
                if (HasCurrentSummary(store, root, day, scope.Key.ContextConversationId, scope.Key.MemoryVisibility)) continue;
                if (pending.Any(x => x.ContextConversationId == scope.Key.ContextConversationId && x.MemoryVisibility == scope.Key.MemoryVisibility)) continue;
                var notes = scope.ToList();
                var text = Joined(notes);
                if (string.IsNullOrWhiteSpace(text))
                    throw new InvalidOperationException("当天回望没有可发表的正文。");
                if (text.Length > DaySummaryAccept)
                {
                    var shortened = await CondenseOnceAsync(llm, day, text, token);
                    if (shortened.Length == 0 || shortened.Length > DaySummaryAccept)
                        throw new InvalidOperationException("当天回望合并后有" + text.Length + "字，精简一次后仍有" + shortened.Length + "字，超过1000字。不截断，已写各段保留。");
                    text = shortened;
                }
                store.CommitRuntimeDaySummary(notes.Select(x => x.Id), text);
            }
        }

        private static async Task<string> CondenseOnceAsync(ILlmClient llm, string day, string text, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var raw = await llm.CompleteJsonAsync(new List<DeepSeekMessageData> {
                new("system", "下面是已经按时间接好的 " + day + " 当天回望，现在" + text.Length + "字，超过1000字。"
                    + "只做一件事：把字数压到500字以内。删掉次要的事，只留少数转折。"
                    + "不要补充这篇里没有的事，不要解释。"
                    + "只输出 JSON {\"summary\":\"压短后的正文\"}。\n\n" + text),
                new("user", "把字数压到500字以内。") }, token);
            ReviewOutput output;
            try { output = DeepSeekStructuredOutputLogic.Read<ReviewOutput>(raw); }
            catch (JsonException) { throw new InvalidOperationException("精简结果的 summary 不是正文。"); }
            if (string.IsNullOrWhiteSpace(output?.summary))
                throw new InvalidOperationException("精简结果的 summary 不是正文。");
            return output.summary.Trim();
        }

        private static async Task<int> WriteNextPassageAsync(SqliteMemoryManager store, ILlmClient llm, string root, string day,
            List<RuntimeSliceRecord> pending, CancellationToken token)
        {
            var first = pending[0];
            var scopePending = pending.Where(x => x.ContextConversationId == first.ContextConversationId && x.MemoryVisibility == first.MemoryVisibility).ToList();
            var notes = store.GetRuntimeDayReviews(root, day)
                .Where(x => x.ContextConversationId == first.ContextConversationId && x.MemoryVisibility == first.MemoryVisibility).ToList();
            var batches = PlanBatches(scopePending);
            return await WritePassageAsync(store, llm, day, batches[0], notes.Count == 0 && batches.Count == 1, token);
        }

        private static async Task<int> WritePassageAsync(SqliteMemoryManager store, ILlmClient llm, string day,
            List<RuntimeSliceRecord> batch, bool wholeDay, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var first = batch[0];
            var output = await DeepSeekStructuredOutputLogic.CompleteAsync<ReviewOutput>(llm,
                PassagePrompt(day, first.MemoryVisibility, wholeDay, batch.Count, string.Concat(batch.Select(PassageLine))),
                x => !string.IsNullOrWhiteSpace(x?.summary),
                "summary 缺失或为空，请写下这一段正文。", token);
            store.CommitRuntimeSliceReview(batch.Select(x => x.Id), output.summary);
            return batch.Count;
        }

        private static List<DeepSeekMessageData> PassagePrompt(string day, string visibility, bool wholeDay, int sliceCount, string body)
        {
            var place = wholeDay
                ? "这一篇就是 " + day + " 的当天回望。"
                : "这是 " + day + " 当天回望里按时间排列的一段。各段会按时间接成整篇。";
            var crowded = sliceCount * 40 > DaySummaryTarget;
            var prompt = place
                + "整篇保持在500字以内；事情少就只写那么短。"
                + (crowded ? "这一批切片多于整篇篇幅能逐条写下的数量，大多数不要出现。" : "")
                + "只写这一批切片里新发生的变化：事情转到哪里，感受和之前有什么不同，还有什么没定。"
                + "同一件事只出现一次。重复的照顾、语气和同一种心情收成一句。这一批以外的经历不要写进来。"
                + "切片里的回想和转述保持原来的性质；主观感受仍按主观记录理解。"
                + "new_fact、today 和活动描述都不是外部核实，想做或说过不等于做成。"
                + "专属用户只能由 speaker.owner 确认，其他人不能默认继承两人的关系。"
                + "没有依据的方向不写。不把一时感受写成性格，不写身份卡或认知结论。"
                + "当前范围：" + visibility + "。只输出 JSON {\"summary\":\"这一段正文\"}。\n\n这一段的切片：\n" + body;
            return new List<DeepSeekMessageData> {
                new("system", prompt), new("user", wholeDay ? "用500字以内写下这一天。" : "写下这一段。整篇要能收在500字以内。") };
        }

        private static List<List<RuntimeSliceRecord>> PlanBatches(List<RuntimeSliceRecord> pending)
        {
            var batches = new List<List<RuntimeSliceRecord>>();
            var batch = new List<RuntimeSliceRecord>();
            var chars = 0;
            foreach (var slice in pending)
            {
                var line = PassageLine(slice);
                if (line.Length > 16000)
                {
                    if (batch.Count > 0) batches.Add(batch);
                    batch = new List<RuntimeSliceRecord>();
                    if (batches.Count == 0)
                        throw new InvalidOperationException("当下切片超过夜间单批预算，保留原件等待处理。");
                    break;
                }
                if (batch.Count > 0 && (chars + line.Length > 16000 || batch.Count == 80))
                {
                    batches.Add(batch);
                    batch = new List<RuntimeSliceRecord>();
                    chars = 0;
                }
                batch.Add(slice);
                chars += line.Length;
            }
            if (batch.Count > 0) batches.Add(batch);
            if (batches.Count == 0) throw new InvalidOperationException("当下切片超过夜间单批预算，保留原件等待处理。");
            return batches;
        }

        private static string PassageLine(RuntimeSliceRecord slice) =>
            slice.Id + "｜来源:" + slice.MomentId + "｜" + slice.CreatedUnixMs + "｜" + slice.SnapshotJson + "\n";

        private static string Joined(IEnumerable<RuntimeDayReviewRecord> notes) =>
            string.Join("\n\n", notes.Select(x => (x.Summary ?? "").Trim()).Where(x => x.Length > 0));

        private static bool HasCurrentSummary(SqliteMemoryManager store, string root, string day, string context, string visibility) =>
            store.GetRuntimeDaySummaries(root, day).Any(x => x.ContextConversationId == context && x.MemoryVisibility == visibility);
    }
}
