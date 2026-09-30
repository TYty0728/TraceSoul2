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
    private static void RunRuntimeSliceMigrationChecks(System.Reflection.Assembly assembly)
    {
        var dir = Path.Combine(Path.GetTempPath(), "slice-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        IDisposable context = null;
        try
        {
            var contextType = assembly.GetType("TraceSoul2.Migrate.MigrationContext", true);
            var storeType = assembly.GetType("TraceSoul2.Manager.SqliteMemoryManager", true);
            var migrationType = assembly.GetType("TraceSoul2.Migrate.MigrationDb", true);
            var builder = assembly.GetType("TraceSoul2.Migrate.DayBuilder", true);
            var db = Path.Combine(dir, "brain.db");
            context = (IDisposable)Activator.CreateInstance(contextType); // 不调用会加载正式配置的 Create()。
            var actualStore = Activator.CreateInstance(storeType, db);
            contextType.GetProperty("Store").SetValue(context, actualStore);
            var migration = Activator.CreateInstance(migrationType, Path.Combine(dir, "migration.db"), db);
            contextType.GetProperty("Migration").SetValue(context, migration);
            var store = (IMemoryStore)actualStore;
            store.SavePairIdentity("本人", "同伴", "称呼");
            const string day = "2020-08-20", root = "tracesoul2";
            var time = MemoryDayLogic.StartOf(day).ToUnixTimeMilliseconds();
            var sources = Enumerable.Range(0, 250).Select(i => new MomentRecord { Id = "actual-source-" + i,
                ConversationId = root, Role = "user", Content = "完整原文标记_" + i + "_结束", CreatedUnixMs = time + i }).ToList();
            foreach (var moment in sources) store.SaveMoment(moment);
            storeType.GetMethod("AppendRuntimeSlice").Invoke(actualStore, new object[] { new RuntimeSliceRecord {
                RootConversationId = root, ContextConversationId = root, MomentId = sources[0].Id,
                SnapshotJson = "{\"inner\":\"真实日构建切片标记\"}" } });
            var night = new AgentSequenceLlm(Enumerable.Repeat("{\"cognitions\":[]}", 10).ToArray());
            var formation = builder.GetMethod("RunCognitionFormationAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            ((Task)formation.Invoke(null, new object[] { context, store.LoadPairIdentity(), night, day,
                new List<EventIndexRecord>(), new List<EventEntryRecord>(), sources })).GetAwaiter().GetResult();
            Require(night.Requests.Count >= 3 && sources.All(m => night.Requests.Any(p => p.Contains(m.Content))) &&
                night.Requests[0].Contains("真实日构建切片标记"),
                "实际 Migration 认知阶段覆盖全天完整原文，并带上对应当下切片");
            migrationType.GetMethod("MarkDayCompleted").Invoke(migration, new object[] { day });
            var review = new AgentSequenceLlm("{\"summary\":\"这一天留下了仍待理解的感受。\"}");
            contextType.GetProperty("Llm").SetValue(context, review);
            var run = builder.GetMethod("RunAsync");
            ((Task)run.Invoke(null, new object[] { context, new[] { "--day", day } })).GetAwaiter().GetResult();
            ((Task)run.Invoke(null, new object[] { context, new[] { "--day", day } })).GetAwaiter().GetResult();
            var reviews = (List<RuntimeDayReviewRecord>)storeType.GetMethod("GetRuntimeDayReviews").Invoke(actualStore, new object[] { root, day });
            Require(review.Requests.Count == 1 && reviews.Count == 1,
                "实际日构建完成分支仍补齐待复盘切片，重跑不重复调用模型");
        }
        finally { context?.Dispose(); Directory.Delete(dir, true); }
        Console.WriteLine("Runtime slice Migration checks passed: actual day entry, all evidence batches, late slices and replay.");
    }

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
                var failed = false;
                try { await RuntimeSliceLogic.ReviewDayAsync(store, new AgentSequenceLlm(), root, day); }
                catch { failed = true; }
                Require(failed && store.GetRuntimeSlices(root, day, true).Count == 3 && store.GetRuntimeDayReviews(root, day).Count == 0,
                    "夜间模型失败保留全部待复盘切片，不能标成成功");
                var night = new AgentSequenceLlm("{\"summary\":\"桂花感受从急切走向平静，还有未想明白的部分。\"}",
                    "{\"summary\":\"公开交流中的雨声让我留意到环境变化。\"}");
                Require(await RuntimeSliceLogic.ReviewDayAsync(store, night, root, day) == 3 && night.Requests.Count == 2 &&
                    night.Requests[0].Contains(privateMarker) && night.Requests[0].Contains("先前的感觉发生了变化") &&
                    !night.Requests[1].Contains(privateMarker) && night.Requests[1].Contains(publicMarker),
                    "夜间按环境和时间整合完整切片，公开整理不携带私人感受");
                var reviews = store.GetRuntimeDayReviews(root, day);
                Require(reviews.Count == 2 && reviews[0].SliceIdsJson.Contains("slice-first") &&
                    reviews[0].MomentIdsJson.Contains("slice-second") && store.GetRuntimeSlices(root, day, true).Count == 0 &&
                    store.GetCognitionNodes(20).Count == 0, "日拼图保留原始切片与触发来源，一时感受不自动升级为稳定认知");
                var empty = new AgentSequenceLlm();
                Require(await RuntimeSliceLogic.ReviewDayAsync(store, empty, root, day) == 0 && empty.Requests.Count == 0,
                    "成功批次重跑不再调用模型或重复整理");
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
                var many = Enumerable.Range(0, 430).Select(i => new MomentRecord { Id = "batch-" + i, ConversationId = root,
                    Content = "完整证据" + i, CreatedUnixMs = i }).ToList();
                var batches = RuntimeSliceLogic.EvidenceBatches(store, many).ToList();
                Require(batches.Count >= 3 && batches.SelectMany(x => x).Select(x => x.Id).SequenceEqual(many.Select(x => x.Id)),
                    "全天经历逐批覆盖，不能只取最后200条后标记整日完成");
                failed = false;
                try { RuntimeSliceLogic.EvidenceBatches(store, new[] { new MomentRecord { Id = "large", Content = new string('大', 25000) } }).ToList(); }
                catch (InvalidOperationException) { failed = true; }
                Require(failed, "超大单条不能静默跳过后声称完整复盘");
                failed = false;
                try { store.CommitRuntimeSliceReview(new[] { actual.Id, pub.Id }, "混合来源"); }
                catch (InvalidOperationException) { failed = true; }
                Require(failed, "跨日跨环境切片不能混为一个复盘批次");
                var late = Sample("late-slice", root, "private", "后来补到的旧日感受", begin + 4);
                Require(store.GetUnreviewedRuntimeDayKeysBefore(begin + 86400000).Contains(day), "迟到切片可由日构建补偿发现");
            }
            using (var store = new SqliteMemoryManager(path))
                Require(store.GetRuntimeDayReviews(root, day).Count == 2 && store.GetRuntimeSlices(root, day, true).Count == 1,
                    "重启保留已落地日拼图和未完成切片，分别继续处理");
        }
        finally { foreach (var suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(path + suffix)) File.Delete(path + suffix); }
        Console.WriteLine("Runtime slice checks passed: lightweight capture, full-day batches, night review, provenance, privacy, recall, failure and restart.");
    }
}
