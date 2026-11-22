using System;
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
    private static async Task RunUtteranceRetractionChecksAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), "tracesoul2-retract-" + Guid.NewGuid().ToString("N") + ".sqlite3");
        const string root = "retract-test", user = "保留的用户发言", bad = "待撤回的异常回复";
        var day = MemoryDayLogic.CurrentDayKey(DateTimeOffset.Now);
        try
        {
            using (var store = new SqliteMemoryManager(path))
            {
                store.SavePairIdentity("本人", "同伴", "称呼");
                var services = new TracePluginServices(store, new HierarchicalVectorRouterLogic(new FakeEncoder()))
                    { HeartbeatMinMinutes = 8, HeartbeatMaxMinutes = 10 };
                using var plugins = new TracePluginManager(store, services);
                RegisterUtteranceTestPlugins(plugins);
                var llm = new AgentSequenceLlm("""
                    {"step":"finish","reply":"待撤回的异常回复","inner":"异常内心","today":"异常轨迹","new_fact":"异常新识",
                    "next_heartbeat_plan":"异常心跳计划","next_heartbeat_minutes":40}
                    """);
                await new KernelLogic(store, llm, plugins).ChatAsync(root, user, historyWindowMax: 24);
                var answer = store.GetLatestPrivateUtterance(root);
                Require(answer.Content == bad && !string.IsNullOrEmpty(answer.ParentMomentId) &&
                    !store.WasUtteranceTurnRetracted(answer.ParentMomentId), "中枢文本回复关联到引起它的轮次");
                var input = store.GetEvidenceMoments(new[] { answer.ParentMomentId }).Single();
                using (var legacy = new SQLite.SQLiteConnection(path))
                    legacy.Execute("UPDATE moments SET ParentMomentId=NULL WHERE Id=?", answer.Id);
                store.SaveMoment(new MomentRecord { Id = "observation", ConversationId = root, Role = MomentTypes.PerceptionRole,
                    Type = MomentTypes.Sight, ParentMomentId = input.Id, Content = "用户图片观察",
                    CreatedUnixMs = answer.CreatedUnixMs + 1 });
                store.SaveMoment(new MomentRecord { Id = "other", ConversationId = root + ":environment:public", Role = "user",
                    Type = MomentTypes.Message, Content = "群里的其他人", MemoryVisibility = "public", CreatedUnixMs = answer.CreatedUnixMs + 2 });

                Require(store.GetDayTrajectoryEntries(root, day).Any(x => x.Text == "异常轨迹") &&
                    store.GetTodayNewItemsByDay(root, day).Any(x => x.Content == "异常新识") &&
                    HeartbeatLogic.NextPlan(store, root) == "异常心跳计划", "复现回复同时留下轨迹、新识与下次计划");
                Require(!store.RetractLatestPrivateUtterance(root, input.Id) && !store.RetractLatestPrivateUtterance(root, "other"),
                    "旧预览和另一个会话的 ID 不会导致误删");
                var succeeded = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() => store.RetractLatestPrivateUtterance(root, answer.Id))));
                Require(succeeded.Count(x => x) == 1 && store.GetLatestPrivateUtterance(root)?.Id == input.Id &&
                    store.GetEvidenceMoments(new[] { input.Id, "observation", "other", answer.Id }).Count == 3,
                    "并发重试只撤同一条助手发言，保留用户输入、用户图片观察与公开会话");
                Require(store.GetDayTrajectoryEntries(root, day).Count == 0 && store.GetTodayNewItemsByDay(root, day).Count == 0 &&
                    store.GetRecentTurnReviews(root, 10).Count == 0 && HeartbeatLogic.NextPlan(store, root) == HeartbeatLogic.DefaultNextPlan &&
                    !SubjectRuntimeLogic.View(store, root, root, false).Narrative.Contains("异常内心") &&
                    store.WasUtteranceTurnRetracted(input.Id), "同轮派生上下文退出，撤回标记阻挡轮后回写");
            }

            using (var store = new SqliteMemoryManager(path))
            {
                var services = new TracePluginServices(store, new HierarchicalVectorRouterLogic(new FakeEncoder()));
                using var plugins = new TracePluginManager(store, services);
                RegisterUtteranceTestPlugins(plugins);
                var llm = new AgentSequenceLlm("{\"step\":\"finish\",\"reply\":\"正常测试回复\"}");
                var kernel = new KernelLogic(store, llm, plugins);
                var message = new PluginEventData { PluginId = "builtin.dialogue", ExternalEventId = "repeat-me",
                    Role = "user", Type = MomentTypes.Message, Content = "待撤回的用户发言", OccurredUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
                await kernel.ProcessPluginEventAsync(root, message, historyWindowMax: 24);
                Require(llm.Requests.Single().Contains(user) && !llm.Requests.Single().Contains(bad) &&
                    !llm.Requests.Single().Contains("异常轨迹") && !llm.Requests.Single().Contains("异常心跳计划"),
                    "重启后的真实 Agent 请求保留前一条用户消息，已撤回回复及其派生片段不再进入拼接");
                var probeAnswer = store.GetLatestPrivateUtterance(root);
                Require(probeAnswer.Content == "正常测试回复" && store.RetractLatestPrivateUtterance(root, probeAnswer.Id), "只撤除探测轮的本地模拟回复");
                var last = store.GetLatestPrivateUtterance(root);
                Require(last.Content == message.Content && store.RetractLatestPrivateUtterance(root, last.Id), "最新是用户就撤用户");
                await kernel.ProcessPluginEventAsync(root, message, historyWindowMax: 24);
                Require(llm.Requests.Count == 1 && store.GetLatestPrivateUtterance(root)?.Content == user,
                    "同一平台事件的重放不会复活用户发言或触发模型");
                RunArchivedRetractionCheck(store, root);
            }
            Console.WriteLine("Utterance retraction checks passed: two speakers, stale and duplicate requests, derived state, archive, restart and prompt assembly.");
        }
        finally { File.Delete(path); }
    }

    private static void RegisterUtteranceTestPlugins(TracePluginManager plugins)
    {
        plugins.RegisterExternal(new DialogueTracePlugin());
        plugins.RegisterExternal(new InnerLifePlugin());
        plugins.RegisterExternal(new TimeSchedulerPlugin());
        plugins.RegisterExternal(new MemoryNervePlugin());
    }

    private static void RunArchivedRetractionCheck(SqliteMemoryManager store, string root)
    {
        var keep = store.GetLatestPrivateUtterance(root);
        const string id = "archived-utterance", index = "affected-index", entry = "affected-entry";
        var time = keep.CreatedUnixMs + 2;
        var day = MemoryDayLogic.CurrentDayKey(DateTimeOffset.FromUnixTimeMilliseconds(time));
        store.SaveMoment(new MomentRecord { Id = id, ConversationId = root, Role = "user", Content = "已归档的待撤回发言",
            CreatedUnixMs = time, MemoryStatus = "built" });
        using (var db = new SQLite.SQLiteConnection(store.DatabasePath))
        {
            db.Insert(new EventIndexRecord { Id = index, FirstMomentId = "another-source", Status = "active", EventSummary = "混合经历" });
            db.Insert(new EventEntryRecord { Id = entry, IndexId = index, SourceMomentId = keep.Id, Summary = "混合经历" });
            db.Insert(new LadderItemRecord { Id = "affected-rank", RefKind = "event_index", RefId = index, Label = "混合经历" });
        }
        store.SavePluginDocument(SqliteMemoryManager.DailyReviewPlugin, day, TraceJson.ToJson(new
        {
            SourceIds = new[] { keep.Id, id }, Events = new[] { new { Id = entry, IndexId = index, Sources = new[] { keep.Id, id } } },
            Extracted = new[] { keep.Id, id }, CognitionEngine = "batch-v1", Done = true
        }));
        Require(store.RetractLatestPrivateUtterance(root, id) &&
            store.GetActiveEventIndexes().All(x => x.Id != index) && store.GetAllLadderItems().All(x => x.RefId != index) &&
            !store.LoadPluginDocument(SqliteMemoryManager.DailyReviewPlugin, day).Contains(id) &&
            store.LoadPluginDocument(SqliteMemoryManager.DailyReviewPlugin, day).Contains(store.DailyReviewSourceStamp(new[] { keep.Id })),
            "已归档发言从混合事件、榜单和完成检查点移除，其余来源及检查点摘要有效");
    }
}
