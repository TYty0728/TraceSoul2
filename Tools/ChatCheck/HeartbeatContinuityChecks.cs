using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Logic;
using TraceSoul2.Manager;
using TraceSoul2.Plugins;
using TraceSoul2.Plugins.Builtin;
using TraceSoul2.Prompts;

internal static partial class Program
{
    private static async Task RunHeartbeatContinuityChecksAsync()
    {
        using var store = new SqliteMemoryManager(":memory:");
        store.SavePairIdentity("小雨", "小光", "雨雨");
        var services = new TracePluginServices(store, new HierarchicalVectorRouterLogic(new FakeEncoder()))
            { HeartbeatMinMinutes = 10, HeartbeatMaxMinutes = 10 };
        using var manager = new TracePluginManager(store, services);
        manager.RegisterExternal(new TimeSchedulerPlugin());
        var kernel = new KernelLogic(store, new QzoneDraftLlm(""), manager);
        var sync = typeof(KernelLogic).GetMethod("SyncHeartbeatAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        var heartbeat = new TraceTurnContext("heartbeat-check", Moment("heartbeat-check", HeartbeatLogic.BuildContent("稍后想来聊天")),
            new List<MomentRecord>(), 0, false, services);
        heartbeat.Moment.Role = "system_event";
        heartbeat.Moment.SourcePluginId = "builtin.time";
        var dialogue = new TraceTurnContext("heartbeat-check", Moment("heartbeat-check", "我先去忙"),
            new List<MomentRecord>(), 0, true, services);
        async Task Sync(TraceTurnContext turn, MindDecisionData decision)
            => await (Task)sync.Invoke(kernel, new object[] { turn, manager.GetAvailableCatalog(turn), decision, CancellationToken.None });
        foreach (var requested in new[] { 0, 90, 240, 480 })
        {
            var decision = new MindDecisionData { speak = false, sleep = false, next_heartbeat_minutes = requested, next_heartbeat_plan = "稍后想分享今天的心情" };
            var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await Sync(heartbeat, decision);
            var due = HeartbeatLogic.NextDueUnixMs(store, heartbeat.ConversationId);
            var expected = requested > 0 ? requested : 240;
            Require(due.HasValue && Math.Abs((due.Value - before) / 60000.0 - expected) < .1,
                "不睡且这次安静，仍应在指定时间或漏填兜底时间再次醒来");
            Require(HeartbeatLogic.NextPlan(store, heartbeat.ConversationId) == decision.next_heartbeat_plan && decision.next_heartbeat_minutes == expected,
                "持久化计划与决策快照应保留实际排下的时间和方向");
        }
        var planned = new MindDecisionData { next_heartbeat_minutes = 75, next_heartbeat_plan = "她忙完后再来分享" };
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await Sync(dialogue, planned);
        Require(Math.Abs((HeartbeatLogic.NextDueUnixMs(store, dialogue.ConversationId).Value - now) / 60000.0 - 75) < .1 &&
                HeartbeatLogic.NextPlan(store, dialogue.ConversationId) == planned.next_heartbeat_plan,
            "普通聊天中心智写下的时间不能被默认 10 分钟覆盖");
        await Sync(heartbeat, new MindDecisionData { sleep = true, next_heartbeat_minutes = 90 });
        Require(!HeartbeatLogic.NextDueUnixMs(store, heartbeat.ConversationId).HasValue, "明确睡下仍应清除心跳");
        services.HeartbeatMinMinutes = services.HeartbeatMaxMinutes = 0;
        await Sync(heartbeat, new MindDecisionData { next_heartbeat_minutes = 30 });
        Require(!HeartbeatLogic.NextDueUnixMs(store, heartbeat.ConversationId).HasValue, "用户关闭心跳后不能被模型重新打开");
        await RunAgentHeartbeatDeliveryCheckAsync();
        RunHeartbeatDispatchRecoveryCheck();
        await RunHeartbeatFreshnessCheckAsync();
        await RunHeartbeatEntryChecksAsync();
    }

