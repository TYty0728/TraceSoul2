using System;
using System.Collections.Generic;
using System.Linq;
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
        public const int DaySummaryTarget = 1000;
        public const int DaySummaryLimit = 1200;
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
            var slices = store.GetRuntimeSlicesForMoments(evidence.Select(x => x.Id));
            if (slices.Count == 0) return "";
            return "\n【这些经历发生时的 runtime 切片】\n切片是当时的主观感受、关注与轨迹，不是外部事实确认；"
                + "只能引用本批真实 Moment 作为认知依据，不将自己的想法当作对方反馈。\n"
                + string.Join("\n", slices.Select(x => x.MomentId + "｜" + x.SnapshotJson));
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
            var rewritten = new HashSet<string>();
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (ReplaceOutdatedPassages(store, root, day, rewritten)) continue;
                var pending = store.GetRuntimeSlices(root, day, true);
                if (pending.Count == 0)
                {
                    PublishDay(store, root, day);
                    return count;
                }
                count += await WriteNextPassageAsync(store, llm, root, day, pending, rewritten, token);
            }
        }

        /// <summary>旧指令写成的长文不是新正文的材料。超长的整段退回切片，按各自篇幅重写。</summary>
        private static bool ReplaceOutdatedPassages(SqliteMemoryManager store, string root, string day, HashSet<string> rewritten)
        {
            var changed = false;
            foreach (var scope in store.GetRuntimeDayReviews(root, day).GroupBy(x => (x.ContextConversationId, x.MemoryVisibility)))
            {
                if (HasCurrentSummary(store, root, day, scope.Key.ContextConversationId, scope.Key.MemoryVisibility)) continue;
                var notes = scope.ToList();
                if (Joined(notes).Length <= DaySummaryLimit && notes.All(x => (x.Summary ?? "").Trim().Length <= DaySummaryLimit)) continue;
                if (!rewritten.Add(ScopeKey(scope.Key.ContextConversationId, scope.Key.MemoryVisibility)))
                    throw new InvalidOperationException("按各自篇幅重写后，当天回望仍然超过一千字。");
                store.ReleaseRuntimeDayReviews(notes.Select(x => x.Id));
                changed = true;
            }
            return changed;
        }

        private static void PublishDay(SqliteMemoryManager store, string root, string day)
        {
            var pending = store.GetRuntimeSlices(root, day, true);
            foreach (var scope in store.GetRuntimeDayReviews(root, day).GroupBy(x => (x.ContextConversationId, x.MemoryVisibility)))
            {
                if (HasCurrentSummary(store, root, day, scope.Key.ContextConversationId, scope.Key.MemoryVisibility)) continue;
                if (pending.Any(x => x.ContextConversationId == scope.Key.ContextConversationId && x.MemoryVisibility == scope.Key.MemoryVisibility)) continue;
                var notes = scope.ToList();
                var text = Joined(notes);
                if (string.IsNullOrWhiteSpace(text) || text.Length > DaySummaryLimit)
                    throw new InvalidOperationException("当天回望超出一千字篇幅，不能靠事后压缩保存。");
                store.CommitRuntimeDaySummary(notes.Select(x => x.Id), text);
            }
        }

        private static async Task<int> WriteNextPassageAsync(SqliteMemoryManager store, ILlmClient llm, string root, string day,
            List<RuntimeSliceRecord> pending, HashSet<string> rewritten, CancellationToken token)
        {
            var first = pending[0];
            var scopePending = pending.Where(x => x.ContextConversationId == first.ContextConversationId && x.MemoryVisibility == first.MemoryVisibility).ToList();
            var notes = store.GetRuntimeDayReviews(root, day)
                .Where(x => x.ContextConversationId == first.ContextConversationId && x.MemoryVisibility == first.MemoryVisibility).ToList();
            var batches = PlanBatches(scopePending);
            var room = RemainingRoom(Joined(notes).Length, notes.Count, batches.Count);
            if (room < batches.Count)
            {
                if (notes.Count == 0) throw new InvalidOperationException("当天切片分成的段落超过一千字能容纳的段数。");
                if (!rewritten.Add(ScopeKey(first.ContextConversationId, first.MemoryVisibility)))
                    throw new InvalidOperationException("按各自篇幅重写后，当天回望仍然超过一千字。");
                store.ReleaseRuntimeDayReviews(notes.Select(x => x.Id));
                return 0;
            }
            var limit = Math.Max(1, Math.Min(DaySummaryTarget, room / batches.Count));
            return await WritePassageAsync(store, llm, day, batches[0], limit, notes.Count == 0 && batches.Count == 1, token);
        }

        private static async Task<int> WritePassageAsync(SqliteMemoryManager store, ILlmClient llm, string day,
            List<RuntimeSliceRecord> batch, int limit, bool wholeDay, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var first = batch[0];
            var output = await DeepSeekStructuredOutputLogic.CompleteAsync<ReviewOutput>(llm,
                PassagePrompt(day, first.MemoryVisibility, limit, wholeDay, string.Concat(batch.Select(PassageLine))),
                x => !string.IsNullOrWhiteSpace(x?.summary) && x.summary.Trim().Length <= limit,
                "summary 缺失或为空，请写下这一段正文。", token,
                validationError: x =>
                {
                    var text = (x?.summary ?? string.Empty).Trim();
                    if (text.Length <= limit) return null;
                    return "summary 当前" + text.Length + "字，这一段最多" + limit + "字。按原篇幅重新写下完整 JSON，不要截断已写正文。";
                });
            store.CommitRuntimeSliceReview(batch.Select(x => x.Id), output.summary);
            return batch.Count;
        }

        private static List<DeepSeekMessageData> PassagePrompt(string day, string visibility, int limit, bool wholeDay, string body)
        {
            var place = wholeDay
                ? "这一篇就是 " + day + " 的当天回望，写完即是最终正文。"
                : "这是 " + day + " 当天回望里按时间排列的一段，写完即是最终正文，不与其他时段合起来再改。";
            var prompt = place
                + "动笔前篇幅已经定好：最多" + limit + "字；事情少就只写那么短。"
                + "只写这一批切片里新发生的变化：事情转到哪里，感受和之前有什么不同，还有什么没定。"
                + "同一件事只出现一次。重复的照顾、语气和同一种心情收成一句。这一批以外的经历不要写进来。"
                + "切片里的回想和转述保持原来的性质；主观感受仍按主观记录理解。"
                + "new_fact、today 和活动描述都不是外部核实，想做或说过不等于做成。"
                + "专属用户只能由 speaker.owner 确认，其他人不能默认继承两人的关系。"
                + "没有依据的方向不写。不把一时感受写成性格，不写身份卡或认知结论。"
                + "当前范围：" + visibility + "。只输出 JSON {\"summary\":\"这一段正文\"}。\n\n这一段的切片：\n" + body;
            return new List<DeepSeekMessageData> {
                new("system", prompt), new("user", "用最多" + limit + "字写下" + (wholeDay ? "这一天。" : "这一段。")) };
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

        private static int RemainingRoom(int used, int noteCount, int pendingBatches)
        {
            var bridge = noteCount > 0 && pendingBatches > 0 ? 2 : 0;
            var gaps = Math.Max(0, pendingBatches - 1) * 2;
            return DaySummaryTarget - used - bridge - gaps;
        }

        private static bool HasCurrentSummary(SqliteMemoryManager store, string root, string day, string context, string visibility) =>
            store.GetRuntimeDaySummaries(root, day).Any(x => x.ContextConversationId == context && x.MemoryVisibility == visibility);

        private static string ScopeKey(string context, string visibility) => context + "\n" + visibility;
    }
}
