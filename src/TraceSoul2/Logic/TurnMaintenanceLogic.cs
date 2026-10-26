using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Manager;
using TraceSoul2.Plugins;
using TraceSoul2.Prompts;

namespace TraceSoul2.Logic
{
    /// <summary>同一主体的轮后整理：快照内分析，宿主锁内按版本提交，不进入行动循环。</summary>
    public static class TurnMaintenanceLogic
    {
        private static readonly JsonSerializerOptions Json = new() {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        public sealed class Output
        {
            public List<Attention> attention { get; set; }
            public string scene { get; set; }
        }
        public sealed class Attention
        {
            public string kind { get; set; }
            public string content { get; set; }
        }

        public static KernelLogic.DeferredTurnWork Prepare(TraceTurnContext turn, AgentStepData decision)
        {
            if (decision == null || turn.Environment == null || EnvironmentLogic.IsPublic(turn) ||
                turn.Services.Storage is not SqliteMemoryManager store) return null;
            var llm = turn.Services.ReviewLlm ?? turn.Services.Llm;
            if (llm == null) return null;
            var root = PuzzleViewLogic.Root(turn);
            var subject = SubjectRuntimeLogic.Read(store, root);
            var runtime = SubjectRuntimeLogic.Current(turn);
            var revision = subject.Revision;
            var runtimeRevision = runtime.Revision;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            // 原文有界装入，不截断单条原件；过大的当轮留待后续轮次和夜间整理。
            var moments = turn.RecentMoments.Append(turn.Moment).Where(x => x != null)
                .GroupBy(x => x.Id).Select(x => x.Last()).TakeLast(8).ToList();
            while (moments.Count > 1 && moments.Sum(x => x.Content?.Length ?? 0) > 16000) moments.RemoveAt(0);
            if (moments.Sum(x => x.Content?.Length ?? 0) + (decision.reply?.Length ?? 0) > 20000) return null;
            var evidence = JsonSerializer.Serialize(new {
                at = DateTimeOffset.FromUnixTimeMilliseconds(now),
                attention = InnerLifeLogic.LiveAttention(runtime, now).Select(x => new { x.kind, x.content, x.UpdatedUnixMs }),
                scene = new { text = runtime.OngoingActivity, at = subject.Scene?.UpdatedUnixMs },
                moments = moments.Select(x => new { x.Role, x.Type, x.Content, x.CreatedUnixMs }),
                proposed_reply = decision.reply,
                results = turn.Workspace.Results.Where(x => x != null).TakeLast(8).Select(x => new { x.CapabilityId, x.Status, x.Summary })
            }, Json);
            if (evidence.Length > 28000) return null;
            var messages = new List<DeepSeekMessageData> {
                new("system", TurnMaintenancePrompts.Rules), new("user", evidence)
            };
            return new KernelLogic.DeferredTurnWork(turn.TraceId, async token =>
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                Output output;
                try
                {
                    var raw = await llm.CompleteJsonAsync(messages, timeout.Token);
                    output = JsonSerializer.Deserialize<Output>(raw ?? "");
                    if (output?.attention == null || output.attention.Count > 3 ||
                        (output.scene?.Length ?? 0) > 120 || output.attention.Any(x => x == null ||
                            x.kind is not ("topic" or "activity" or "concern") || string.IsNullOrWhiteSpace(x.content) ||
                            x.content.Length > 160) || output.attention.Select(x => x.content.Trim()).Distinct().Count() != output.attention.Count)
                        throw new InvalidOperationException("整理结果格式或篇幅不合格");
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    turn.Services.LogTiming(turn.TraceId, "轮后关注整理未更新", detail: error.GetType().Name);
                    return null;
                }
                return commitToken =>
                {
                    commitToken.ThrowIfCancellationRequested();
                    var changes = new InnerRuntimeWriteData {
                        attention = output.attention.Select(x => new AttentionWriteData { kind = x.kind, content = x.content.Trim() }).ToList(),
                        ongoing_activity = output.scene
                    };
                    var applied = SubjectRuntimeLogic.CommitMaintenance(turn, revision, runtimeRevision, changes, now);
                    turn.Services.LogTiming(turn.TraceId, applied ? "轮后关注整理已提交" : "轮后关注整理已过期，丢弃旧结果");
                    return Task.CompletedTask;
                };
            }, maintenance: true);
        }
    }
}
