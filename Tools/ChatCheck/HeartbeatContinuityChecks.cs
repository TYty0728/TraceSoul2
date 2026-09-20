using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Logic;
using TraceSoul2.Manager;
using TraceSoul2.Plugins;
using TraceSoul2.Plugins.Builtin;

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
    }
}
