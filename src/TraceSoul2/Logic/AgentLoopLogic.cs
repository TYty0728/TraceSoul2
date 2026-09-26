using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Manager;
using TraceSoul2.Plugins;
using TraceSoul2.Prompts;

namespace TraceSoul2.Logic
{
    /// <summary>有界的观察、行动、反馈循环。状态不自动对外发送，只有最终 reply 进入发送链。</summary>
    public sealed class AgentLoopLogic
    {
        public const int MaxActionRounds = 4;
        public const int MaxActionsPerStep = 4;
        private readonly ILlmClient llm;
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
            { IncludeFields = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        public AgentLoopLogic(ILlmClient llm) { this.llm = llm ?? throw new ArgumentNullException(nameof(llm)); }

        public async Task<AgentStepData> RunAsync(TraceTurnContext turn, string memory,
            Func<List<TraceContributionDescriptorData>> catalogProvider,
            Func<BrainCapabilityCallData, CancellationToken, Task<TraceCapabilityResultData>> execute,
            CancellationToken token,
            Func<CancellationToken, Task> refreshContext = null)
        {
            var stable = BuildStable(turn);
            var history = new List<object>();
            var callIds = new Dictionary<string, string>(StringComparer.Ordinal);
            var executed = new Dictionary<string, TraceCapabilityResultData>(StringComparer.Ordinal);
            var groups = new Dictionary<string, string>(StringComparer.Ordinal);
            var responded = false;
            for (var round = 0; round <= MaxActionRounds; round++)
            {
                token.ThrowIfCancellationRequested();
                if (round > 0 && refreshContext != null) await refreshContext(token);
                var catalog = BuildCatalog(catalogProvider());
                var dynamic = MindLogic.BuildTurnPrompt(turn, null, false, Array.Empty<MindTemplate>(), string.Empty, false);
                dynamic += "\n【本轮上下文】\n" + string.Join("\n", turn.Workspace.ContextBlocks
                    .Where(x => x != null && x.FacetId != "identity.base").Select(x => x.Content));
                dynamic += "\n【当前可用能力】\n" + JsonSerializer.Serialize(catalog.Select(x => new
                {
                    id = x.Id, description = x.Description, body_id = MouthLogic.BodyOf(x), organ = MouthLogic.OrganOf(x),
                    parameters = x.ParametersJsonSchema, external_effect = x.HasExternalSideEffect,
                    when_to_use = x.WhenToUse, when_not_to_use = x.WhenNotToUse
                }), Json);
                dynamic += "\n【执行账本：仅设备回执是完成证据】\n" +
                    JsonSerializer.Serialize(turn.Services.Executions.List(turn.ConversationId)
                        .OrderBy(x => TraceExecutionRegistry.Terminal(x.Status) ? 1 : 0).Take(24), Json);
                if (history.Count > 0) dynamic += "\n【本轮行动与结果，未发送的 reply 不是对话历史】\n" + JsonSerializer.Serialize(history, Json);
                dynamic += "\n本轮是否必须回应：" + (turn.RequiresExpression && !responded ? "是" : "否") +
                    "。最终步骤请包含仍需保存的状态变化。";
                if (!turn.RequiresExpression)
                    dynamic += "\n【当前运行事件，不是对方发言】\n" + (turn.Moment?.Content ?? string.Empty);
                if (turn.Moment?.SourcePluginId == "runtime.execution")
                    dynamic += "\n【设备确认回执】\n" + Limit(turn.Moment.PayloadJson, 12000);
                if (round == MaxActionRounds) dynamic += "\n" + AgentLoopPrompts.Budget;
                var messages = LlmContextPackLogic.Assemble(llm, LlmContextPackLogic.SharedSystem(llm, turn), turn,
                    memory, turn.RequiresExpression ? turn.Moment?.Content ?? string.Empty : string.Empty,
                    AgentLoopPrompts.Header, stable, dynamic);
                var step = await DeepSeekStructuredOutputLogic.CompleteAsync<AgentStepData>(llm, messages,
                    value => Valid(value, turn.RequiresExpression && !responded, round == MaxActionRounds),
                    AgentLoopPrompts.Invalid, token, LlmContextPackLogic.BuildPromptCacheKey(llm, turn.ConversationId));
                MindLogic.Normalize(step);
                turn.Services.LogTiming(turn.TraceId, "Agent 推进", detail: "step=" + step.step + "｜round=" + (round + 1));
                if (step.actions == null || step.actions.Count == 0)
                {
                    step.reply = (step.reply ?? string.Empty).Trim();
                    step.speak = step.reply.Length > 0;
                    return step;
                }

                var results = new List<object>();
                foreach (var call in step.actions)
                {
                    token.ThrowIfCancellationRequested();
                    var descriptor = catalog.FirstOrDefault(x => x.Id == call.capability_id);
                    if (descriptor != null && string.IsNullOrWhiteSpace(call.body_id)) call.body_id = MouthLogic.BodyOf(descriptor);
                    var signature = Signature(call);
                    TraceCapabilityResultData result;
                    if (callIds.TryGetValue(call.call_id, out var oldSignature) && oldSignature != signature)
                        result = Failure(call, "同一 call_id 不可改成另一项行动。");
                    else if (executed.TryGetValue(signature, out var previous))
                        result = new TraceCapabilityResultData { CallId = call.call_id, CapabilityId = call.capability_id,
                            Status = previous.Status, Summary = "本轮已执行，不重复产生副作用。" + previous.Summary,
                            Payload = previous.Payload, ExecutionId = previous.ExecutionId };
                    else if (descriptor == null)
                        result = Failure(call, "能力不在当前可用目录。");
                    else if (!string.IsNullOrWhiteSpace(call.body_id) && call.body_id != MouthLogic.BodyOf(descriptor))
                        result = Failure(call, "目标身体与能力绑定不符。");
                    else if (!string.IsNullOrWhiteSpace(call.group_id) && groups.TryGetValue(call.group_id, out var groupBody) &&
                             groupBody != call.body_id)
                        result = Failure(call, "同一动作组不能混用不同身体。");
                    else if (!HasRequiredArguments(call, descriptor))
                        result = Failure(call, "缺少能力 schema 声明的必填参数。");
                    else
                    {
                        if (!string.IsNullOrWhiteSpace(call.group_id)) groups[call.group_id] = call.body_id;
                        call.execution_id = null; // 运行标识只能由执行器分配。
                        result = await execute(call, token) ?? Failure(call, "执行没有返回结果。");
                        executed[signature] = result;
                        var organ = MouthLogic.OrganOf(descriptor);
                        if ((result.Status == "success" || result.Status == "running" || result.Status == "accepted") &&
                            (organ == BodyOrganValues.Voice || organ == BodyOrganValues.Text) &&
                            IsReplyBody(turn, descriptor)) responded = true;
                    }
                    if (!callIds.ContainsKey(call.call_id)) callIds[call.call_id] = signature;
                    if (!turn.Workspace.Results.Contains(result)) turn.Workspace.Results.Add(result);
                    results.Add(new { call, result = new { result.Status, result.Summary,
                        Payload = Limit(result.Payload, 12000), result.ExecutionId } });
                }
                // 不把中间草稿写入历史，也不向平台发送；完整请求/结果供下一步修正判断。
                history.Add(new { state = step, results });
            }
            throw new InvalidOperationException("Agent 行动预算耗尽。");
        }

        public static List<TraceContributionDescriptorData> BuildCatalog(IEnumerable<TraceContributionDescriptorData> source)
        {
            var result = (source ?? Enumerable.Empty<TraceContributionDescriptorData>())
                .Where(x => ToolLookupLogic.IsLookupEligible(x) || x?.Id == "dialogue.recent_history").ToList();
            result.RemoveAll(x => x.Id == "memory.recall" || x.Id == "execution.cancel");
            result.Add(new TraceContributionDescriptorData { Id = "memory.recall", Kind = TraceContributionKindValues.CallableNerve,
                Description = "按具体问题检索真实共同记忆，不生成或改写记忆。", ParametersJsonSchema = "{\"required\":[\"query\"],\"properties\":{\"query\":{\"type\":\"string\"}}}" });
            result.Add(new TraceContributionDescriptorData { Id = "execution.cancel", Kind = TraceContributionKindValues.CallableNerve,
                Description = "请求停止当前会话中的持续语音或动作；必须等待设备确认停止。", ParametersJsonSchema = "{\"required\":[\"execution_id\"],\"properties\":{\"execution_id\":{\"type\":\"string\"}}}" });
            return result.GroupBy(x => x.Id, StringComparer.Ordinal).Select(x => x.First()).ToList();
        }

        private static bool IsReplyBody(TraceTurnContext turn, TraceContributionDescriptorData descriptor)
        {
            if (turn.Moment != null && MouthLogic.BodyOfPlugin(turn.Moment.SourcePluginId) == MouthLogic.BodyOf(descriptor))
                return true;
            var reply = turn.Services.AvailableCatalogProvider?.Invoke(turn)?.FirstOrDefault(x =>
                x.Kind == TraceContributionKindValues.Effector && MouthLogic.OrganOf(x) == BodyOrganValues.Text);
            return reply != null && MouthLogic.BodyOf(reply) == MouthLogic.BodyOf(descriptor);
        }

        internal static bool Valid(AgentStepData value, bool needsReply, bool finalOnly)
        {
            if (value == null || (value.step != "finish" && value.step != "continue" && value.step != "wait")) return false;
            if (!string.IsNullOrWhiteSpace(value.tool_call) || value.WantsLeave() || value.WantsMemory()) return false;
            var actions = value.actions ?? new List<BrainCapabilityCallData>();
            if (actions.Count > 0)
                return !finalOnly && value.step == "continue" && actions.Count <= MaxActionsPerStep && actions.All(x =>
                    x != null && !string.IsNullOrWhiteSpace(x.call_id) && x.call_id.Length <= 80 &&
                    !string.IsNullOrWhiteSpace(x.capability_id) && x.capability_id.Length <= 160 &&
                    (x.body_id?.Length ?? 0) <= 160 && (x.group_id?.Length ?? 0) <= 80 &&
                    (x.arguments == null || (x.arguments.Count <= 16 && x.arguments.All(a => a != null &&
                        !string.IsNullOrWhiteSpace(a.name) && a.name.Length <= 80 && (a.value?.Length ?? 0) <= 12000) &&
                        x.arguments.Select(a => a.name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == x.arguments.Count)));
            if (value.step == "wait") return !needsReply && string.IsNullOrWhiteSpace(value.reply) && !value.refine;
            return value.step == "finish" && (!(needsReply || value.refine) || !string.IsNullOrWhiteSpace(value.reply));
        }

        private static string BuildStable(TraceTurnContext turn)
        {
            var builder = new StringBuilder(AgentLoopPrompts.Rules);
            builder.AppendLine().AppendLine(CorePrompts.Expressor.ExpressionPosture);
            foreach (var append in turn.Services.MindPromptAppends.Concat(turn.Services.MindJsonFields).ToArray())
            {
                try { var text = append?.Invoke(turn); if (!string.IsNullOrWhiteSpace(text)) builder.AppendLine(text.Trim()); }
                catch (Exception error) { turn.Services.LogTiming(turn.TraceId, "Agent 器官提示失败", detail: error.GetType().Name); }
            }
            return builder.ToString();
        }
        private static bool HasRequiredArguments(BrainCapabilityCallData call, TraceContributionDescriptorData descriptor)
        {
            if (string.IsNullOrWhiteSpace(descriptor.ParametersJsonSchema)) return true;
            try
            {
                using var schema = JsonDocument.Parse(descriptor.ParametersJsonSchema);
                return !schema.RootElement.TryGetProperty("required", out var required) || required.EnumerateArray()
                    .All(x => !string.IsNullOrWhiteSpace(call.GetArgument(x.GetString())));
            }
            // 旧插件的 schema 是 {query:string} 一类说明文本，具体校验继续由插件负责。
            catch (JsonException) { return true; }
            catch (InvalidOperationException) { return false; }
        }
        private static string Signature(BrainCapabilityCallData call) => JsonSerializer.Serialize(new
        {
            call.capability_id, body = call.body_id ?? string.Empty,
            arguments = (call.arguments ?? new List<BrainCallArgumentData>())
                .Select(x => new { name = x.name.ToLowerInvariant(), value = x.value ?? string.Empty })
                .OrderBy(x => x.name, StringComparer.Ordinal)
        }, Json);
        private static TraceCapabilityResultData Failure(BrainCapabilityCallData call, string summary) => new TraceCapabilityResultData
            { CallId = call.call_id, CapabilityId = call.capability_id, Status = "failed", Summary = summary, Payload = string.Empty };
        private static string Limit(string value, int max) => (value ?? string.Empty).Length <= max ? value ?? string.Empty : value.Substring(0, max);
    }
}
