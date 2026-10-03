using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Logic;
using TraceSoul2.Manager;
using TraceSoul2.Plugins;
using TraceSoul2.Plugins.Builtin;

internal static partial class Program
{
    private static async Task RunDayOverviewChecksAsync()
    {
        await RunCompactDayOverviewChecksAsync();
        await RunDayOverviewKernelCheckAsync();
        var path = Path.Combine(Path.GetTempPath(), "tracesoul-overview-" + Guid.NewGuid().ToString("N") + ".sqlite3");
        try
        {
            using var store = new SqliteMemoryManager(path);
            const string context = "overview-check";
            var now = DateTimeOffset.Now;
            var day = MemoryDayLogic.CurrentDayKey(now);
            var start = MemoryDayLogic.CurrentStart(now).AddMinutes(1);
            var services = new TracePluginServices(store, new HierarchicalVectorRouterLogic(new FakeEncoder()));
            var turn = new TraceTurnContext(context, new MomentRecord { Id = "trigger", ConversationId = context }, new(), 0, true, services,
                environment: new EnvironmentSnapshotData { ContextConversationId = context, RootConversationId = context, Visibility = "private" });
            void Add(int i, string text)
            {
                var id = "overview-" + i;
                store.SaveMoment(new MomentRecord { Id = id, ConversationId = context, Role = "user", Content = "原始对话保留", CreatedUnixMs = start.AddMinutes(i).ToUnixTimeMilliseconds() });
                store.AppendDayTrajectory(context, id, text);
            }
            Add(0, "[2026年10月2日 09:34] 准备去郊外远足。");
            Add(1, "因为下雨把远足推迟到周末。");
            Require(DayTrajectoryOverviewLogic.Prepare(turn) == null, "少量短记录不增加整理调用");
            Add(2, "傍晚发现附近有书店，约好改日去逛。");
            var original = store.GetDayTrajectoryEntries(context, day).Select(x => x.Text).ToList();
            var llm = new AgentSequenceLlm("{\"events\":[{\"text\":\"准备远足，因降雨推迟到周末。\",\"sources\":[\"n0\",\"n1\"]},{\"text\":\"发现附近有书店，约好改日去逛。\",\"sources\":[\"n2\"]}]}");
            services.Llm = llm;
            var work = DayTrajectoryOverviewLogic.Prepare(turn);
            Require(work != null && llm.Requests.Count == 0 && DayTrajectoryOverviewLogic.Prepare(turn) == null,
                "只建立轮后任务，同一快照不重复请求，也不在主对话调用整理模型");
            var commit = await work.AnalyzeAsync(default);
            Add(3, "晚上已经回家。");
            await commit(default);
            var view = DayTrajectoryLogic.ReadOverview(store, context, day).Text;
            Require(view.Contains("因降雨推迟") && view.Contains("约好改日") && view.Contains("晚上已经回家") && !view.Contains("2026年10月2日 09:34"),
                "事件合并并保留未处理新进展，重复模型时间退出阅读视图");
            Require(store.GetDayTrajectoryEntries(context, day).Take(3).Select(x => x.Text).SequenceEqual(original) &&
                DayTrajectoryLogic.Read(store, context, day).Text.Contains("准备去郊外远足"), "原始条目和复盘入口保留完整经历");
            Require(RuntimeContextLogic.State(turn, now).Contains("准备远足，因降雨推迟"), "主对话注入也使用事件概览");
            using (var reopened = new SqliteMemoryManager(path))
                Require(DayTrajectoryLogic.ReadOverview(reopened, context, day).Text == view, "概览和来源关系重启后保持");
            Require(DayTrajectoryLogic.CleanLeadingTime("[09:35] [2026年10月2日 09:36] 新变化") == "新变化" &&
                DayTrajectoryLogic.CleanLeadingTime("约好[09:35]见面") == "约好[09:35]见面", "只消除前缀时间，正文中的约定时间保留");
            Add(4, "开始读新买的书。"); Add(5, "选好了下次阅读的章节。");
            services.Llm = new AgentSequenceLlm("{\"events\":[{\"text\":\"缺失来源的概括\",\"sources\":[\"n0\"]}]}");
            var invalid = DayTrajectoryOverviewLogic.Prepare(turn);
            Require(invalid != null && await invalid.AnalyzeAsync(default) == null && DayTrajectoryOverviewLogic.Prepare(turn) == null,
                "漏掉输入依据时保留原视图，同一失败批次不自动反复调用");
            Require(DayTrajectoryLogic.ReadOverview(store, context, day).Text.Contains("因降雨推迟"), "错误不清除旧概览");
            var guest = new TraceTurnContext(context, turn.Moment, new(), 0, true, services,
                environment: new EnvironmentSnapshotData { ContextConversationId = context, Visibility = "public" });
            Require(DayTrajectoryOverviewLogic.Prepare(guest) == null && !RuntimeContextLogic.State(guest, now).Contains("因降雨推迟"),
                "公开环境不读取或整理私密轨迹");
            Require(DayTrajectoryLogic.ReadOverview(store, context, start.AddDays(1).ToString("yyyy-MM-dd")) == null,
                "不同记忆日不混入旧概览");
            Add(6, "读完第一章。"); Add(7, "准备晚餐。"); Add(8, "晚餐结束。");
            services.Llm = new AgentSequenceLlm("{\"events\":[{\"text\":\"从远足计划转为阅读，晚上完成晚餐。\",\"sources\":[\"g0\",\"g1\",\"n0\",\"n1\",\"n2\",\"n3\",\"n4\",\"n5\"]}]}");
            var newer = await DayTrajectoryOverviewLogic.Prepare(turn).AnalyzeAsync(default);
            await newer(default);
            var newest = DayTrajectoryLogic.ReadOverview(store, context, day).Text;
            await commit(default);
            Require(DayTrajectoryLogic.ReadOverview(store, context, day).Text == newest, "旧后台结果不能覆盖更新的概览");
            store.RetireDayRuntimeSamples(context, day);
            await newer(default);
            Require(DayTrajectoryLogic.ReadOverview(store, context, day) == null && store.GetDayTrajectoryEntries(context, day, true).Count == 9,
                "日终退出后旧后台结果不复活当日视图，原始条目继续保存");
            Console.WriteLine("Day overview checks passed: grouped view, full sources, new entries, restart, time prefixes, privacy and bounded failure.");
        }
        finally { Delete(path); Delete(path + "-wal"); Delete(path + "-shm"); }
    }

