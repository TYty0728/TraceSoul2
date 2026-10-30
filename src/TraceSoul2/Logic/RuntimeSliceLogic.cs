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
    /// <summary>保存当下原始切片，提供证据上下文与历史回望读取；不执行独立日终成稿。</summary>
    public static class RuntimeSliceLogic
    {
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
            var records = evidence.ToList();
            var metadata = records.Where(x => !string.IsNullOrWhiteSpace(x.Type)).Select(x =>
                x.Id + "｜" + MomentTypes.Label(x.Type) + "｜" + MomentTypes.PlatformLabel(x.SourcePlatform) +
                (string.IsNullOrWhiteSpace(x.ParentMomentId) ? "" : "｜关联输入=" + x.ParentMomentId)).ToList();
            var semantics = metadata.Count == 0 ? "" : "\n【经历类型与来源】\n感知观察不是对方亲口发言；不把视觉、接触或身体感受冒认为用户自述。\n" + string.Join("\n", metadata);
            var lines = store.GetRuntimeSlicesForMoments(records.Select(x => x.Id))
                .Select(x => x.MomentId + "｜" + SliceFeeling(x.SnapshotJson))
                .Where(x => !x.EndsWith("｜", StringComparison.Ordinal)).ToList();
            if (lines.Count == 0) return semantics;
            return semantics + "\n【这些经历发生时的 runtime 切片】\n切片是当时的主观感受、关注与轨迹，不是外部事实确认；"
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
                    if (cost > 24000) throw new ReviewProsePendingException("单条经历与当下切片超过夜间证据预算，原件保留，不能截断后标记完成。");
                    if (chars + cost > 24000 || batch.Count == 200)
                    { yield return batch; batch = new(); chars = 0; }
                    batch.Add(source); chars += cost;
                }
                if (batch.Count > 0) yield return batch;
            }
        }

    }
}
