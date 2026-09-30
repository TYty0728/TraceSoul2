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
    /// <summary>有界的观察、行动、反馈循环。状态独立保存，最终正文与显式表达行动分别进入发送链。</summary>
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
            var pendingState = new AgentTurnStateLogic();
            var appliedGoals = new List<AgentGoalUpdateData>();
            var callIds = new Dictionary<string, string>(StringComparer.Ordinal);
            var executed = new Dictionary<string, TraceCapabilityResultData>(StringComparer.Ordinal);
            var groups = new Dictionary<string, string>(StringComparer.Ordinal);
            var responded = false;
            for (var round = 0; round <= MaxActionRounds; round++)
            {
                token.ThrowIfCancellationRequested();
                if (round > 0 && refreshContext != null) await refreshContext(token);
                var catalog = BuildCatalog(catalogProvider());
                if (!AgentPromptContextLogic.HasActiveExecution(turn)) catalog.RemoveAll(x => x.Id == "execution.cancel");
                var dynamic = (catalog.Any(x => x.Id == "web.search" || x.Id == "web.read") ? AgentLoopPrompts.Search + "\n" : "") +
                    AgentPromptContextLogic.Context(turn) + AgentPromptContextLogic.Catalog(catalog) +
                    AgentPromptContextLogic.Executions(turn);
                if (history.Count > 0) dynamic += AgentPromptContextLogic.Results(history);
                dynamic += AgentPromptContextLogic.PendingState(pendingState.Values);
                dynamic += "\n本轮表达受理情况：" + (responded ? "已受理，无需重复追加。" : "尚未受理。") +
                    "已提案状态由程序保留，后续只需填写修订。";
                if (!EnvironmentLogic.IsHumanInput(turn))
                    dynamic += "\n【当前运行事件，不是对方发言】\n" + (turn.Moment?.Content ?? string.Empty);
                if (turn.Moment?.SourcePluginId == "runtime.execution")
                    dynamic += "\n【设备确认回执】\n" + Limit(turn.Moment.PayloadJson, 12000);
                if (round == MaxActionRounds) dynamic += "\n" + AgentLoopPrompts.Budget;
                var messages = LlmContextPackLogic.Assemble(llm, LlmContextPackLogic.SharedSystem(llm, turn), turn,
                    memory, EnvironmentLogic.IsHumanInput(turn) ? turn.Moment?.Content ?? string.Empty : string.Empty,
                    AgentLoopPrompts.Header, stable, dynamic);
                IReadOnlyCollection<string> providedFields = null;
                var output = await DeepSeekStructuredOutputLogic.CompleteAsync<AgentOutputData>(llm, messages,
                    null, AgentLoopPrompts.Invalid, token, LlmContextPackLogic.BuildPromptCacheKey(llm, turn.ConversationId),
                    value => ValidationError(value.ToRuntime(), turn.RequiresExpression && !responded, round == MaxActionRounds) ??
                        GoalMemoryLogic.ValidationError(turn, value.goal_updates),
                    (_, fields) => providedFields = fields);
                pendingState.Update(output, providedFields);
                pendingState.ApplyTo(output);
                var step = output.ToRuntime();
                step.state_fields = pendingState.Values.Keys.ToList();
                MindLogic.Normalize(step);
                // 明确反馈在执行或发送前落库；后续行动失败也不会抹掉已经听取的调整。
                GoalMemoryLogic.Apply(turn, step.goal_updates);
                appliedGoals.AddRange(step.goal_updates ?? new());
                step.applied_goal_updates = appliedGoals.ToList();
                turn.Services.LogTiming(turn.TraceId, "Agent 推进", detail: "step=" + step.step + "｜round=" + (round + 1));
                if (step.actions == null || step.actions.Count == 0)
                {
                    step.reply = (step.reply ?? string.Empty).Trim();
                    // 已选择媒体/动作也算本轮表达，不能因没有文字就被心跳逻辑误判为全程安静。
                    step.speak = step.reply.Length > 0 || responded;
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
                        if ((result.Status == "success" || result.Status == "running" || result.Status == "accepted") &&
                            IsConversationalExpression(descriptor) &&
                            IsReplyBody(turn, descriptor)) responded = true;
                    }
                    if (!callIds.ContainsKey(call.call_id)) callIds[call.call_id] = signature;
                    if (!turn.Workspace.Results.Contains(result)) turn.Workspace.Results.Add(result);
                    results.Add(AgentPromptContextLogic.Result(call, result));
                }
                // 尚未落库的状态变化单独保留，续推不复制整份输出或未发送草稿。
                history.AddRange(results);
            }
            throw new InvalidOperationException("Agent 行动预算耗尽。");
        }

        public static List<TraceContributionDescriptorData> BuildCatalog(IEnumerable<TraceContributionDescriptorData> source)
        {
            var result = (source ?? Enumerable.Empty<TraceContributionDescriptorData>())
                .Where(x => ToolLookupLogic.IsLookupEligible(x) || x?.Id == "dialogue.recent_history" ||
                    IsConversationalExpression(x)).ToList();
            result.RemoveAll(x => x.Id == "memory.recall" || x.Id == "execution.cancel");
            result.Add(new TraceContributionDescriptorData { Id = "memory.recall", Kind = TraceContributionKindValues.CallableNerve,
                Description = "按具体问题检索真实共同记忆，不生成或改写记忆。", ParametersJsonSchema = "{\"required\":[\"query\"],\"properties\":{\"query\":{\"type\":\"string\"}}}" });
            result.Add(new TraceContributionDescriptorData { Id = "execution.cancel", Kind = TraceContributionKindValues.CallableNerve,
                Description = "请求停止当前会话中的持续语音或动作；必须等待设备确认停止。", ParametersJsonSchema = "{\"required\":[\"execution_id\"],\"properties\":{\"execution_id\":{\"type\":\"string\"}}}" });
            return result.GroupBy(x => x.Id, StringComparer.Ordinal).Select(x => x.First()).ToList();
        }

        private static bool IsConversationalExpression(TraceContributionDescriptorData item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Id) || item.Kind != TraceContributionKindValues.Effector)
                return false;
            if (item.Id.StartsWith("memory.", StringComparison.OrdinalIgnoreCase) ||
                item.Id.StartsWith("identity.", StringComparison.OrdinalIgnoreCase) ||
                item.Id.StartsWith("time.", StringComparison.OrdinalIgnoreCase)) return false;
            var organ = MouthLogic.OrganOf(item);
            return organ == BodyOrganValues.Text || organ == BodyOrganValues.Voice ||
                organ == BodyOrganValues.Image ||
                organ == BodyOrganValues.Video || organ == "gesture";
        }

        private static bool IsReplyBody(TraceTurnContext turn, TraceContributionDescriptorData descriptor)
        {
            if (turn.Moment != null && MouthLogic.BodyOfPlugin(turn.Moment.SourcePluginId) == MouthLogic.BodyOf(descriptor))
                return true;
            var reply = turn.Services.AvailableCatalogProvider?.Invoke(turn)?.FirstOrDefault(x =>
                x.Kind == TraceContributionKindValues.Effector && MouthLogic.OrganOf(x) == BodyOrganValues.Text);
            return reply != null && MouthLogic.BodyOf(reply) == MouthLogic.BodyOf(descriptor);
        }

        internal static bool Valid(AgentStepData value, bool needsReply, bool finalOnly) =>
            ValidationError(value, needsReply, finalOnly) == null;

        internal static string ValidationError(AgentStepData value, bool needsReply, bool finalOnly)
        {
            if (value == null) return "$ 必须是 JSON 对象。";
            if (!SubjectRuntimeLogic.ValidAffect(value.affect)) return "$.affect 必须为 calm/joyful/sad/angry/anxious/tired/curious/mixed，不能包含私人原因。";
            if (value.step != "finish" && value.step != "continue" && value.step != "wait")
                return "$.step 必须填写 finish、continue 或 wait。";
            var actions = value.actions ?? new List<BrainCapabilityCallData>();
            if (actions.Count > 0)
            {
                if (finalOnly) return "$.actions：本轮行动预算已用完，清空 actions，根据已有结果输出最终 reply。";
                if (value.step != "continue")
                    return "$.step 与 $.actions 冲突：actions 非空时 step 必须为 continue；保留行动并改为 continue，执行结果回来后再 finish。当前 reply 是草稿，不会发送。若不需要行动则清空 actions。";
                if (value.refine) return "$.refine 与 $.actions 冲突：行动步骤必须 refine=false；仅最终有正文的 finish 可润色。";
                if (actions.Count > MaxActionsPerStep) return "$.actions 每步最多4项，请拆到后续步骤。";
                for (var i = 0; i < actions.Count; i++)
                {
                    var x = actions[i]; var path = "$.actions[" + i + "]";
                    if (x == null) return path + " 必须是行动对象，不能为 null。";
                    if (string.IsNullOrWhiteSpace(x.call_id) || x.call_id.Length > 80) return path + ".call_id 须为1～80字符的唯一标识。";
                    if (string.IsNullOrWhiteSpace(x.capability_id) || x.capability_id.Length > 160) return path + ".capability_id 须为目录中的能力ID，长度1～160。";
                    if ((x.body_id?.Length ?? 0) > 160 || (x.group_id?.Length ?? 0) > 80) return path + " 的 body_id/group_id 过长；分别最多160/80字符。";
                    if (x.arguments == null) continue;
                    if (x.arguments.Count > 16) return path + ".arguments 最多16项。";
                    for (var j = 0; j < x.arguments.Count; j++)
                    {
                        var argument = x.arguments[j]; var argumentPath = path + ".arguments[" + j + "]";
                        if (argument == null) return argumentPath + " 必须是参数对象，不能为 null。";
                        if (string.IsNullOrWhiteSpace(argument.name) || argument.name.Length > 80) return argumentPath + ".name 须为1～80字符的参数名。";
                        if ((argument.value?.Length ?? 0) > 12000) return argumentPath + ".value 超过12000字符。";
                    }
                    if (x.arguments.Select(a => a.name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != x.arguments.Count)
                        return path + ".arguments 的 name 不能重复（忽略大小写）。";
                }
            }
            else
            {
                if (value.step == "continue") return "$.step=continue 时 $.actions 必须至少包含一项有效行动；准备直接回应时改为 finish 并填写 reply。";
                if (value.step == "wait")
                {
                    if (needsReply) return "$.step=wait 不可用于尚未回应的用户消息；请用 finish+reply 回应，或 continue+actions 执行表达。";
                    if (!string.IsNullOrWhiteSpace(value.reply) || value.refine) return "$.step=wait 时 reply 须为空且 refine=false；需要发送正文请改为 finish。";
                }
                if (value.step == "finish" && (needsReply || value.refine) && string.IsNullOrWhiteSpace(value.reply))
                    return "$.reply 不能为空：尚未回应用户或 refine=true 的 finish 必须有正文；需要表达行动请用 continue+actions。";
            }
            if (value.attention_links != null)
            {
                if (value.attention_links.Count > 3) return "$.attention_links 最多3项。";
                for (var i = 0; i < value.attention_links.Count; i++)
                {
                    var x = value.attention_links[i]; var path = "$.attention_links[" + i + "]";
                    if (x == null) return path + " 必须是对象，不能为 null。";
                    if ((x.attention?.Length ?? 0) > 160) return path + ".attention 最多160字符。";
                    if ((x.cognition_ids?.Count ?? 0) > 6 || (x.cognition_ids != null && x.cognition_ids.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 80)))
                        return path + ".cognition_ids 最多6个非空ID，每个至多80字符。";
                }
            }
            if (!string.IsNullOrWhiteSpace(value.tool_call) || value.WantsLeave() || value.WantsMemory())
                return "旧心智工具字段不能驱动 Agent；请仅通过 actions 请求能力。";
            return null;
        }

        internal static string BuildStable(TraceTurnContext turn)
        {
            var builder = new StringBuilder(AgentLoopPrompts.Rules);
            builder.AppendLine().AppendLine(AgentLoopPrompts.SubjectContinuity);
            builder.AppendLine().AppendLine(CorePrompts.Expressor.ExpressionPosture);
            // 旧 Mind/Expressor 的协议扩展不进入 Agent 根契约；插件通过能力目录声明调用格式。
            builder.AppendLine().AppendLine(AgentLoopPrompts.ExpressionChoice);
            builder.AppendLine().AppendLine(AgentOutputContractLogic.Prompt);
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