    private static async Task RunCompactDayOverviewChecksAsync()
    {
        using var store = new SqliteMemoryManager(":memory:");
        const string context = "compact-overview";
        var start = MemoryDayLogic.CurrentStart(DateTimeOffset.Now);
        var at = start.AddHours(7).AddMinutes(18);
        var now = start.AddHours(11);
        var day = start.ToString("yyyy-MM-dd");
        const string original = "早上小雨买来了一本新书，笑着递给我看，我接过来翻了翻封面，想着终于又有新的故事可以一起读，约好周末慢慢看。";
        store.SaveMoment(new MomentRecord { Id = "compact-source", ConversationId = context, Role = "user", Content = "合成记录", CreatedUnixMs = at.ToUnixTimeMilliseconds() });
        store.AppendDayTrajectory(context, "compact-source", original);
        var llm = new AgentSequenceLlm("{\"events\":[{\"text\":\"小雨买了新书，约好周末一起读。\",\"sources\":[\"n0\"]}]}");
        var services = new TracePluginServices(store, new HierarchicalVectorRouterLogic(new FakeEncoder())) { Llm = llm };
        var turn = new TraceTurnContext(context, new MomentRecord { ConversationId = context }, new(), 0, true, services,
            environment: new EnvironmentSnapshotData { ContextConversationId = context, Visibility = "private" });
        var work = DayTrajectoryOverviewLogic.Prepare(turn);
        Require(work != null && work.Maintenance && llm.Requests.Count == 0, "单条长记录立即安排后台精简，不等待三条");
        await (await work.AnalyzeAsync(default))(default);
        var expected = "上午 · 小雨买了新书，约好周末一起读。";
        Require(DayTrajectoryLogic.ReadOverview(store, context, day, now).Text == expected &&
            RuntimeContextLogic.State(turn, now).Contains(expected), "页面与注入复用时段加短句，约定条件保留");
        Require(DayTrajectoryLogic.ReadOverview(store, context, day, now.ToOffset(TimeSpan.Zero)).Text == expected &&
            store.GetDayTrajectoryEntries(context, day).Single().Text == original && DayTrajectoryOverviewLogic.Prepare(turn) == null,
            "阅读统一北京时间，原件完整保留，精简后不重复调用");
        var sourceId = store.GetDayTrajectoryEntries(context, day).Single().Id;
        store.SavePluginDocument("runtime.trajectory-overview", context + ":" + day,
            System.Text.Json.JsonSerializer.Serialize(new DayTrajectoryOverviewLogic.Overview { events = new() {
                new() { text = "小雨买来新书，笑着翻给我看，两个人约好周末再读。", sources = new() { sourceId } } } }));
        // 模拟升级前的尝试记录；新样式只安排一次刷新。
        store.SavePluginDocument("runtime.trajectory-overview", context + ":" + day + ":attempt:v2", "");
        store.SavePluginDocument("runtime.trajectory-overview", context + ":" + day + ":attempt", "[\"" + sourceId + "\"]");
        services.Llm = new AgentSequenceLlm("{\"events\":[{\"text\":\"小雨买了新书，约好周末一起读。\",\"sources\":[\"g0\"]}]}");
        work = DayTrajectoryOverviewLogic.Prepare(turn);
        Require(work != null && DayTrajectoryOverviewLogic.Prepare(turn) == null, "已有旧概览也精简一次，旧版本尝试记录不阻挡升级");
        await (await work.AnalyzeAsync(default))(default);
        Require(DayTrajectoryLogic.ReadOverview(store, context, day, now).Text == expected && DayTrajectoryOverviewLogic.Prepare(turn) == null,
            "旧概览刷新后保存新样式版本并停止重复整理");
        var item = new DayTrajectoryOverviewLogic.Item { Text = "计划有了变化。", Start = at.ToUnixTimeMilliseconds(), End = at.AddMinutes(10).ToUnixTimeMilliseconds() };
        Require(DayTrajectoryLogic.OverviewLine(item, now) == "上午 · 计划有了变化。", "同一时段合并标签");
        item.End = now.ToUnixTimeMilliseconds();
        Require(DayTrajectoryLogic.OverviewLine(item, now) == "上午至下午 · 计划有了变化。", "跨时段保留范围");
        Require(DayTrajectoryLogic.OverviewLine(item, now.AddDays(1)) == "昨天上午至昨天下午 · 计划有了变化。", "跨日沿用活跃事件的相对日期语义");
        Console.WriteLine("Compact day overview checks passed: single long entry, shared natural time, preserved source and legacy view refresh.");
    }