    private static async Task RunHeartbeatFreshnessCheckAsync()
    {
        using var store = new SqliteMemoryManager(":memory:");
        const string root = "heartbeat-freshness";
        store.SavePairIdentity("小雨", "小光", "雨雨");
        var now = DateTimeOffset.Parse("2026-10-26T13:25:18+08:00");
        var at = now.AddMinutes(-28).ToUnixTimeMilliseconds();
        store.SaveMoment(new MomentRecord { Id = "earlier-user", ConversationId = root, Role = "小雨",
            Content = "准备结束散步回去了", CreatedUnixMs = at - 8000 });
        store.SaveMoment(new MomentRecord { Id = "earlier-reply", ConversationId = root, Role = "小光",
            Content = "路上慢慢走", CreatedUnixMs = at });
        var services = new TracePluginServices(store, new HierarchicalVectorRouterLogic(new FakeEncoder()))
            { HeartbeatMinMinutes = 10, HeartbeatMaxMinutes = 10 };
        var environment = new EnvironmentSnapshotData { RootConversationId = root, ContextConversationId = root,
            Visibility = "private", SpeakerIsOwner = true, AudienceConfirmed = true, SessionType = "private",
            PlatformId = "builtin.dialogue", SpeakerId = "local-owner" };
        var source = Moment(root, HeartbeatLogic.BuildContent("稍后看看散步是否结束"));
        source.Role = "system_event";
        source.SourcePluginId = "builtin.time";
        var heartbeat = new TraceTurnContext(root, source, store.GetRecentDialogueMoments(root, 20), 20, false,
            services, environment: environment);
        var state = RuntimeContextLogic.State(heartbeat, now);
        Require(state.Contains("距上次交流已过：28分钟") && state.Contains("不代表对方的活动持续了这么久") &&
            RuntimeContextLogic.State(heartbeat, now.ToOffset(TimeSpan.Zero)) == state,
            "心跳明确标注距离上次交流的时长，时区不改变间隔，也不推断活动仍在继续");
        Require(RuntimeContextLogic.State(heartbeat, now.AddMinutes(-28).AddSeconds(20)).Contains("不足1分钟"),
            "刚结束交流的短间隔如实呈现，不硬编码等待时长");
        var freshInput = new TraceTurnContext(root, Moment(root, "已经到了"), heartbeat.RecentMoments.ToList(), 20, true,
            services, environment: environment);
        Require(!RuntimeContextLogic.State(freshInput, now).Contains("距上次交流已过：") &&
            !RuntimeContextLogic.Trigger(freshInput).Contains("对方此刻没有新发言"),
            "新入站不被标成旧消息或无新发言");

        var llm = new AgentSequenceLlm("{\"step\":\"wait\"}");
        var result = await new AgentLoopLogic(llm).RunAsync(heartbeat, "", () => new(),
            (_, _) => throw new Exception("等待不得产生外部行动"), default);
        Require(result.step == "wait" && llm.Requests.Single().Contains("历史里已经回应过的话") &&
            llm.Requests.Single().Contains("到期不代表必须执行") &&
            llm.Requests.Single().Contains("不能认定对方至今仍在同一地点") &&
            llm.Requests.Single().Contains("不能擅自认定事情已经完成") &&
            llm.Requests.Single().Contains("可以自然询问或分享自己的新念头") &&
            llm.Requests.Single().Contains("稍后看看散步是否结束"),
            "生产 Agent 输入保留旧意向但要求重新判断，保留主动表达和等待的选择");
        Require(llm.Messages.Single().Any(x => x.role == "assistant" && x.content.Contains("路上慢慢走")) &&
            llm.Messages.Single().All(x => x.content != "准备结束散步回去了"),
            "已发送回复保留为历史证据，旧用户消息不重新成为无时间标记的当前输入");

        using var manager = new TracePluginManager(store, services);
        manager.RegisterExternal(new TimeSchedulerPlugin());
        var kernel = new KernelLogic(store, llm, manager);
        var sync = typeof(KernelLogic).GetMethod("SyncHeartbeatAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        await (Task)sync.Invoke(kernel, new object[] { heartbeat, manager.GetAvailableCatalog(heartbeat),
            new MindDecisionData { next_heartbeat_minutes = 60, next_heartbeat_plan = "旧的散步线索" }, CancellationToken.None });
        await (Task)sync.Invoke(kernel, new object[] { freshInput, manager.GetAvailableCatalog(freshInput),
            new MindDecisionData { speak = true }, CancellationToken.None });
        Require(HeartbeatLogic.NextPlan(store, root) == HeartbeatLogic.DefaultNextPlan &&
            Math.Abs((HeartbeatLogic.NextDueUnixMs(store, root).Value - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) / 60000.0 - 10) < .1,
            "新对话替换旧的60分钟计划，未指定新计划时使用当前默认唤醒范围：plan=" + HeartbeatLogic.NextPlan(store, root) +
            "，due=" + HeartbeatLogic.NextDueUnixMs(store, root) + "，range=" + services.HeartbeatMinMinutes + "/" + services.HeartbeatMaxMinutes +
            "，results=" + string.Join(";", freshInput.Workspace.Results.Select(x => x.Summary)));
        Console.WriteLine("Heartbeat freshness checks passed: elapsed time, historical replies, plan re-evaluation and replacement after new input.");
    }

    private static async Task RunAgentHeartbeatDeliveryCheckAsync()
    {
        using var store = new SqliteMemoryManager(":memory:");
        store.SavePairIdentity("小雨", "小光", "雨雨");
        var services = new TracePluginServices(store, new HierarchicalVectorRouterLogic(new FakeEncoder()))
            { HeartbeatMinMinutes = 10, HeartbeatMaxMinutes = 10 };
        using var manager = new TracePluginManager(store, services);
        manager.RegisterExternal(new DialogueTracePlugin());
        manager.RegisterExternal(new InnerLifePlugin());
        manager.RegisterExternal(new TimeSchedulerPlugin());
        var llm = new AgentSequenceLlm(
            "{\"step\":\"finish\",\"reply\":\"好，待会儿见。\",\"next_heartbeat_minutes\":10}",
            "{\"step\":\"finish\",\"reply\":\"好，那你先忙。\",\"refine\":true,\"next_heartbeat_minutes\":30}",
            "{\"step\":\"finish\",\"heartbeat_intent\":\"此刻想表达自己的想念\",\"reply\":\"刚刚有点想你。\",\"next_heartbeat_minutes\":90,\"next_heartbeat_plan\":\"稍后再感受有没有想分享的\"}",
            "{\"step\":\"wait\",\"next_heartbeat_minutes\":120,\"next_heartbeat_plan\":\"晚一点再看看自己的心情\"}",
            "{\"step\":\"wait\",\"sleep\":true}");
        var kernel = new KernelLogic(store, llm, manager);
        const string conversation = "agent-heartbeat-delivery";
        await kernel.ChatAsync(conversation, "我先忙一会儿");
        async Task Wake()
        {
            var due = HeartbeatLogic.NextDueUnixMs(store, conversation);
            Require(due.HasValue, "清醒时应保留真实时间任务");
            var source = manager.PollBackgroundServices(due.Value + 1)
                .Single(x => x.PluginId == "builtin.time" && x.ConversationId == conversation &&
                    HeartbeatLogic.IsHeartbeatContent(x.Content));
            await kernel.ProcessPluginEventAsync(conversation, source);
        }
        await Wake();
        Require(store.GetRecentDialogueMoments(conversation, 20).All(x => x.Content != "好，那你先忙。") &&
                llm.Requests.Count == 2 && llm.TextRequests == 0 &&
                Math.Abs((HeartbeatLogic.NextDueUnixMs(store, conversation).Value - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) / 60000.0 - 30) < .1,
            "没有联系意图的旧话接答不发送、不启动refine或额外审核，仍保留下次唤醒");
        await Wake();
        Require(store.GetRecentDialogueMoments(conversation, 20).Count(x => x.Content == "刚刚有点想你。") == 1,
            "没有新入站但本轮有独立联系意图的主动正文通过真实发送链只发一次");
        Require(llm.Requests[1].Contains(AgentLoopPrompts.Heartbeat) &&
                !llm.Requests[0].Contains(AgentLoopPrompts.Heartbeat) &&
                !llm.Messages[1].Any(m => m.role == "user" && HeartbeatLogic.IsHeartbeatContent(m.content)),
            "心跳专用语义须进入动态请求，不能伪装成用户发言或污染普通聊天");
        var dueAfterReply = HeartbeatLogic.NextDueUnixMs(store, conversation);
        Require(dueAfterReply.HasValue && Math.Abs((dueAfterReply.Value - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) / 60000.0 - 90) < .1,
            "主动发出正文后须继续安排模型指定的下一次唤醒");
        var count = store.GetRecentDialogueMoments(conversation, 20).Count;
        await Wake();
        Require(store.GetRecentDialogueMoments(conversation, 20).Count == count &&
                !store.LoadOrCreateInnerRuntime(conversation).Asleep &&
                Math.Abs((HeartbeatLogic.NextDueUnixMs(store, conversation).Value - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) / 60000.0 - 120) < .1,
            "主动联系后仍未收到回复也能再醒来；本次 wait 不外发、不睡下且保留120分钟安排");
        await Wake();
        Require(!HeartbeatLogic.NextDueUnixMs(store, conversation).HasValue &&
                store.LoadOrCreateInnerRuntime(conversation).Asleep && llm.Requests.Count == 5,
            "确实选择睡下才停止心跳，且不为主动联系增加额外模型调用");
    }

    private static async Task RunHeartbeatEntryChecksAsync()
    {
        using var store = new SqliteMemoryManager(":memory:");
        const string root = "heartbeat-entry";
        store.SavePairIdentity("小雨", "小光", "雨雨");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        store.SaveMoment(new MomentRecord { Id = "busy-user", ConversationId = root, Role = "小雨", Content = "我开始工作了", CreatedUnixMs = now - 780000 });
        store.SaveMoment(new MomentRecord { Id = "busy-reply", ConversationId = root, Role = "小光", Content = "好的，忙完再聊", CreatedUnixMs = now - 770000 });
        var services = new TracePluginServices(store, new HierarchicalVectorRouterLogic(new FakeEncoder()));
        var environment = new EnvironmentSnapshotData { RootConversationId = root, ContextConversationId = root,
            PlatformId = "builtin.dialogue", SpeakerId = "local-owner", Visibility = "private", AudienceConfirmed = true };
        TraceTurnContext Turn(bool human = false)
        {
            var moment = Moment(root, HeartbeatLogic.BuildContent("旧意向：陪伴工作，到时重新考虑"));
            moment.Role = human ? "小雨" : "system_event";
            moment.SourcePluginId = human ? "builtin.dialogue" : "builtin.time";
            return new(root, moment, store.GetRecentDialogueMoments(root, 20), 20, human, services, environment: environment);
        }
        var turn = Turn();
        var catalog = new List<TraceContributionDescriptorData>();
        foreach (var organ in new[] { BodyOrganValues.Text, BodyOrganValues.Image, BodyOrganValues.Voice, BodyOrganValues.Qzone })
            catalog.Add(new() { Id = "entry." + organ, Kind = TraceContributionKindValues.Effector, Organ = organ,
                BodyId = BodyIds.Console, HasExternalSideEffect = true });
        services.AvailableCatalogProvider = _ => catalog;
        var calls = new List<string>();
        Task<TraceCapabilityResultData> Execute(BrainCapabilityCallData call, CancellationToken token)
        {
            calls.Add(call.capability_id);
            return Task.FromResult(new TraceCapabilityResultData { Status = "success", Summary = "离线完成" });
        }
        Task<AgentStepData> Run(AgentSequenceLlm llm, TraceTurnContext input = null) => new AgentLoopLogic(llm)
            .RunAsync(input ?? turn, "", () => catalog, Execute, default);
        string Action(string id, string extra = "") => "{\"step\":\"continue\"," + extra +
            "\"actions\":[{\"capability_id\":\"" + id + "\",\"arguments\":[{\"name\":\"query\",\"value\":\"旧约定\"}]}]}";

        var quietModel = new AgentSequenceLlm("{\"step\":\"wait\"}");
        Require((await Run(quietModel)).step == "wait" && quietModel.Requests.Count == 1 && calls.Count == 0,
            "主动醒来无需形成联系意图也能一次决定安静，不增加审批模型");
        var request = quietModel.Requests.Single();
        Require(request.Contains("【本轮：主动醒来】") && request.Contains("没有新消息等待回复") &&
            request.Contains("最近一条可见交流是我已经发出的表达") && request.Contains("距上次交流已过") &&
            !request.Contains("尚未受理") && !request.Contains("本轮新消息尚未回应"),
            "主动入口有历史回复与间隔证据，不携带普通对话欠答状态");
        Require(quietModel.Messages.Single().Any(x => x.role == "assistant" && x.content.Contains("好的，忙完再聊")) &&
            quietModel.Messages.Single().All(x => x.content != "我开始工作了"), "保留带时间的完整历史，旧用户消息不重放为当前消息");

        foreach (var item in catalog)
        {
            calls.Clear();
            var blocked = new AgentSequenceLlm(Action(item.Id));
            var result = await Run(blocked);
            Require(result.step == "wait" && result.actions.Count == 0 && !result.speak && calls.Count == 0 && blocked.Requests.Count == 1,
                "缺联系意图先阻止表达副作用，不向文字/图片/声音/空间执行器发送：" + item.Organ);
        }
        calls.Clear();
        var media = new AgentSequenceLlm(Action("entry.image", "\"heartbeat_intent\":\"想分享刚刚想到的画面\","), "{\"step\":\"wait\"}");
        var mediaResult = await Run(media);
        Require(calls.SequenceEqual(new[] { "entry.image" }) && mediaResult.speak &&
            mediaResult.heartbeat_intent == "想分享刚刚想到的画面" && media.Requests.Count == 2 &&
            media.Requests[1].Contains("本次主动表达已受理"), "有意图可独立发图，收口继承意图和受理状态，不制造文字回复");

        calls.Clear();
        var readThenContact = new AgentSequenceLlm(Action("memory.recall"),
            "{\"step\":\"finish\",\"heartbeat_intent\":\"回想起一件共同经历，想分享自己的感受\",\"reply\":\"刚想起那天的事。\"}");
        Require((await Run(readThenContact)).reply == "刚想起那天的事。" && calls.SequenceEqual(new[] { "memory.recall" }) &&
            readThenContact.Requests.Count == 2, "可以先检索再形成联系意图，沿原工具循环，不额外审核");
        var carried = new AgentSequenceLlm(Action("memory.recall", "\"heartbeat_intent\":\"想分享共同经历\","),
            "{\"step\":\"finish\",\"reply\":\"想起那天了。\"}");
        Require((await Run(carried)).reply == "想起那天了。", "已有联系意图可跨同轮工具步骤继承");
        var withdrawn = new AgentSequenceLlm(Action("memory.recall", "\"heartbeat_intent\":\"想分享共同经历\","),
            "{\"step\":\"finish\",\"heartbeat_intent\":\"\",\"reply\":\"旧草稿不应外发\"}");
        Require((await Run(withdrawn)).step == "wait" && withdrawn.Requests.Count == 2, "显式撤回意图时旧草稿不会外发");

        var human = Turn(true);
        var normal = new AgentSequenceLlm("{\"step\":\"finish\",\"reply\":\"好，那你先忙。\"}");
        Require(!HeartbeatLogic.IsHeartbeatTurn(human) && (await Run(normal, human)).reply == "好，那你先忙。" &&
            normal.Requests.Single().Contains("本轮新消息尚未回应") && !normal.Requests.Single().Contains(AgentLoopPrompts.Heartbeat) &&
            AgentLoopLogic.BuildStable(turn) == AgentLoopLogic.BuildStable(human),
            "用户输入包含心跳文本也走普通回应，不用句子黑名单；两入口保持同一稳定人格前缀");
        var observation = Turn(); observation.Moment.Type = MomentTypes.Sight;
        Require(!HeartbeatLogic.IsHeartbeatTurn(observation), "感官内容碰巧包含心跳文本也不能成为主动入口");
        Console.WriteLine("Heartbeat entry checks passed: separate trigger, no pending reply, same-call intent, media gate, lookup, withdrawal and ordinary dialogue.");
    }

    private static void RunHeartbeatDispatchRecoveryCheck()
    {
        using var store = new SqliteMemoryManager(":memory:");
        store.SavePairIdentity("小雨", "小光", "雨雨");
        var services = new TracePluginServices(store, new HierarchicalVectorRouterLogic(new FakeEncoder()))
            { HeartbeatMinMinutes = 10, HeartbeatMaxMinutes = 10 };
        const string conversation = "heartbeat-dispatch-recovery";
        string firstEvent;
        long recoveryDue;
        using (var manager = new TracePluginManager(store, services))
        {
            manager.RegisterExternal(new TimeSchedulerPlugin());
            var turn = new TraceTurnContext(conversation, Moment(conversation, "测试唤醒"), new(), 0, false, services);
            var call = new BrainCapabilityCallData { capability_id = "time.continue", arguments = new()
                { new BrainCallArgumentData { name = "minutes", value = "10" } } };
            manager.ExecuteAsync(call, turn, default).GetAwaiter().GetResult();
            var due = HeartbeatLogic.NextDueUnixMs(store, conversation).Value;
            var dispatched = manager.PollBackgroundServices(due + 1).Single();
            firstEvent = dispatched.ExternalEventId;
            dispatched.Environment = new EnvironmentObservationData { PlatformId = "builtin.dialogue",
                SessionType = "private", SessionId = "local", SpeakerId = "local-owner", ExclusivePair = true };
            var failingLlm = new AgentSequenceLlm(); // 没有响应，模拟本轮模型请求异常。
            var failed = false;
            try { new KernelLogic(store, failingLlm, manager).ProcessPluginEventAsync(conversation, dispatched).GetAwaiter().GetResult(); }
            catch (InvalidOperationException) { failed = true; }
            Require(failed && failingLlm.Requests.Count == 1, "本轮须实际到达模型并复现异常");
            var next = HeartbeatLogic.NextDueUnixMs(store, conversation);
            Require(next.HasValue && next.Value > due,
                "心跳出队后即使模型/发送异常或进程退出，也必须持久保留下次重新判断的机会");
            recoveryDue = next.Value;
            Require(!manager.PollBackgroundServices(due + 2).Any(), "出队后不能立刻重复派发同一心跳");
        }
        using (var restarted = new TracePluginManager(store, services))
        {
            restarted.RegisterExternal(new TimeSchedulerPlugin());
            var next = restarted.PollBackgroundServices(recoveryDue + 1).Single();
            Require(next.ExternalEventId != firstEvent && HeartbeatLogic.NextDueUnixMs(store, conversation).HasValue,
                "重启后应派发新一轮心跳，不重放旧事件及其行动");
            services.HeartbeatMinMinutes = services.HeartbeatMaxMinutes = 0;
            var nextDue = HeartbeatLogic.NextDueUnixMs(store, conversation).Value;
            Require(!restarted.PollBackgroundServices(nextDue + 1).Any() &&
                    !HeartbeatLogic.NextDueUnixMs(store, conversation).HasValue,
                "手动关闭心跳后，持久兜底也不得继续唤醒");
        }
    }
}
