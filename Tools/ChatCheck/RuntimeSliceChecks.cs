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
    private static async Task RunRuntimeSliceChecksAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), "runtime-slices-" + Guid.NewGuid().ToString("N") + ".db");
        const string root = "slices", day = "2020-08-20", privateMarker = "仅私密的桂花感受", publicMarker = "公开交流中的雨声";
        var begin = MemoryDayLogic.StartOf(day).ToUnixTimeMilliseconds();
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
                var cardsBefore = TraceJson.ToJson(store.LoadIdentityCards(root));
                var llm = new AgentSequenceLlm("{\"step\":\"finish\",\"reply\":\"先听你说\",\"inner\":\"还没有想明白\",\"today\":\"从急着建议到先听完\"}");
                await new KernelLogic(store, llm, plugins).ChatAsync(root, "先听我讲完");
                var today = MemoryDayLogic.CurrentDayKey(DateTimeOffset.Now);
                var actual = store.GetRuntimeSlices(root, today).Single();
                Require(llm.Requests.Count == 1 && actual.SnapshotJson.Contains("从急着建议到先听完") && string.IsNullOrEmpty(actual.ReviewId) &&
                    store.GetRuntimeDayReviews(root, today).Count == 0 && store.GetCognitionNodes(20).Count == 0 &&
                    TraceJson.ToJson(store.LoadIdentityCards(root)) == cardsBefore,
                    "真实白天 Kernel 只追加切片，不增加模型阶段、不即时形成长期认知或改写身份");
                RuntimeSliceRecord Sample(string id, string scope, string visibility, string text, long time)
                {
                    store.SaveMoment(new MomentRecord { Id = id, ConversationId = scope, Role = "user", Content = "触发原话" + id,
                        MemoryVisibility = visibility, CreatedUnixMs = time });
                    var slice = new RuntimeSliceRecord { RootConversationId = root, ContextConversationId = scope, MomentId = id,
                        SnapshotJson = TraceJson.ToJson(new { inner = text, today = "这一刻仍未形成结论" }) };
                    store.AppendRuntimeSlice(slice); return slice;
                }
                var first = Sample("slice-first", root, "private", privateMarker, begin + 1);
                var second = Sample("slice-second", root, "private", "先前的感觉发生了变化", begin + 2);
                var pub = Sample("slice-public", root + ":environment:room", "public", publicMarker, begin + 3);
                first.SnapshotJson = "不应覆盖旧切片"; store.AppendRuntimeSlice(first);
                Require(store.GetRuntimeSlices(root, day).Count == 3 && store.GetRuntimeSlices(root, day)[0].SnapshotJson.Contains(privateMarker),
                    "当下轨迹追加保存中间变化，同轮重放不覆盖最初切片");
                // Historical rows remain readable; the retired daily-summary writer is not retained for tests.
                void Historical(string id, string summary, params RuntimeSliceRecord[] slices)
                {
                    using var db = new SQLite.SQLiteConnection(path);
                    db.Insert(new RuntimeDayReviewRecord { Id = id, RootConversationId = root,
                        ContextConversationId = slices[0].ContextConversationId, MemoryVisibility = slices[0].MemoryVisibility,
                        DayKey = day, Summary = summary, SliceIdsJson = TraceJson.ToJson(slices.Select(x => x.Id).ToList()),
                        MomentIdsJson = TraceJson.ToJson(slices.Select(x => x.MomentId).ToList()), FirstUnixMs = slices[0].CreatedUnixMs });
                    foreach (var slice in slices) db.Execute("UPDATE runtime_slices SET ReviewId=? WHERE Id=?", id, slice.Id);
                }
                Historical("private-history", "桂花感受从急切走向平静，还有未想明白的部分。", first, second);
                Historical("public-history", "公开交流中的雨声让我留意到环境变化。", pub);
                store.RetireDayRuntimeSamples(root, day);
                Require(store.GetRuntimeSlices(root, day).Count == 3 && store.GetRuntimeDayReviews(root, day).Count == 2 &&
                    store.GetRuntimeSlices(root, today, true).Count == 1, "复盘收尾保留可追溯切片和日拼图，不误清次日实时切片");
                var source = store.GetEvidenceMoments(new[] { first.MomentId });
                Require(RuntimeSliceLogic.EvidenceContext(store, source).Contains(privateMarker), "夜间认知形成能把原话与当时的感受重新接起来");
                var owner = new TraceTurnContext(root, Moment(root, "桂花感受"), new(), 0, true, services);
                var environment = new EnvironmentSnapshotData { RootConversationId = root, ContextConversationId = pub.ContextConversationId, Visibility = "public" };
                var guest = new TraceTurnContext(pub.ContextConversationId, Moment(pub.ContextConversationId, "桂花感受"), new(), 0, true, services, environment: environment);
                Require(MemoryRecallLogic.Assemble(owner, new MindDecisionData { query = "桂花感受" }, 5).Contains("走向平静") &&
                    !MemoryRecallLogic.Assemble(guest, new MindDecisionData { query = "桂花感受" }, 5).Contains("走向平静") &&
                    owner.Workspace.RecalledCognitionIds.Count == 0,
                    "日拼图接入长期召回且遵守受众范围，日回望ID不能冒充认知ID");
                var ownerPreview = MemoryRecallLogic.Preview(owner, 5);
                var publicRecallTurn = new TraceTurnContext(pub.ContextConversationId,
                    Moment(pub.ContextConversationId, "公开交流中的雨声"), new(), 0, true, services, environment: environment);
                var publicPreview = MemoryRecallLogic.Preview(publicRecallTurn, 5);
                Require(ownerPreview.Contains("桂花感受从急切走向平静，还有未想明白的部分。") && ownerPreview.Contains(day) &&
                    ownerPreview.Contains("主观回望") && publicPreview.Contains("公开交流中的雨声让我留意到环境变化。") &&
                    !publicPreview.Contains("走向平静") && !publicPreview.Contains("触发原话") ,
                    "私密与公开预召回直接使用对应环境已保存的完整日终回望，不注入零散原句或增加总结调用");
                Require(MemoryRecallLogic.Assemble(publicRecallTurn, new MindDecisionData { query = "触发原话" }, 5)
                    .Contains("触发原话slice-public"), "公开原始消息仍可在主动查证时读取");
                var many = Enumerable.Range(0, 430).Select(i => new MomentRecord { Id = "batch-" + i, ConversationId = root,
                    Content = "完整证据" + i, CreatedUnixMs = i }).ToList();
                var batches = RuntimeSliceLogic.EvidenceBatches(store, many).ToList();
                Require(batches.Count >= 3 && batches.SelectMany(x => x).Select(x => x.Id).SequenceEqual(many.Select(x => x.Id)),
                    "全天经历逐批覆盖，不能只取最后200条后标记整日完成");
                var failed = false;
                try { RuntimeSliceLogic.EvidenceBatches(store, new[] { new MomentRecord { Id = "large", Content = new string('大', 25000) } }).ToList(); }
                catch (InvalidOperationException) { failed = true; }
                Require(failed, "超大单条不能静默跳过后声称完整复盘");
                Sample("late-slice", root, "private", "后来补到的旧日感受", begin + 4);
            }
            using (var store = new SqliteMemoryManager(path))
            {
                Require(store.GetRuntimeDayReviews(root, day).Count == 2 && store.GetRuntimeSlices(root, day, true).Count == 1,
                    "重启仍能读取历史回望和原始切片");

            }
        }
        finally { foreach (var suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(path + suffix)) File.Delete(path + suffix); }
        Console.WriteLine("Runtime slice checks passed: lightweight capture, evidence batches, preserved historical rows, privacy and recall.");
    }

}