    private static async Task RunDayOverviewKernelCheckAsync()
    {
        using var store = new SqliteMemoryManager(":memory:");
        store.SavePairIdentity("本人", "同伴", "称呼");
        EnvironmentLogic.SaveSettings(store, new EnvironmentSettings { OwnerAccounts = new() {
            new() { PlatformId = "builtin.onebot", UserId = "owner" } } });
        var services = new TracePluginServices(store, new HierarchicalVectorRouterLogic(new FakeEncoder()));
        using var manager = new TracePluginManager(store, services);
        manager.RegisterExternal(new InnerLifePlugin());
        manager.RegisterExternal(new TimeSchedulerPlugin());
        var delivery = new AutomaticPhotoProbe { Active = true };
        manager.RegisterExternal(delivery);
        var replies = new AgentSequenceLlm("{\"step\":\"finish\",\"reply\":\"在听。\",\"today\":\"开始读书。\"}",
            "{\"step\":\"finish\",\"reply\":\"在听。\",\"today\":\"读完第一章。\"}",
            "{\"step\":\"finish\",\"reply\":\"在听。\",\"today\":\"写下阅读感受。\"}");
        var review = new AgentSequenceLlm("{\"events\":[{\"text\":\"读完第一章，写下阅读感受。\",\"sources\":[\"n0\",\"n1\",\"n2\"]}]}");
        services.ReviewLlm = review;
        KernelLogic.DeferredTurnWork deferred = null;
        for (var i = 0; i < 3; i++)
        {
            var kernel = new KernelLogic(store, replies, manager);
            await kernel.ProcessPluginEventAsync("kernel-overview", new PluginEventData { PluginId = "builtin.onebot", Role = "本人", Content = "聊聊阅读",
                Wake = KernelWakeValues.Mind, Environment = new() { PlatformId = "builtin.onebot", SessionType = "private", SessionId = "owner",
                    SpeakerId = "owner", ExclusivePair = true, DirectAddress = true } });
            deferred = kernel.TakeDeferredWork();
            Require((i == 2) == (deferred != null), "实际对话入口积累片段后才安排概览");
        }
        Require(delivery.Texts == 3 && replies.Requests.Count == 3 && review.Requests.Count == 0 && deferred.Maintenance,
            "文字已发出，主对话不增加调用，概览进入独立整理队列");
        var commit = await deferred.AnalyzeAsync(default);
        await commit(default);
        Require(review.Requests.Count == 1 && DayTrajectoryLogic.ReadOverview(store, "kernel-overview", MemoryDayLogic.CurrentDayKey(DateTimeOffset.Now))
            .Text.Contains("读完第一章，写下阅读感受"), "真实内核到复盘模型再到阅读视图的完整链路");
    }
}
