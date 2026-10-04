using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SQLite;
using TraceSoul2.Data;
using TraceSoul2.Logic;
using TraceSoul2.Manager;
using TraceSoul2.Plugins;
using TraceSoul2.Plugins.Builtin;

internal static partial class Program
{
    private static async Task RunStartupOverviewHostCheckAsync(string assemblyPath)
    {
        var root = Path.Combine(Path.GetTempPath(), "overview-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "tracesoul2-brainframe.sqlite3");
        var day = MemoryDayLogic.CurrentDayKey(DateTimeOffset.Now);
        using var server = new FailureHttpServer(200, System.Text.Json.JsonSerializer.Serialize(new { choices = new[] {
            new { message = new { content = "{\"events\":[{\"text\":\"买了书，约好周末一起读。\",\"sources\":[\"n0\"]}]}" }, finish_reason = "stop" } } }));
        try
        {
            using (var seed = new SqliteMemoryManager(database))
            {
                seed.SaveMoment(new MomentRecord { Id = "existing-source", ConversationId = "tracesoul2", Role = "user", Content = "已有的合成记录", CreatedUnixMs = DateTimeOffset.Now.ToUnixTimeMilliseconds() });
                seed.AppendDayTrajectory("tracesoul2", "existing-source", "买了书，约好周末一起读。");
            }
            var providers = new TraceSoul2.Host.LlmProviderStore(Path.Combine(root, "llm-providers.json"));
            providers.Upsert(new TraceSoul2.Host.LlmProviderRecord { id = "default", model = "test", apiKey = "test", baseUrl = server.Url });
            var assembly = System.Reflection.Assembly.LoadFrom(Path.GetFullPath(assemblyPath));
            var type = assembly.GetType("TraceSoul2.Host.SoulRuntime", true);
            using (var runtime = (IDisposable)Activator.CreateInstance(type, new object[] { root, Path.Combine(root, "empty-plugins"), Path.Combine(root, "plugin-data") }))
            using (var check = new SqliteMemoryManager(database))
            {
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (string.IsNullOrWhiteSpace(check.LoadPluginDocument("runtime.trajectory-overview", "tracesoul2:" + day)) && DateTime.UtcNow < deadline)
                    await Task.Delay(50);
                Require(server.Requests.Count == 1 && !string.IsNullOrWhiteSpace(check.LoadPluginDocument("runtime.trajectory-overview", "tracesoul2:" + day)),
                    "真实宿主启动后自动调用回环复盘模型并提交概览，未输入聊天；calls=" + server.Requests.Count +
                    ";cached=" + (!string.IsNullOrWhiteSpace(check.LoadPluginDocument("runtime.trajectory-overview", "tracesoul2:" + day))));
                Require(check.GetRecentMoments("tracesoul2", 10).Count == 1 && check.GetRecentTurnReviews("tracesoul2", 10).Count == 0,
                    "启动整理不伪造聊天或轮次记录");
            }
            using (var restarted = (IDisposable)Activator.CreateInstance(type, new object[] { root, Path.Combine(root, "empty-plugins"), Path.Combine(root, "plugin-data") }))
            {
                await Task.Delay(200);
                Require(server.Requests.Count == 1, "真实宿主重启读取已有概览，不再发送整理请求");
            }
            Console.WriteLine("Host startup overview passed: existing runtime updated without chat, loopback only, persisted result and restart without repeat.");
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task RunDayOverviewChecksAsync()
    {
        await RunStartupDayOverviewChecksAsync();
        await RunCompactDayOverviewChecksAsync();
        await RunDayOverviewKernelCheckAsync();
        await RunLifeReadingBudgetChecksAsync();
        RunLadderContextChecks();
        RunRetiredActiveEventReadingCleanupChecks();
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

    private static async Task RunStartupDayOverviewChecksAsync()
    {
        using var store = new SqliteMemoryManager(":memory:");
        var start = MemoryDayLogic.CurrentStart(DateTimeOffset.Now);
        var day = start.ToString("yyyy-MM-dd");
        void Seed(string context, int count)
        {
            for (var i = 0; i < count; i++)
            {
                var id = context + i;
                store.SaveMoment(new MomentRecord { Id = id, ConversationId = context, Role = "user", Content = "合成来源", CreatedUnixMs = start.AddHours(7).AddMinutes(i).ToUnixTimeMilliseconds() });
                store.AppendDayTrajectory(context, id, "读完一页书。");
            }
        }
        string Answer(IEnumerable<string> refs) => System.Text.Json.JsonSerializer.Serialize(new { events = new[] {
            new { text = "读书并整理笔记。", sources = refs.ToArray() } } });
        var model = new AgentSequenceLlm(Answer(Enumerable.Range(0, 16).Select(i => "n" + i)), Answer(new[] { "g0", "n0" }));
        var services = new TracePluginServices(store, new HierarchicalVectorRouterLogic(new FakeEncoder())) { ReviewLlm = model };
        Seed("startup", 17);
        var work = DayTrajectoryOverviewLogic.PrepareStartup(services, "startup");
        var turn = new TraceTurnContext("startup", new MomentRecord { ConversationId = "startup" }, new(), 0, true, services,
            environment: new EnvironmentSnapshotData { ContextConversationId = "startup", Visibility = "private" });
        Require(work != null && work.Maintenance && model.Requests.Count == 0 && DayTrajectoryOverviewLogic.Prepare(turn) == null,
            "没有聊天模型或新对话也可安排启动整理，同时占用普通轮次的重复机会");
        await (await work.AnalyzeAsync(default))(default);
        work = DayTrajectoryOverviewLogic.PrepareStartup(services, "startup");
        Require(work != null, "启动批次继续处理不足三条的末尾记录");
        await (await work.AnalyzeAsync(default))(default);
        Require(model.Requests.Count == 2 && DayTrajectoryOverviewLogic.PrepareStartup(services, "startup") == null &&
            DayTrajectoryOverviewLogic.Read(store, "startup", day).Count == 1 && store.GetDayTrajectoryEntries("startup", day).Count == 17,
            "所有原件整理完成，结果保持且重复启动不增加调用");
        Seed("startup-failed", 1);
        services.ReviewLlm = new AgentSequenceLlm("{\"events\":[{\"text\":\"未知来源\",\"sources\":[\"missing\"]}]}");
        work = DayTrajectoryOverviewLogic.PrepareStartup(services, "startup-failed");
        await (await work.AnalyzeAsync(default))(default);
        Require(DayTrajectoryOverviewLogic.PrepareStartup(services, "startup-failed") == null &&
            DayTrajectoryLogic.ReadOverview(store, "startup-failed", day).Text.Contains("读完一页书"),
            "失败留原文并持久记住同一快照，不因重启反复调用");
        Seed("startup-interrupted", 1);
        services.ReviewLlm = new AgentSequenceLlm(Answer(new[] { "n0" }));
        work = DayTrajectoryOverviewLogic.PrepareStartup(services, "startup-interrupted");
        // 模拟排队后进程退出，没有完成分析或提交；下一次启动应恢复。
        Require(DayTrajectoryOverviewLogic.PrepareStartup(services, "startup-interrupted") != null,
            "排队后未完成的任务不伪装成已处理，后续启动可恢复");
        services.ReviewLlm = null;
        Require(DayTrajectoryOverviewLogic.PrepareStartup(services, "startup-interrupted") == null &&
            DayTrajectoryOverviewLogic.PrepareStartup(services, "empty") == null, "没有可用模型或记录时启动不发请求");
        Console.WriteLine("Startup overview checks passed: no chat needed, review-only client, tail batch, persistent completion/failure and interrupted recovery.");
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
        var expected = "上午\n· 小雨买了新书，约好周末一起读。";
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
        store.SavePluginDocument("runtime.trajectory-overview", context + ":" + day + ":attempt:v3", "");
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
        var sameTime = new DayTrajectoryOverviewLogic.Item { Text = at.ToString("M月d日") + "周六上午，小雨买了新书。", Start = at.ToUnixTimeMilliseconds(), End = at.ToUnixTimeMilliseconds() };
        var cleaned = DayTrajectoryLogic.OverviewText(sameTime.Text, sameTime.Start);
        Require(cleaned == "小雨买了新书。" && DayTrajectoryLogic.OverviewText("约好10月5日上午读书。", sameTime.Start) == "约好10月5日上午读书。" &&
            DayTrajectoryLogic.OverviewText(at.AddDays(-1).ToString("M月d日") + "上午，买了书。", sameTime.Start).Contains("月"),
            "只去掉与来源日期相同的开头日期时段，约定时间和不同日期的叙述保留");
        item.End = item.Start;
        var grouped = DayTrajectoryLogic.FormatGroups(DayTrajectoryLogic.OverviewGroups(new[] { item, sameTime }, now));
        Require(grouped.Split("上午").Length == 2 && grouped.Contains("· 计划有了变化。\n· 小雨买了新书。"), "相同时段统一标题并分行列事件");
        store.SavePluginDocument("runtime.trajectory-overview", context + ":" + day, "");
        store.SavePluginDocument("runtime.trajectory-overview", context + ":" + day + ":attempt:v3", "");
        services.Llm = new AgentSequenceLlm("{\"events\":[{\"text\":\"小雨买了新书。\",\"sources\":[\"n0\"]},{\"text\":\"约好周末一起读。\",\"sources\":[\"n0\"]}]}");
        work = DayTrajectoryOverviewLogic.Prepare(turn);
        await (await work.AnalyzeAsync(default))(default);
        var split = DayTrajectoryLogic.ReadOverview(store, context, day, now).Text;
        Require(split == "上午\n· 小雨买了新书。\n· 约好周末一起读。" && DayTrajectoryOverviewLogic.Read(store, context, day).Count == 2,
            "同一来源包含多个事件时允许共同引用，持久读取不再丢弃第二个事件或退回长原文");
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

    private static async Task RunLifeReadingBudgetChecksAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), "tracesoul-reading-" + Guid.NewGuid().ToString("N") + ".sqlite3");
        try
        {
            using var store = new SqliteMemoryManager(path);
            const string context = "reading-budget";
            var now = MemoryDayLogic.CurrentStart(DateTimeOffset.Now).AddHours(8);
            var day = MemoryDayLogic.CurrentDayKey(now);
            var services = new TracePluginServices(store, new HierarchicalVectorRouterLogic(new FakeEncoder()));
            var turn = new TraceTurnContext(context, new MomentRecord { Id = "reading-trigger", ConversationId = context }, new(), 0, true, services,
                environment: new EnvironmentSnapshotData { ContextConversationId = context, RootConversationId = context, Visibility = "private" });
            void AddTrajectory(int from, int count)
            {
                for (var i = from; i < from + count; i++)
                {
                    var id = "reading-" + i;
                    store.SaveMoment(new MomentRecord { Id = id, ConversationId = context, Role = "user", Content = "原件", CreatedUnixMs = now.AddMinutes(i).ToUnixTimeMilliseconds() });
                    store.AppendDayTrajectory(context, id, "第" + i + "件仍在发生的事。" + new string('记', 70));
                }
            }
            AddTrajectory(0, 3);
            Require(LifeReadingBudgetLogic.Prepare(turn) == null, "不到一千字不筛选");
            AddTrajectory(3, 17);
            var originals = store.GetDayTrajectoryEntries(context, day).Select(x => x.Text).ToList();
            var kept = "留下远足改期和书店这两件。";
            var llm = new AgentSequenceLlm("{\"text\":\"" + new string('长', 1001) + "\"}", "{\"text\":\"" + kept + "\"}");
            services.Llm = llm;
            var work = LifeReadingBudgetLogic.Prepare(turn);
            Require(work != null, "超过一千字安排一次筛选");
            await (await work.AnalyzeAsync(default))(default);
            Require(llm.Requests.Count == 2 && llm.Requests[0].Contains("筛掉不重要的") && llm.Requests[0].Contains("最多1000字") &&
                llm.Requests[1].Contains("重新写成") && llm.Requests[1].Contains("不要交回上一份") &&
                !llm.Requests[1].Contains(new string('长', 1001)) &&
                LifeReadingBudgetLogic.TrajectoryReading(store, context, day) == kept &&
                store.GetDayTrajectoryEntries(context, day).Select(x => x.Text).SequenceEqual(originals),
                "超长阅读筛掉不重要的并精简留下的，原件不截断不删除");
            Require(LifeReadingBudgetLogic.Prepare(turn) == null, "同一批内容只筛选一次");
            for (var i = 0; i < 16; i++)
                store.AddTodayNewItems(context, new[] { "新识" + i + new string('知', 70) }, "reading-" + i, day, now.AddMinutes(i).ToUnixTimeMilliseconds());
            var facts = store.GetTodayNewItemsByDay(context, day).Select(x => x.Content).ToList();
            services.Llm = new AgentSequenceLlm("{\"text\":\"" + new string('超', 1200) + "\"}", "{\"text\":\"" + new string('超', 1200) + "\"}");
            work = LifeReadingBudgetLogic.Prepare(turn);
            await (await work.AnalyzeAsync(default))(default);
            Require(LifeReadingBudgetLogic.TodayNewReading(store, context, day) == null &&
                store.GetTodayNewItemsByDay(context, day).Select(x => x.Content).SequenceEqual(facts) &&
                LifeReadingBudgetLogic.Prepare(turn) == null,
                "筛选两次仍超限就不保存精简文，原件还在，也不反复调用");
        }
        finally { foreach (var suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(path + suffix)) File.Delete(path + suffix); }
        Console.WriteLine("Life reading budget checks passed: one selection under 1000, originals kept, no repeat.");
    }

    private static void RunLadderContextChecks()
    {
        Require(LadderContextLogic.Capacity("day") == 10 && LadderContextLogic.Capacity("week") == 3 &&
            LadderContextLogic.Capacity("month") == 3 && LadderContextLogic.Capacity("year") == 3 &&
            LadderContextLogic.Capacity("forever") == 3, "日榜 10，周月年永久各 3");
        Require(LadderContextLogic.InjectCount("day") == 5 && LadderContextLogic.InjectCount("week") == 2 &&
            LadderContextLogic.InjectCount("month") == 2 && LadderContextLogic.InjectCount("year") == 2 &&
            LadderContextLogic.InjectCount("forever") == 2, "注入日榜 5，其余各 2");
        var now = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.FromHours(8));
        var items = new List<LadderItemRecord>();
        for (var rank = 1; rank <= 11; rank++)
            items.Add(new LadderItemRecord { Tier = "day", PeriodKey = "2026-10-03", ListKind = "event", Rank = rank, Label = "日" + rank, Reason = rank == 1 ? "这一天的终点" : "" });
        items.Add(new LadderItemRecord { Tier = "day", PeriodKey = "2026-10-02", ListKind = "event", Rank = 1, Label = "更早的一天", Reason = "不该出现" });
        items.Add(new LadderItemRecord { Tier = "day", PeriodKey = "2026-10-03", ListKind = "cognition", Rank = 1, Label = "她把怕的事说出来了", Reason = "关系往前了" });
        for (var rank = 1; rank <= 4; rank++)
            items.Add(new LadderItemRecord { Tier = "week", PeriodKey = "2026-09-28", ListKind = "event", Rank = rank + 5, Label = "周" + rank, Reason = "本周理由" });
        items.Add(new LadderItemRecord { Tier = "week", PeriodKey = "2026-09-21", ListKind = "event", Rank = 1, Label = "更早的一周", Reason = "不该出现" });
        items.Add(new LadderItemRecord { Tier = "week", PeriodKey = "2026-10-05", ListKind = "event", Rank = 1, Label = "还没到的一周", Reason = "不该出现" });
        items.Add(new LadderItemRecord { Tier = "month", PeriodKey = "2026-09", ListKind = "event", Rank = 1, Label = "九月留下的事", Reason = "月榜理由" });
        items.Add(new LadderItemRecord { Tier = "year", PeriodKey = "2026", ListKind = "event", Rank = 1, Label = "这一年的事", Reason = "年榜理由" });
        for (var rank = 1; rank <= 4; rank++)
            items.Add(new LadderItemRecord { Tier = "forever", PeriodKey = "forever", ListKind = "event", Rank = rank, Label = "永久" + rank, Reason = "一直记得" });
        var page = LadderContextLogic.Boards(items, now);
        var yesterday = page.Single(x => x.Title == "昨天");
        Require(yesterday.Items.Count(x => x.ListKind != "cognition") == 10 && yesterday.Items.Any(x => x.Label == "日10") &&
            !yesterday.Items.Any(x => x.Label == "日11"),
            "记忆页仍展示昨天日榜 10 条");
        var text = LadderContextLogic.Reading(items, now).Replace("\r\n", "\n");
        Require(text.StartsWith("昨天\n1. 日1\n") && text.Contains("\n5. 日5\n") && !text.Contains("日6") && !text.Contains("日11") &&
            !text.Contains("更早的一天") && text.Contains("认知\n1. 她把怕的事说出来了\n"),
            "注入只取昨天日榜前 5 条，认知另列");
        Require(text.Contains("本周\n1. 周1\n") && text.Contains("\n2. 周2") && !text.Contains("周3") && !text.Contains("6. 周") &&
            !text.Contains("更早的一周") && !text.Contains("还没到的一周"),
            "本周用不晚于本周一的最新周榜，注入只取 2 条");
        Require(text.Contains("本月\n1. 九月留下的事\n") && text.Contains("本年\n1. 这一年的事\n") &&
            text.Contains("永久\n1. 永久1\n") && text.Contains("\n2. 永久2") && !text.Contains("永久3") &&
            !text.Contains("这一天的终点") && !text.Contains("关系往前了") && !text.Contains("本周理由") && !text.Contains("一直记得"),
            "本月、本年、永久取当前可见榜，永久注入 2 条，注入不带上榜理由");
        Console.WriteLine("Ladder context checks passed: page keeps 10/3, injection uses 5/2.");
    }

    private static void RunRetiredActiveEventReadingCleanupChecks()
    {
        var path = Path.Combine(Path.GetTempPath(), "tracesoul-events-reading-" + Guid.NewGuid().ToString("N") + ".sqlite3");
        try
        {
            using (var raw = new SQLiteConnection(path))
            {
                raw.CreateTable<PluginDocumentRecord>();
                raw.Insert(new PluginDocumentRecord
                {
                    Id = "runtime.reading-budget:main:events",
                    PluginId = "runtime.reading-budget",
                    DocumentKey = "main:events",
                    Json = "{\"text\":\"旧活跃事件阅读\"}",
                    UpdatedUnixMs = 1
                });
                raw.Insert(new PluginDocumentRecord
                {
                    Id = "runtime.reading-budget:main:events:attempt",
                    PluginId = "runtime.reading-budget",
                    DocumentKey = "main:events:attempt",
                    Json = "旧原文",
                    UpdatedUnixMs = 1
                });
                raw.Insert(new PluginDocumentRecord
                {
                    Id = "runtime.reading-budget:main:2026-10-03:trajectory",
                    PluginId = "runtime.reading-budget",
                    DocumentKey = "main:2026-10-03:trajectory",
                    Json = "{\"text\":\"轨迹还在\"}",
                    UpdatedUnixMs = 1
                });
            }
            using (var store = new SqliteMemoryManager(path))
            {
                Require(store.LoadPluginDocument("runtime.reading-budget", "main:events") == string.Empty &&
                    store.LoadPluginDocument("runtime.reading-budget", "main:events:attempt") == string.Empty &&
                    store.LoadPluginDocument("runtime.reading-budget", "main:2026-10-03:trajectory").Contains("轨迹还在"),
                    "升级一次性删掉已保存的活跃事件阅读，轨迹阅读保留");
            }
            using (var raw = new SQLiteConnection(path))
            {
                raw.Insert(new PluginDocumentRecord
                {
                    Id = "runtime.reading-budget:main:events",
                    PluginId = "runtime.reading-budget",
                    DocumentKey = "main:events",
                    Json = "later",
                    UpdatedUnixMs = 2
                });
            }
            using (var store = new SqliteMemoryManager(path))
                Require(store.LoadPluginDocument("runtime.reading-budget", "main:events") == "later", "清理只做一次");
        }
        finally { foreach (var suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(path + suffix)) File.Delete(path + suffix); }
        Console.WriteLine("Retired active-event reading cleanup passed: once, trajectory kept.");
    }
}
