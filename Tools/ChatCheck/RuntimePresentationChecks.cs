using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Logic;
using TraceSoul2.Manager;
using TraceSoul2.Plugins;
using TraceSoul2.Plugins.Builtin;
using TraceSoul2.Util;

internal static partial class Program
{
    private sealed class PresentationLifeStore : ILifeStateStore
    {
        public LifeStateData Value = new();
        public int Reads;
        public LifeStateData Load(string conversationId) { Reads++; return Value; }
        public LifeStateData Update(string conversationId, LifeStatePatchData patch) => throw new Exception("展示不应写生活状态");
    }

    private static async Task RunRuntimePresentationChecksAsync()
    {
        using var store = new SqliteMemoryManager(":memory:");
        const string root = "state-presentation";
        store.SavePairIdentity("小雨", "小光", "雨雨");
        var now = DateTimeOffset.Parse("2026-10-01T17:00:00Z"); // 北京时间 10 月 2 日 01:00，仍属 10 月 1 日。
        var life = new PresentationLifeStore();
        var services = new TracePluginServices(store, new HierarchicalVectorRouterLogic(new FakeEncoder())) { LifeState = life };
        var environment = new EnvironmentSnapshotData { RootConversationId = root, ContextConversationId = root,
            Visibility = "private", SessionType = "private", SpeakerIsOwner = true, AudienceConfirmed = true,
            PlatformId = "builtin.dialogue", SpeakerId = "local-owner" };
        var turn = new TraceTurnContext(root, Moment(root, "在吗"), new(), 0, true, services, environment: environment);
        var original = new SubjectState
        {
            Narrative = new SubjectFragment { Text = "还记得窗边的雨声", Context = root, UpdatedUnixMs = now.AddHours(-25).ToUnixTimeMilliseconds() },
            Mood = new SubjectFragment { Text = "轻松", Context = root, UpdatedUnixMs = now.AddHours(-2).ToUnixTimeMilliseconds() },
            Attention = new SubjectFragment { Context = root, Text = TraceJson.ToJson(new List<AttentionItemData> {
                new() { content = "最近想起的书", UpdatedUnixMs = now.AddHours(-1).ToUnixTimeMilliseconds() },
                new() { content = "过期关注", UpdatedUnixMs = now.AddHours(-7).ToUnixTimeMilliseconds() } }) }
        };
        store.SavePluginDocument(SubjectRuntimeLogic.StoreId, root, TraceJson.ToJson(original));
        life.Value.activity = "看书";
        life.Value.activity_source = LifeStateSourceValues.Mind;
        life.Value.activity_updated_unix_ms = now.AddHours(-8).ToUnixTimeMilliseconds();
        var state = RuntimeContextLogic.State(turn, now);
        Require(state.Contains("2026年10月2日 01:00") && state.Contains("北京时间 UTC+08:00") &&
            state.Contains("生活位置（初始设定；时间未记录）：家里") &&
            state.Contains("生活活动（自己留下的生活记录；2026年10月1日 17:00）：看书") &&
            state.Contains("留下的感受（内心记录；2026年10月1日 00:00）：还记得窗边的雨声") &&
            state.Contains("延续的情绪（情绪记录；2026年10月1日 23:00）：轻松") &&
            state.Contains("最近想起的书") && !state.Contains("过期关注") && !state.Contains("实际位置") && !state.Contains("正在做："),
            "来源与每个字段的记录时间独立展示，旧状态不变成当前观测，关注仍按原龄过期");
        foreach (var offset in new[] { TimeSpan.Zero, TimeSpan.FromHours(-7), TimeSpan.FromHours(8) })
            Require(RuntimeContextLogic.State(turn, now.ToOffset(offset)) == state, "同一时刻的状态展示不受宿主输入时区影响");
        Require(store.LoadPluginDocument(SubjectRuntimeLogic.StoreId, root) == TraceJson.ToJson(original),
            "展示不会续龄或重写主体感受");

        var day = MemoryDayLogic.CurrentDayKey(now);
        foreach (var (id, at, text) in new[] {
            ("before-midnight", now.AddHours(-2), "午夜前的经历"), ("after-midnight", now.AddMinutes(-30), "午夜后的经历"),
            ("next-day", now.AddHours(3), "四点后的经历") })
        {
            store.SaveMoment(new MomentRecord { Id = id, ConversationId = root, Role = "user", Content = text, CreatedUnixMs = at.ToUnixTimeMilliseconds() });
            store.AppendDayTrajectory(root, id, text);
        }
        store.AddTodayNewItems(root, new[] { "新得知的内容" }, "after-midnight", day, now.AddMinutes(-30).ToUnixTimeMilliseconds());
        state = RuntimeContextLogic.State(turn, now);
        Require(state.Contains("2026年10月1日 04:00 至 2026年10月2日 04:00") &&
            state.Contains("昨天深夜 · 午夜前的经历") && state.Contains("凌晨 · 午夜后的经历") &&
            state.Contains("[2026年10月2日 00:30] 新得知的内容") && !state.Contains("四点后的经历"),
            "跨午夜轨迹使用相对日期和时段，新识精确时间及04:00归属保持");
        var afterBoundary = RuntimeContextLogic.State(turn, now.AddHours(3));
        Require(afterBoundary.Contains("四点后的经历") && !afterBoundary.Contains("午夜前的经历"), "切日后展示新日期范围的经历");

        var publicEnvironment = new EnvironmentSnapshotData { RootConversationId = root, ContextConversationId = root + ":environment:room",
            Visibility = "public", SessionType = "group" };
        var guest = new TraceTurnContext(publicEnvironment.ContextConversationId, Moment(publicEnvironment.ContextConversationId, "大家好"),
            new(), 0, true, services, environment: publicEnvironment);
        var reads = life.Reads;
        var publicState = RuntimeContextLogic.State(guest, now);
        Require(!publicState.Contains("窗边的雨声") && !publicState.Contains("看书") && !publicState.Contains("最近想起的书") &&
            !publicState.Contains("午夜前的经历") && !publicState.Contains("2026年10月1日 23:00") && reads == life.Reads,
            "公开视图不读取私人生活，也不泄露私人感受的正文或时间");

        // 走真实插件装配，确保今日新识只显示一次，时间与计划段仍保留。
        var live = DateTimeOffset.Now;
        store.AddTodayNewItems(root, new[] { "单一注入的新识" }, "fixture", MemoryDayLogic.CurrentDayKey(live), live.ToUnixTimeMilliseconds());
        using var manager = new TracePluginManager(store, services);
        manager.RegisterExternal(new MemoryNervePlugin());
        manager.RegisterExternal(new TimeSchedulerPlugin());
        await manager.BuildContextBlocksAsync(turn, default);
        Require(turn.Workspace.ContextBlocks.Any(x => x.FacetId == "memory.today.new"), "真实新识插件仍提供上下文及写回入口：" +
            string.Join(";", manager.GetPlugins().Select(x => x.Id + ":" + x.Enabled + ":" + x.LoadError)) +
            ";items=" + store.GetTodayNewItems(root, MemoryDayLogic.CurrentStart(live).ToUnixTimeMilliseconds(), 10).Count);
        var context = AgentPromptContextLogic.Context(turn);
        Require(context.Split("单一注入的新识").Length == 2 && !context.Contains("【观察：今日新识】") &&
            context.Split(turn.Workspace.ContextBlocks.Single(x => x.FacetId == "time.context").Content).Length == 2,
            "新识和时间分别只注入一次");
        Console.WriteLine("Runtime presentation checks passed: individual provenance/times, defaults, midnight/day boundary, privacy and single injection.");
    }
}
