using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Logic;
using TraceSoul2.Manager;
using TraceSoul2.Plugins;
using TraceSoul2.Plugins.Builtin;

internal static partial class Program
{
    private static async Task RunAgentLoopChecksAsync()
    {
        using var store = new SqliteMemoryManager(":memory:");
        store.SavePairIdentity("小雨", "小光", "雨雨");
        var services = new TracePluginServices(store, new HierarchicalVectorRouterLogic(new FakeEncoder()));
        using var manager = new TracePluginManager(store, services);
        manager.RegisterExternal(new DialogueTracePlugin());
        manager.RegisterExternal(new InnerLifePlugin());
        manager.RegisterExternal(new TimeSchedulerPlugin());
        var probe = new AgentProbePlugin();
        manager.RegisterExternal(probe);
        var json = new JsonSerializerOptions { IncludeFields = true };
        string Step(AgentStepData value) => JsonSerializer.Serialize(value, json);
        BrainCapabilityCallData Action(string id, string capability = "check.agent.read", string value = "资料") => new BrainCapabilityCallData
        {
            call_id = id, capability_id = capability,
            arguments = new List<BrainCallArgumentData> { new BrainCallArgumentData { name = "query", value = value } }
        };
        AgentStepData Continue(params BrainCapabilityCallData[] calls) => new AgentStepData
            { step = "continue", reply = "这句只是中间草稿，不能发送", actions = calls.ToList() };
        AgentStepData Finish(string reply = "知道啦。") => new AgentStepData { step = "finish", reply = reply };
        async Task<(ChatTurnResultData result, AgentSequenceLlm llm)> Chat(string conversation, params string[] responses)
        {
            var llm = new AgentSequenceLlm(responses);
            var kernel = new KernelLogic(store, llm, manager);
            var result = conversation == "agent-voice"
                ? await kernel.ProcessPluginEventAsync(conversation, new PluginEventData
                {
                    PluginId = "check.agent", Role = "小雨", Content = "请用声音回应", Organ = "voice",
                    OccurredUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                }, historyWindowMax: 8)
                : await kernel.ChatAsync(conversation, "帮我看看资料", historyWindowMax: 8);
            return (result, llm);
        }

        var direct = Finish("我在这里。");
        direct.inner = "内部状态不能外发";
        var normal = await Chat("agent-direct", Step(direct));
        Require(normal.llm.Requests.Count == 1 && normal.llm.TextRequests == 0, "普通对话只能调用一次生成");
        Require(store.GetRecentMoments("agent-direct", 10).Count(x => x.Content == "我在这里。") == 1 &&
            store.GetRecentMoments("agent-direct", 10).All(x => x.Content != direct.inner), "只发送最终正文，内部状态不能成为聊天");
        Require(store.LoadOrCreateInnerRuntime("agent-direct").Narrative == direct.inner, "同次生成的内心变化仍须落库");
        Require(!normal.llm.Requests[0].Contains("话留到开口") && normal.llm.Requests[0].Contains("【Agent 当下】"),
            "新循环不应被旧的禁说话提示覆盖");

        var lookup = await Chat("agent-tool", Step(Continue(Action("read-1"))), Step(Finish("查到了：答案42。")));
        Require(lookup.llm.Requests.Count == 2 && probe.Count == 1, "一次查资料后应由同一 Agent 继续，不能追加固定表达调用");
        Require(lookup.llm.Requests[1].Contains("答案42") &&
            store.GetRecentMoments("agent-tool", 10).All(x => x.Content != "这句只是中间草稿，不能发送"),
            "实际结果必须进入续推上下文，中间草稿不发送");

        var before = probe.Count;
        var sameArguments = Action("same-2");
        sameArguments.body_id = "check.agent";
        sameArguments.arguments[0].name = "QUERY";
        var duplicate = await Chat("agent-dedupe", Step(Continue(Action("same-1"))),
            Step(Continue(sameArguments)), Step(Finish()));
        Require(probe.Count == before + 1 && duplicate.llm.Requests.Last().Contains("不重复产生副作用"),
            "换 call_id 重复相同动作也不能重复执行");
        before = probe.Count;
        var collision = await Chat("agent-collision", Step(Continue(Action("id"))),
            Step(Continue(Action("id", value: "另一份资料"))), Step(Finish()));
        Require(probe.Count == before + 1 && collision.llm.Requests.Last().Contains("同一 call_id"),
            "不能用同一 call_id 偷换请求");

        var mismatch = Action("wrong-body"); mismatch.body_id = "other-robot";
        before = probe.Count;
        var denied = await Chat("agent-denied", Step(Continue(Action("unknown", "does.not.exist"), mismatch)), Step(Finish()));
        Require(probe.Count == before && denied.llm.Requests.Last().Contains("目标身体") &&
            denied.llm.Requests.Last().Contains("不在当前可用目录"), "未知能力与错误目标不能执行，失败必须返回 Agent");

        var recall = await Chat("agent-recall", Step(Continue(Action("recall", "memory.recall", "约定日期"))), Step(Finish("没有找到日期。")));
        Require(recall.llm.Requests.Count == 2 && recall.llm.Requests.Last().Contains("未找到相关记忆"),
            "记忆为空也必须有明确结果，不能假装找到或强制表达额外调用");

        probe.Fail = true;
        var failed = await Chat("agent-failure", Step(Continue(Action("fail"))), Step(Finish("没查成。")));
        Require(failed.llm.Requests.Last().Contains("failed") && failed.llm.Requests.Last().Contains("模拟执行失败"),
            "执行失败必须回到循环，不能报告成功");
        probe.Fail = false;

        var polish = Finish("原始草稿"); polish.refine = true;
        var polished = await Chat("agent-polish", Step(polish), "润色后的正文。");
        Require(polished.llm.Requests.Count == 2 && polished.llm.TextRequests == 1 &&
            polished.llm.Requests.Last().Contains("原始草稿"), "明确需要加工时才调用表达，且必须带入草稿");

        probe.TextAvailable = false;
        var voice = await Chat("agent-voice", Step(Continue(Action("voice", "check.agent.voice", "欢迎回来"))), Step(Finish("")));
        probe.TextAvailable = true;
        Require(voice.llm.Requests.Count == 2 && store.GetRecentMoments("agent-voice", 10).Count == 1,
            "只有声音的身体受理后也可以等待，不得伪造已说完的文字或补发一条文字");
        var ongoing = services.Executions.List("agent-voice").Single(x => x.CapabilityId == "check.agent.voice");
        Require(ongoing.Status == "running" && string.IsNullOrEmpty(ongoing.ConfirmedContent), "受理不代表播放完成");
        Require(services.Executions.Report(new TraceExecutionReceiptData { ExecutionId = ongoing.ExecutionId,
            Sequence = 1, Status = "running", ProgressMs = 120, ConfirmedContent = "欢迎" }), "设备应可确认已播放前缀");
        Require(!services.Executions.Report(new TraceExecutionReceiptData { ExecutionId = ongoing.ExecutionId,
            Sequence = 0, Status = "completed", ProgressMs = 500 }), "迟到回执不能覆盖新进度");
        Require(!services.Executions.Cancel(ongoing.ExecutionId, "wrong-session"), "不能跨会话取消动作");
        Require(services.Executions.Cancel(ongoing.ExecutionId, "agent-voice") && probe.VoiceToken.IsCancellationRequested,
            "取消必须传到执行器 token");
        Require(services.Executions.List("agent-voice").Single(x => x.ExecutionId == ongoing.ExecutionId).Status == "cancel_requested",
            "请求停止不能冒充已停止");
        Require(services.Executions.Report(new TraceExecutionReceiptData { ExecutionId = ongoing.ExecutionId,
            Sequence = 2, Status = "cancelled", ProgressMs = 120, ConfirmedContent = "欢迎" }), "驱动确认后才能进入取消终态");
        Require(!services.Executions.Report(new TraceExecutionReceiptData { ExecutionId = ongoing.ExecutionId,
            Sequence = 3, Status = "completed", ConfirmedContent = "欢迎回来" }), "终态不能被旧播放完成回执复活");
        var events = manager.PollBackgroundServices(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            .Where(x => x.PluginId == "runtime.execution").ToList();
        Require(events.Count == 1 && events[0].IsOperational && events[0].ConversationId == "agent-voice" &&
            manager.PollBackgroundServices(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).All(x => x.PluginId != "runtime.execution"),
            "持续执行终态须产生一次有归属的运行事件，不直接编入真实聊天");

        var wave = Action("gesture", "check.agent.gesture"); wave.group_id = "greeting";
        var look = Action("look", "check.agent.look"); look.group_id = "greeting";
        var quietLlm = new AgentSequenceLlm(Step(Continue(wave, look)), Step(new AgentStepData { step = "wait" }));
        await new KernelLogic(store, quietLlm, manager).ProcessPluginEventAsync("agent-gesture", new PluginEventData
        {
            PluginId = "check.agent", Role = "system_event", Content = "有人走近，可以挥手", IsOperational = true,
            Wake = KernelWakeValues.Mind, OccurredUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        }, cancellationToken: CancellationToken.None);
        Require(quietLlm.Requests.Count == 2 && probe.Gestures == 2 && store.GetRecentMoments("agent-gesture", 10).Count == 0,
            "同类独立动作都应可执行，不依附文字，也不能伪造一轮聊天");
        Require(quietLlm.Messages.All(x => x.All(m => m.content != "有人走近，可以挥手")) &&
            quietLlm.Requests[0].Contains("当前运行事件，不是对方发言"), "后台事件不能伪装成对方的 user 消息");

        var quiet = new AgentSequenceLlm(Step(new AgentStepData { step = "wait" }));
        var turn = new TraceTurnContext("agent-budget", Moment("agent-budget", "看看资料"), new List<MomentRecord>(), 0, false, services);
        await new AgentLoopLogic(quiet).RunAsync(turn, "", () => manager.GetAvailableCatalog(turn),
            (_, _) => throw new Exception("等待不得执行工具"), CancellationToken.None);
        Require(quiet.Requests.Count == 1, "普通等待不应调用表达");

        var budgetResponses = Enumerable.Range(0, AgentLoopLogic.MaxActionRounds).Select(i => Step(Continue(Action("budget-" + i, value: "资料" + i))))
            .Append(Step(Finish("先到这里。"))).ToArray();
        var budget = new AgentSequenceLlm(budgetResponses);
        var calls = 0;
        await new AgentLoopLogic(budget).RunAsync(turn, "", () => manager.GetAvailableCatalog(turn),
            (call, _) => { calls++; return Task.FromResult(new TraceCapabilityResultData { Status = "success", Summary = "ok" }); }, CancellationToken.None);
        Require(calls == AgentLoopLogic.MaxActionRounds && budget.Requests.Last().Contains("预算已用完"), "循环必须有明确行动上限和收口步骤");
        Require(!AgentLoopLogic.Valid(Continue(Action("overflow")), false, true), "最后一步不得继续执行动作");

        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var cancelledLlm = new AgentSequenceLlm(Step(Finish()));
        try
        {
            await new AgentLoopLogic(cancelledLlm).RunAsync(turn, "", () => manager.GetAvailableCatalog(turn),
                (_, _) => throw new Exception("不应执行"), cancelled.Token);
            throw new Exception("取消应向上传播");
        }
        catch (OperationCanceledException) { Require(cancelledLlm.Requests.Count == 0, "取消不能启动模型请求"); }
        await manager.ExecuteAsync(Action("plugin-stop", "check.agent.voice"), turn, CancellationToken.None);
        var pluginToken = probe.VoiceToken;
        manager.SetEnabled("check.agent", false);
        Require(pluginToken.IsCancellationRequested && manager.GetAvailableActionCatalog(turn).All(x => x.PluginId != "check.agent"),
            "禁用插件必须先请求停止其持续动作，并移除行动目录");
        Console.WriteLine("Agent loop checks passed: one-call reply, continuation, recall, actions, dedupe, optional refinement, independent voice/gesture, receipts, cancellation and bounds.");
    }

    private sealed class AgentSequenceLlm : ILlmClient
    {
        private readonly Queue<string> responses;
        public AgentSequenceLlm(params string[] responses) { this.responses = new Queue<string>(responses); }
        public string ProviderId => "agent-check";
        public string Model => "agent-check";
        public List<string> Requests { get; } = new List<string>();
        public List<List<DeepSeekMessageData>> Messages { get; } = new List<List<DeepSeekMessageData>>();
        public int TextRequests;
        public Task<string> CompleteJsonAsync(List<DeepSeekMessageData> messages, CancellationToken cancellationToken = default, string promptCacheKey = null)
        {
            Requests.Add(string.Join("\n", messages.Select(x => x.content)));
            Messages.Add(messages.ToList());
            if (responses.Count == 0) throw new InvalidOperationException("测试收到非预期模型调用。");
            return Task.FromResult(responses.Dequeue());
        }
        public Task<string> CompleteTextAsync(List<DeepSeekMessageData> messages, CancellationToken cancellationToken = default, string promptCacheKey = null)
        { TextRequests++; return CompleteJsonAsync(messages, cancellationToken, promptCacheKey); }
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>(new[] { Model });
    }

    private sealed class AgentProbePlugin : ITracePlugin
    {
        public int Count, Gestures;
        public bool Fail;
        public bool TextAvailable = true;
        public CancellationToken VoiceToken;
        public TracePluginMetadataData Metadata { get; } = new TracePluginMetadataData
            { Id = "check.agent", DisplayName = "Agent 离线检查", Version = "1.0.0" };
        public void Register(TracePluginContext context)
        {
            context.Services.Platforms.Register(new PlatformHandle { Id = "check.agent", IsConnected = () => true });
            context.AddCallable(new Capability(this, "check.agent.read", "", false));
            context.AddCallable(new Capability(this, "check.agent.text", "text", true));
            context.AddCallable(new Capability(this, "check.agent.voice", "voice", true));
            context.AddCallable(new Capability(this, "check.agent.gesture", "gesture", true));
            context.AddCallable(new Capability(this, "check.agent.look", "gesture", true));
        }
        public void Shutdown() { }
        private sealed class Capability : ITraceCallableContribution
        {
            private readonly AgentProbePlugin owner;
            public TraceContributionDescriptorData Descriptor { get; }
            public Capability(AgentProbePlugin owner, string id, string organ, bool effector)
            {
                this.owner = owner;
                Descriptor = new TraceContributionDescriptorData { Id = id, DisplayName = id,
                    Kind = effector ? TraceContributionKindValues.Effector : TraceContributionKindValues.CallableNerve,
                    BodyId = "check.agent", BodyTier = BodyTierValues.Chat, Organ = organ,
                    Description = "离线模拟能力", ParametersJsonSchema = "{query:string}", HasExternalSideEffect = effector };
            }
            public bool IsAvailable(TraceTurnContext context) => Descriptor.Organ != "text" || owner.TextAvailable;
            public Task<TraceCapabilityResultData> ExecuteAsync(BrainCapabilityCallData call, TraceTurnContext context, CancellationToken cancellationToken)
            {
                if (Descriptor.Organ == "voice")
                {
                    owner.VoiceToken = cancellationToken;
                    return Task.FromResult(new TraceCapabilityResultData { Status = "running", Summary = "开始播放，尚未完成。" });
                }
                if (Descriptor.Organ == "gesture") owner.Gestures++; else owner.Count++;
                if (owner.Fail) throw new InvalidOperationException("模拟执行失败");
                return Task.FromResult(new TraceCapabilityResultData { Status = "success", Summary = "完成", Payload = "答案42" });
            }
        }
    }
}
