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
            "{\"step\":\"finish\",\"reply\":\"刚刚有点想你。\",\"next_heartbeat_minutes\":90,\"next_heartbeat_plan\":\"稍后再感受有没有想分享的\"}",
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
        Require(store.GetRecentDialogueMoments(conversation, 20).Count(x => x.Content == "刚刚有点想你。") == 1,
            "没有新入站、未填写 heartbeat_intent 的主动正文也应通过真实发送链只发一次");
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
                store.LoadOrCreateInnerRuntime(conversation).Asleep && llm.Requests.Count == 4,
            "确实选择睡下才停止心跳，且不为主动联系增加额外模型调用");
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
