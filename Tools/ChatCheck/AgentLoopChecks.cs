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
        EnvironmentLogic.SaveSettings(store, new EnvironmentSettings { OwnerAccounts = new()
            { new OwnerAccountBindingData { PlatformId = "check.agent", UserId = "owner" } } });
        var observed = new EnvironmentObservationData { PlatformId = "check.agent", SessionId = "owner", SessionType = "private",
            SpeakerId = "owner", ExclusivePair = true, DirectAddress = true };
        var services = new TracePluginServices(store, new HierarchicalVectorRouterLogic(new FakeEncoder()));
        using var manager = new TracePluginManager(store, services);
        manager.RegisterExternal(new DialogueTracePlugin());
        manager.RegisterExternal(new InnerLifePlugin());
        manager.RegisterExternal(new TimeSchedulerPlugin());
        var probe = new AgentProbePlugin();
        manager.RegisterExternal(probe);
        var expressionStarts = new List<string>();
        services.ExpressionStartingHooks.Add(turn => { expressionStarts.Add(turn.ConversationId); return Task.CompletedTask; });
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
            var result = conversation == "agent-silent"
                ? await kernel.ProcessPluginEventAsync(conversation, new PluginEventData { PluginId = "check.agent", Environment = observed, Role = "system_event",
                    Content = "后台观察", IsOperational = true, Wake = KernelWakeValues.Mind, OccurredUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() })
                : conversation == "agent-voice" || conversation.StartsWith("agent-media", StringComparison.Ordinal)
                ? await kernel.ProcessPluginEventAsync(conversation, new PluginEventData
                {
                    PluginId = "check.agent", Environment = observed, Role = "小雨", Content = "请用声音回应", Organ = "voice",
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

        services.HeartbeatMinMinutes = services.HeartbeatMaxMinutes = 10;
        var staged = Continue(Action("state-before-action"));
        staged.inner = "这一轮决定留下的心情";
        staged.next_heartbeat_minutes = 90;
        staged.next_heartbeat_plan = "稍后再来分享";
        var inherited = await Chat("agent-state-carry", Step(staged),
            "{\"step\":\"finish\",\"reply\":\"看好了。\"}");
        Require(store.LoadOrCreateInnerRuntime("agent-state-carry").Narrative == staged.inner,
            "行动前的内心状态必须由代码传到最终提交，收尾省略不能丢失");
        Require(HeartbeatLogic.NextPlan(store, "agent-state-carry") == staged.next_heartbeat_plan,
            "行动前的联系计划必须由代码传到最终提交");
        Require(HeartbeatLogic.NextDueUnixMs(store, "agent-state-carry").HasValue &&
                Math.Abs((HeartbeatLogic.NextDueUnixMs(store, "agent-state-carry").Value - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) / 60000.0 - 90) < .1,
            "行动前的90分钟安排必须由代码传到最终提交");
        probe.FailText = true;
        var failedSend = new AgentSequenceLlm("{\"step\":\"finish\",\"reply\":\"测试未发出的正文\",\"inner\":\"发送前已形成的状态\",\"next_heartbeat_minutes\":90,\"next_heartbeat_plan\":\"下一次重新判断\"}");
        var sendThrew = false;
        try
        {
            await new KernelLogic(store, failedSend, manager).ProcessPluginEventAsync("agent-send-failure", new PluginEventData
            {
                PluginId = "check.agent", Environment = observed, Role = "小雨", Content = "现在怎么样",
                OccurredUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
        }
        catch (InvalidOperationException) { sendThrew = true; }
        Require(sendThrew && HeartbeatLogic.NextPlan(store, "agent-send-failure") == "下一次重新判断" &&
                store.LoadOrCreateInnerRuntime("agent-send-failure").Narrative == "发送前已形成的状态" &&
                store.GetRecentDialogueMoments("agent-send-failure", 10).All(x => x.Content != "测试未发出的正文"),
            "发送失败也须保留已形成的状态和后续唤醒，不能把未发出的正文记为成功");
        probe.FailText = false;
        services.HeartbeatMinMinutes = services.HeartbeatMaxMinutes = 0;
        probe.Count = 0;

        var silentStep = new AgentStepData { step = "wait", inner = "这一刻安静听着。", next_heartbeat_minutes = 15,
            next_heartbeat_plan = "稍后看看是否有想分享的事" };
        var silent = await Chat("agent-silent", Step(silentStep));
        Require(silent.llm.Requests.Count == 1 && silent.llm.TextRequests == 0 &&
            store.GetRecentMoments("agent-silent", 10).Count == 0 && !expressionStarts.Contains("agent-silent"),
            "后台唤醒可以保持安静：一次生成，不发送假回复，也不触发开始输入");
        Require(store.LoadOrCreateInnerRuntime("agent-silent").Narrative == silentStep.inner,
            "选择安静仍须保存本轮内心变化");
        Require(!AgentLoopLogic.Valid(new AgentStepData(), true, false) &&
            !AgentLoopLogic.Valid(new AgentStepData(), false, false) &&
            !AgentLoopLogic.Valid(Finish(""), true, false) &&
            !AgentLoopLogic.Valid(new AgentStepData { step = "wait", reply = "还在说话" }, true, false) &&
            !AgentLoopLogic.Valid(new AgentStepData { step = "wait", refine = true }, true, false),
            "缺失步骤、空回复和矛盾的 wait 不能冒充有效的沉默决定");
        var corrected = await Chat("agent-silence-repair", "{}", Step(Finish()));
        Require(corrected.llm.Requests.Count == 2 && expressionStarts.Contains("agent-silence-repair"),
            "无效模型输出必须经过纠正，不能悄悄当成不回应");
        var required = await Chat("agent-user-wait", Step(new AgentStepData { step = "wait" }), Step(Finish()));
        Require(required.llm.Requests.Count == 2 && store.GetRecentMoments("agent-user-wait", 10).Count == 2,
            "未回应的用户入站不能选择 wait，须纠正为实际回应");

        var mediaStart = probe.Media;
        var photo = await Chat("agent-media-photo", Step(Continue(Action("photo", "check.agent.image"))), Step(Finish("")));
        var sticker = await Chat("agent-media-sticker", Step(Finish("真开心。")));
        Require(!sticker.llm.Requests[0].Contains("check.agent.sticker") &&
            !sticker.llm.Requests[0].Contains("照片、表情") && sticker.llm.Requests.Count == 1,
            "表情能力和选择说明不进入 Agent prompt，自动匹配不增加生成轮次");
        Require(probe.Media == mediaStart + 2 && photo.llm.Requests.Count == 2 && sticker.llm.Requests.Count == 1 &&
            store.GetRecentMoments("agent-media-photo", 10).Count == 1 &&
            store.GetRecentMoments("agent-media-sticker", 10).Count == 2 &&
            store.GetRecentOperationalEvents("agent-media-photo", 10).Any(x => x.Kind == OperationalEventKindValues.OutboundImage) &&
            store.GetRecentOperationalEvents("agent-media-sticker", 10).Any(x => x.Kind == OperationalEventKindValues.OutboundSticker),
            "照片仍可独立回应，文字自动附表情并保存媒体回执，不伪造聊天正文");
        Require(photo.result.MindDecision.speak && !silent.result.MindDecision.speak,
            "无文字的媒体表达与全程安静须在后续状态判断中区分");
        var bodyGesture = await Chat("agent-media-gesture", Step(Continue(Action("gesture", "check.agent.gesture"))), Step(Finish("")));
        Require(bodyGesture.llm.Requests.Count == 2 && store.GetRecentMoments("agent-media-gesture", 10).Count == 1,
            "真人发言也可只用身体动作回应");
        probe.Gestures = 0;
        mediaStart = probe.Media;
        var combined = await Chat("agent-media-combined", Step(Continue(Action("photo", "check.agent.image"))), Step(Finish("给你看看。")));
        Require(probe.Media == mediaStart + 2 && combined.llm.Requests.Count == 2,
            "照片后继续文字只增加一次自动表情，不重复发照片");
        probe.Fail = true;
        var failedPhoto = await Chat("agent-media-failure", Step(Continue(Action("photo", "check.agent.image"))),
            Step(Finish("")), Step(Finish("图片没发成功。")));
        probe.Fail = false;
        Require(failedPhoto.llm.Requests.Count == 3 && failedPhoto.llm.Requests[1].Contains("failed") &&
            store.GetRecentOperationalEvents("agent-media-failure", 10).All(x => x.Kind != OperationalEventKindValues.OutboundImage),
            "图片失败不能充当已回应证据；空 finish 需纠正为实际回应");

        var choicesTurn = new TraceTurnContext("agent-choices", Moment("agent-choices", "发张照片给我看看"),
            new List<MomentRecord>(), 0, true, services);
        var choices = manager.GetAvailableActionCatalog(choicesTurn);
        Require(AgentLoopLogic.BuildCatalog(choices).All(x => MouthLogic.OrganOf(x) != BodyOrganValues.Sticker),
            "自动表情不能成为模型可执行的动作");
        var oldImageFlag = Finish("我这会儿不想拍。"); oldImageFlag.image = "有";
        var autoMapped = ExpressorLogic.PrepareDirectReply(oldImageFlag.reply, choicesTurn, choices, oldImageFlag);
        Require(autoMapped.expressions.Count == 1 && autoMapped.expressions[0].capability_id == "check.agent.sticker",
            "正文只自动匹配表情，不能因索图关键词或旧 image 字段擅自补图");
        Require(ExpressorLogic.PrepareDirectReply("", choicesTurn, choices, Finish("")).expressions.Count == 0 &&
            ExpressorLogic.PrepareDirectReply("嗯。", choicesTurn,
                choices.Where(x => MouthLogic.OrganOf(x) != BodyOrganValues.Sticker), Finish()).expressions.Count == 0,
            "没有文字或没有表情器官时不自动发表情");
        var refineOnly = new AgentSequenceLlm("{\"reply\":\"给你看看。\",\"image\":\"多余照片\",\"sticker\":\"开心\"}");
        var refinedOnly = await new ExpressorLogic(refineOnly).ExpressAsync(choicesTurn, manager.GetPlugins(), choices,
            Array.Empty<TraceContextBlockData>(), oldImageFlag, "", false, null, CancellationToken.None);
        Require(refinedOnly.expressions.Count == 1 && refinedOnly.expressions[0].capability_id == "check.agent.sticker" &&
            refinedOnly.expressions[0].GetArgument("emotion") != "开心" &&
            !refineOnly.Requests[0].Contains("check.agent.sticker"),
            "润色正文沿用一次自动匹配，忽略模型表情和图片字段，能力不注入 prompt");
        var rejectedSticker = await Chat("agent-media-manual-sticker",
            Step(Continue(Action("manual", "check.agent.sticker"))), Step(Finish("我在。")));
        Require(rejectedSticker.llm.Requests[1].Contains("能力不在当前可用目录") &&
            store.GetRecentOperationalEvents("agent-media-manual-sticker", 10)
                .Count(x => x.Kind == OperationalEventKindValues.OutboundSticker) == 1,
            "幻觉出的表情动作必须拒绝，最后文字只触发一次自动匹配");

        var lookup = await Chat("agent-tool", Step(Continue(Action("read-1"))), Step(Finish("查到了：答案42。")));
        Require(lookup.llm.Requests.Count == 2 && probe.Count == 1, "一次查资料后应由同一 Agent 继续，不能追加固定表达调用");
        Require(lookup.llm.Requests[1].Contains("答案42") &&
            !lookup.llm.Requests[1].Contains("\"sticker\"") &&
            !lookup.llm.Requests[1].Contains("\"speak\"") &&
            store.GetRecentMoments("agent-tool", 10).All(x => x.Content != "这句只是中间草稿，不能发送"),
            "实际结果进入续推上下文，中间草稿不发送，内部遗留字段不回流到 prompt");

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
        Require(probe.Count == before + 2 && !collision.llm.Requests.Last().Contains("同一 call_id 不可改成"),
            "旧响应的相同编号被忽略，不同参数各自获得程序调用身份");

        var mismatch = Action("wrong-body"); mismatch.body_id = "other-robot";
        before = probe.Count;
        var denied = await Chat("agent-denied", Step(Continue(Action("unknown", "does.not.exist"), mismatch)), Step(Finish()));
        Require(probe.Count == before + 1 && denied.llm.Requests.Last().Contains("不在当前可用目录") &&
            !denied.llm.Requests.Last().Contains("other-robot"), "未知能力不执行；旧身体字段被忽略，已知能力使用目录绑定");

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
            PluginId = "check.agent", Environment = observed, Role = "system_event", Content = "有人走近，可以挥手", IsOperational = true,
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
        services.HeartbeatMinMinutes = services.HeartbeatMaxMinutes = 10;
        var photoState = Continue(Action("state-photo", "check.agent.image"));
        photoState.inner = "想把这一刻分享出去";
        photoState.next_heartbeat_minutes = 90;
        photoState.next_heartbeat_plan = "稍后来看看自己的心情";
        var photoOnly = await Chat("agent-media-state-carry", Step(photoState), "{\"step\":\"wait\"}");
        Require(photoOnly.llm.Requests.Count == 2 &&
                store.GetRecentOperationalEvents("agent-media-state-carry", 20).Count(x => x.Kind == OperationalEventKindValues.OutboundImage) == 1 &&
                store.GetRecentDialogueMoments("agent-media-state-carry", 20).Count == 1 &&
                store.LoadOrCreateInnerRuntime("agent-media-state-carry").Narrative == photoState.inner &&
                HeartbeatLogic.NextPlan(store, "agent-media-state-carry") == photoState.next_heartbeat_plan,
            "只发照片后直接wait也须提交行动前的内心和下次联系，不补文字、不重复照片");
        services.HeartbeatMinMinutes = services.HeartbeatMaxMinutes = 0;
        await manager.ExecuteAsync(Action("plugin-stop", "check.agent.voice"), turn, CancellationToken.None);
        var pluginToken = probe.VoiceToken;
        manager.SetEnabled("check.agent", false);
        Require(pluginToken.IsCancellationRequested && manager.GetAvailableActionCatalog(turn).All(x => x.PluginId != "check.agent"),
            "禁用插件必须先请求停止其持续动作，并移除行动目录");
        Console.WriteLine("Agent loop checks passed: one-call reply/silence, independent media/voice/gesture, explicit output choice, continuation, recall, dedupe, refinement, failures, receipts, cancellation and bounds.");
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
        public int Count, Gestures, Media;
        public bool Fail, FailText;
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
            context.AddCallable(new Capability(this, "check.agent.image", "image", true));
            context.AddCallable(new Capability(this, "check.agent.sticker", "sticker", true));
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
                if (Descriptor.Organ == "text" && owner.FailText) throw new InvalidOperationException("模拟平台发送失败");
                if (Descriptor.Organ == "voice")
                {
                    owner.VoiceToken = cancellationToken;
                    return Task.FromResult(new TraceCapabilityResultData { Status = "running", Summary = "开始播放，尚未完成。" });
                }
                if (Descriptor.Organ == "gesture") owner.Gestures++;
                else if (Descriptor.Organ == "image" || Descriptor.Organ == "sticker") owner.Media++;
                else if (Descriptor.Organ != "text") owner.Count++;
                if (owner.Fail && Descriptor.Organ != "text") throw new InvalidOperationException("模拟执行失败");
                if (Descriptor.Organ == "text")
                    return Task.FromResult(new TraceCapabilityResultData { Status = "success", Summary = "正文已发送",
                        ProducedEvent = new PluginEventData { PluginId = "check.agent", Role = "小光",
                            Content = call.GetArgument("text"), OccurredUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() } });
                if (Descriptor.Organ == "image" || Descriptor.Organ == "sticker")
                    return Task.FromResult(new TraceCapabilityResultData { Status = "success", Summary = "媒体已发送",
                        ProducedEvent = new PluginEventData { PluginId = "check.agent", Role = "system_event",
                            IsOperational = true, Content = Descriptor.Organ == "image" ? "发送图片完成" : "发送表情完成",
                            OccurredUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() } });
                return Task.FromResult(new TraceCapabilityResultData { Status = "success", Summary = "完成", Payload = "答案42" });
            }
        }
    }
}
