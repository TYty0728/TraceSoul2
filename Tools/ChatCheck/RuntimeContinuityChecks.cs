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
using TraceSoul2.Util;

internal static partial class Program
{
    private static async Task RunRuntimeContinuityChecksAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), "runtime-continuity-" + Guid.NewGuid().ToString("N") + ".db");
        const string root = "continuity", day = "2026-09-20";
        var start = MemoryDayLogic.StartOf(day);
        try
        {
            using (var store = new SqliteMemoryManager(path))
            {
                store.SavePairIdentity("本人", "同伴", "称呼");
                var services = new TracePluginServices(store, new HierarchicalVectorRouterLogic(new FakeEncoder()));
                using var plugins = new TracePluginManager(store, services);
                plugins.RegisterExternal(new DialogueTracePlugin());
                plugins.RegisterExternal(new InnerLifePlugin());
                plugins.RegisterExternal(new TimeSchedulerPlugin());
                plugins.RegisterExternal(new MemoryNervePlugin());
                async Task<ChatTurnResultData> Chat(string output) => await new KernelLogic(store,
                    new AgentSequenceLlm(output), plugins).ChatAsync(root, "今天得知青苔喜欢潮湿，记下这件事");
                await Chat("{\"step\":\"finish\",\"reply\":\"记下了\",\"mood\":\"有点好奇\",\"attention\":\"青苔的生长\",\"today\":\"中午看了青苔\",\"new_fact\":\"今天得知青苔喜欢潮湿\"}");
                var before = store.LoadOrCreateInnerRuntime(root);
                Require(before.Mood == "有点好奇" && before.Attention.Single().content == "青苔的生长",
                    "Agent mood 不依赖旧布尔标记，关注实际落库");
                var source = before.Attention.Single();
                var omitted = await Chat("{\"step\":\"finish\",\"reply\":\"晚安\",\"inner\":\"准备休息\",\"today\":\"晚上回家\"}");
                var after = store.LoadOrCreateInnerRuntime(root);
                Require(after.Attention.Single().content == source.content && after.Attention.Single().UpdatedUnixMs == source.UpdatedUnixMs &&
                    after.Mood == before.Mood, "普通入站省略字段不清空关注、不刷新其年龄，也不重置情绪");
                var shared = SubjectRuntimeLogic.View(store, root, root, false);
                Require(shared.Mood == "有点好奇" && shared.Attention.Single().UpdatedUnixMs == source.UpdatedUnixMs,
                    "控制台和主体共享视图保留同一情绪与碎片年龄");
                var nowDay = MemoryDayLogic.CurrentDayKey(DateTimeOffset.Now);
                var timeline = DayTrajectoryLogic.Read(store, root, nowDay).Text;
                Require(timeline.Contains("中午看了青苔") && timeline.Contains("晚上回家"), "晚上追加轨迹仍保留中午");
                Require(store.GetTodayNewItemsByDay(root, nowDay).Single().Content == "今天得知青苔喜欢潮湿",
                    "今日新识走完整 facet 写入，后续省略保留");
                var snapshot = TraceJson.FromJson<TurnPayloadSnapshotData>(store.GetRecentTurnReviews(root, 1).Single().PayloadJson);
                Require(snapshot.agent_decision?.step == "finish" && snapshot.agent_decision.HasStateField("today") &&
                    !snapshot.agent_decision.HasStateField("attention"), "完整 Agent 判断及字段存在性写入持久快照");
                var snapshot2 = TraceJson.FromJson<TurnPayloadSnapshotData>(TraceJson.ToJson(snapshot));
                Require(snapshot2.agent_decision.state_fields.SequenceEqual(snapshot.agent_decision.state_fields),
                    "重启读取不再退化成旧 Mind 判断");
                await Chat("{\"step\":\"finish\",\"reply\":\"放下了\",\"attention\":\"\"}");
                Require(store.LoadOrCreateInnerRuntime(root).Attention.Count == 0, "显式空 attention 才清除关注");
                Require(InnerLifeLogic.LiveAttention(before, source.UpdatedUnixMs + 7 * 3600000L).Count == 0,
                    "保留省略值不改变碎片自然过期规则");

                void Source(string id, string context, DateTimeOffset at) => store.SaveMoment(new MomentRecord
                    { Id = id, ConversationId = context, Role = "user", Content = "测试来源", CreatedUnixMs = at.ToUnixTimeMilliseconds() });
                Source("noon", root, start.AddHours(8));
                Source("evening", root, start.AddHours(16));
                Source("before-boundary", root, start.AddDays(1).AddMilliseconds(-1));
                Source("after-boundary", root, start.AddDays(1));
                // Simulate the old overwrite defect, leaving the noon snapshot as the only evidence.
                store.SaveTurnReview(new TurnReviewRecord { Id = "legacy-review", ConversationId = root,
                    TriggerMomentId = "noon", CreatedUnixMs = start.AddDays(1).ToUnixTimeMilliseconds(),
                    PayloadJson = TraceJson.ToJson(new TurnPayloadSnapshotData { mind_decision = new MindDecisionData { today = "中午旧记录" } }) });
                store.SaveDayTrajectory(day, "晚上旧摘要");
                store.AppendDayTrajectory(root, "evening", "晚上新增记录");
                store.AppendDayTrajectory(root, "evening", "重放不能覆盖原件");
                store.AppendDayTrajectory(root, "before-boundary", "四点之前");
                store.AppendDayTrajectory(root, "after-boundary", "四点之后");
                var entries = store.GetDayTrajectoryEntries(root, day);
                Require(entries.Count == 4 && entries.Any(x => x.Text == "中午旧记录") &&
                    entries.Any(x => x.Text == "晚上旧摘要") && entries.Any(x => x.Text == "晚上新增记录") &&
                    entries.All(x => x.Text != "重放不能覆盖原件"), "恢复旧快照和旧摘要，来源幂等追加不覆盖");
                Require(store.GetDayTrajectoryEntries(root, "2026-09-21").Single().Text == "四点之后",
                    "北京时间四点切日，以来源事件时间归档");
                var publicContext = root + ":environment:public";
                Source("public", publicContext, start.AddHours(9));
                store.AppendDayTrajectory(publicContext, "public", "公开天气记录");
                Require(store.GetDayTrajectoryEntries(publicContext, day).Single().Text == "公开天气记录" &&
                    !DayTrajectoryLogic.Read(store, root, day).Text.Contains("公开天气记录"), "私密和公开轨迹互不串入");
                var denied = false;
                try { store.AppendDayTrajectory(publicContext, "noon", "错误来源"); } catch (InvalidOperationException) { denied = true; }
                Require(denied, "轨迹拒绝不属于当前环境的来源");
            }
            using (var reopened = new SqliteMemoryManager(path))
            {
                Require(reopened.GetDayTrajectoryEntries(root, day).Count == 4, "重启恢复不重复导入，也不丢旧经历");
                Require(reopened.GetTodayNewItemsByDay(root, MemoryDayLogic.CurrentDayKey(DateTimeOffset.Now)).Count == 1,
                    "重启后今日新识仍在");
                reopened.RetireDayRuntimeSamples(root, day);
                Require(reopened.GetDayTrajectoryEntries(root, day).Count == 0 &&
                    reopened.GetDayTrajectoryEntries(root, day, true).Count == 4 &&
                    reopened.GetDayTrajectoryEntries(root, "2026-09-21").Count == 1,
                    "日终只退出目标日视图，原始轨迹保留，读取不能复活已退休条目");
            }
        }
        finally { File.Delete(path); }
        Console.WriteLine("Runtime continuity checks passed: omission/clear, mood, facts, persisted decisions, trajectory recovery, replay, scope, restart and day boundary.");
    }
}
