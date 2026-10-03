using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var pending = store.GetRuntimeSlices(root, day, true);
                if (pending.Count == 0) break;
                var first = pending[0];
                var batch = new List<RuntimeSliceRecord>();
                var content = new StringBuilder();
                foreach (var slice in pending.Where(x => x.ContextConversationId == first.ContextConversationId && x.MemoryVisibility == first.MemoryVisibility))
                {
                    var line = slice.Id + "｜来源:" + slice.MomentId + "｜" + slice.CreatedUnixMs + "｜" + slice.SnapshotJson + "\n";
                    if (content.Length + line.Length > 16000 || batch.Count == 80) break;
                    batch.Add(slice); content.Append(line);
                }
                if (batch.Count == 0) throw new InvalidOperationException("当下切片超过夜间单批预算，保留原件等待处理。");
                await ReviewBatchAsync(store, llm, day, batch, token);
                count += batch.Count;
            }
            await FinishDayAsync(store, llm, root, day, token);
            return count;
        }

        private static async Task FinishDayAsync(SqliteMemoryManager store, ILlmClient llm, string root, string day, CancellationToken token)
        {
            foreach (var scope in store.GetRuntimeDayReviews(root, day).GroupBy(x => (x.ContextConversationId, x.MemoryVisibility)))
            {
                token.ThrowIfCancellationRequested();
                if (store.GetRuntimeDaySummaries(root, day).Any(x =>
                    x.ContextConversationId == scope.Key.ContextConversationId && x.MemoryVisibility == scope.Key.MemoryVisibility)) continue;
                var reviews = scope.ToList();
                store.BeginRuntimeDaySummary(reviews[0]);
                var parts = reviews.Select(x => x.Summary).ToList();
                // 极长的一天逐层合并全部分段，每层严格缩小材料；不把旧成稿再叠到新正文。
                while (parts.Sum(x => x.Length + 2) > 12000)
                {
                    var next = new List<string>();
                    var group = new List<string>(); var chars = 0;
                    foreach (var part in parts)
                    {
                        if (group.Count > 0 && chars + part.Length + 2 > 12000)
                        {
                            next.Add(await ComposeSummaryAsync(llm, day, scope.Key.MemoryVisibility, group, token));
                            group.Clear(); chars = 0;
                        }
                        group.Add(part); chars += part.Length + 2;
                    }
                    if (group.Count == 1 && group[0].Length <= DaySummaryLimit) next.Add(group[0]);
                    else if (group.Count > 0) next.Add(await ComposeSummaryAsync(llm, day, scope.Key.MemoryVisibility, group, token));
                    parts = next;
                }
                var summary = parts.Count == 1 && parts[0].Length <= DaySummaryLimit
                    ? parts[0] : await ComposeSummaryAsync(llm, day, scope.Key.MemoryVisibility, parts, token);
                token.ThrowIfCancellationRequested();
                store.CommitRuntimeDaySummary(reviews.Select(x => x.Id), summary);
            }
        }

        private static async Task<string> ComposeSummaryAsync(ILlmClient llm, string day, string visibility,
            List<string> parts, CancellationToken token)
        {
            var prompt = "将 " + day + " 的分段材料整理成一篇完整的当天回望。"
                + "正文约800～1000字，最多1200字，经历少时可以更短。围绕这一天真正重要的经历、变化与余味组织语言，"
                + "相同事件只讲一次，照顾动作、语气和重复感受合并为简洁表述，保留关键转折、条件和未解之处。"
                + "材料中的回想与转述保持原来的性质，主观感受与共同文字场景仍按主观记录理解。"
                + "各段来自同一天同一环境，按发生顺序排列；成稿覆盖全部材料的重要意义，而非逐段拼接。"
                + "当前范围：" + visibility + "。只输出 JSON {\"summary\":\"当天回望\"}。\n\n分段材料：\n"
                + string.Join("\n\n", parts.Select((x, i) => "片段" + (i + 1) + "：\n" + x));
            var result = await DeepSeekStructuredOutputLogic.CompleteAsync<ReviewOutput>(llm,
                new List<DeepSeekMessageData> { new("system", prompt), new("user", "提炼这一天，形成一篇约一千字的回望。") },
                x => !string.IsNullOrWhiteSpace(x?.summary) && x.summary.Length <= DaySummaryLimit,
                "summary 缺失或为空，请填写当天回望。", token,
                validationError: x => !string.IsNullOrWhiteSpace(x?.summary) && x.summary.Length > DaySummaryLimit
                    ? "summary 当前 " + x.summary.Length + " 字，超过整天回望1200字上限。请合并重复叙述，提炼为800～1000字的一篇回望。" : null);
            return result.summary;
        }

        private sealed class SummaryTooLongException : InvalidOperationException
        {
            public SummaryTooLongException(int length) : base("summary 当前 " + length +
                " 字，超过材料整理1200字上限。请围绕本批材料合并重复叙述，保留经历转折、感受变化与未解之处，提炼为约200～400字的材料。") { }
        }

        private static async Task ReviewBatchAsync(SqliteMemoryManager store, ILlmClient llm, string day,
            List<RuntimeSliceRecord> batch, CancellationToken token, int splitDepth = 0)
        {
            token.ThrowIfCancellationRequested();
            var first = batch[0];
            var content = string.Concat(batch.Select(slice => slice.Id + "｜来源:" + slice.MomentId + "｜" +
                slice.CreatedUnixMs + "｜" + slice.SnapshotJson + "\n"));
            var prompt = "整理 " + day + " 中这一批当下切片，提炼供当天回望使用的材料。"
                    + "程序在各批材料齐备后统一形成整天约一千字的成稿；本次正文对应下方这一批材料。"
                    + "沿着本批发生的变化组织语言，同一件事的反复描述合成一次，保留重要转折、条件、矛盾与未解之处。"
                    + "围绕主体自身、专属用户、世界、具体关系理解这一天；没有依据的方向留空，不强凑四类或性格结论。"
                    + "这是当时的主观记录，new_fact/today/活动描述都不等于外部核实，想做或说过不等于做成。"
                    + "专属用户只能由来源 speaker.owner 确认，其他人不能默认继承两人的关系。"
                    + "保留变化、矛盾、条件与未知，不把一时感受直接定成人格。不生成身份卡或认知写入。"
                    + "当前可见范围：" + first.MemoryVisibility + "，只处理这一环境。"
                    + "本批材料通常约200～400字，材料少时可以更短，最多1200字。"
                    + "只输出 JSON {\"summary\":\"这一批经历与感受的回望\"}。\n"
                    + "\n本批完整切片：\n" + content;
            try
            {
                var output = await DeepSeekStructuredOutputLogic.CompleteAsync<ReviewOutput>(llm,
                    new List<DeepSeekMessageData> { new("system", prompt), new("user", "整理本批切片，保留经历与感受原本的性质。") },
                    x =>
                    {
                        if (string.IsNullOrWhiteSpace(x?.summary)) return false;
                        if (x.summary.Length > DaySummaryLimit) throw new SummaryTooLongException(x.summary.Length);
                        return true;
                    },
                    "summary 缺失或为空，请在该字段填写本批经历与感受的回望。", token);
                store.CommitRuntimeSliceReview(batch.Select(x => x.Id), output.summary);
            }
            // 仅正文持续超长时缩小原始材料；语法、类型、网络或取消错误保持原失败路径。
            catch (InvalidOperationException exception) when (exception.InnerException is SummaryTooLongException &&
                batch.Count > 1 && splitDepth < 2)
            {
                var middle = batch.Count / 2;
                await ReviewBatchAsync(store, llm, day, batch.Take(middle).ToList(), token, splitDepth + 1);
                await ReviewBatchAsync(store, llm, day, batch.Skip(middle).ToList(), token, splitDepth + 1);
            }
        }
    }
}
