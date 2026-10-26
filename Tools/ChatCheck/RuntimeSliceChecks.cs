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
            contextType.GetProperty("CognitionEncoder").SetValue(context, new FakeEncoder());
            var migration = Activator.CreateInstance(migrationType, Path.Combine(dir, "migration.db"), db, actualStore);
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
            RunCognitionRepairMigrationChecks(formation, context, store, day, sources[0]);
            RunCognitionRecallMigrationChecks(formation, context, store, day, sources[0]);
            migrationType.GetMethod("MarkDayCompleted").Invoke(migration, new object[] { day });
            var review = new AgentSequenceLlm("{\"summary\":\"这一天留下了仍待理解的感受。\"}");
            contextType.GetProperty("Llm").SetValue(context, review);
            var run = builder.GetMethod("RunAsync");
            ((Task)run.Invoke(null, new object[] { context, new[] { "--day", day } })).GetAwaiter().GetResult();
            ((Task)run.Invoke(null, new object[] { context, new[] { "--day", day } })).GetAwaiter().GetResult();
            var reviews = (List<RuntimeDayReviewRecord>)storeType.GetMethod("GetRuntimeDayReviews").Invoke(actualStore, new object[] { root, day });
            Require(review.Requests.Count == 1 && reviews.Count == 1,
                "实际日构建完成分支仍补齐待复盘切片，重跑不重复调用模型");
            storeType.GetMethod("AppendRuntimeSlice").Invoke(actualStore, new object[] { new RuntimeSliceRecord {
                RootConversationId = root, ContextConversationId = root, MomentId = sources[1].Id,
                SnapshotJson = "{\"inner\":\"迟到的新材料\"}" } });
            var tooLong = TraceJson.ToJson(new { summary = new string('长', 1201) });
            var failedFinal = new AgentSequenceLlm(tooLong, TraceJson.ToJson(new { text = new string('长', 1201) }));
            contextType.GetProperty("Llm").SetValue(context, failedFinal);
            try { ((Task)run.Invoke(null, new object[] { context, new[] { "--day", day } })).GetAwaiter().GetResult(); }
            catch (InvalidOperationException) { }
            Require(failedFinal.Requests.Count == 2 && failedFinal.Requests[1].Contains("上限1000字") &&
                !failedFinal.Requests[1].Contains(new string('长', 1201)) && !failedFinal.Requests[1].Contains("按原篇幅重新写下") &&
                !failedFinal.Requests[0].Contains("请合并重复叙述") &&
                ((List<RuntimeSliceRecord>)storeType.GetMethod("GetRuntimeSlices").Invoke(actualStore, new object[] { root, day, true })).Count == 1 &&
                (bool)storeType.GetMethod("HasPendingRuntimeDaySummary").Invoke(actualStore, new object[] { root, day }),
                "迟到回望从原件重新成稿，超长不提交，原始切片仍待处理");
            using (var connection = new SQLite.SQLiteConnection(db))
                connection.Execute("UPDATE plugin_documents SET Json=json_set(Json, '$.NextAttemptUnixMs', 0) WHERE PluginId=?", ReviewWorkLogic.StoreId);
            var resumedFinal = new AgentSequenceLlm("{\"text\":\"迟到的这一段\"}");
            contextType.GetProperty("Llm").SetValue(context, resumedFinal);
            ((Task)run.Invoke(null, new object[] { context, new[] { "--day", day } })).GetAwaiter().GetResult();
            ((Task)run.Invoke(null, new object[] { context, new[] { "--day", day } })).GetAwaiter().GetResult();
            Require(resumedFinal.Requests.Count == 1 &&
                !(bool)storeType.GetMethod("HasPendingRuntimeDaySummary").Invoke(actualStore, new object[] { root, day }),
                "补上符合篇幅的迟到段落后直接形成整天正文，再次运行不再调用模型");
        }
        finally { context?.Dispose(); Directory.Delete(dir, true); }
        Console.WriteLine("Runtime slice Migration checks passed: actual day entry, all evidence batches, late slices and replay.");
    }

    private static void RunCognitionRepairMigrationChecks(System.Reflection.MethodInfo formation, IDisposable context,
        IMemoryStore store, string day, MomentRecord source)
    {
        const string tagId = "concept.life.test-opaque-id", wrongTag = "concept.life.合成测试标签";
        var graph = (ICognitionGraphStore)store;
        store.SeedLifeTags(new[] { new VectorIndexNode(tagId, VectorNodeLevel.Concept, "合成测试标签", "仅用于回归检查",
            "test", new[] { "ass" }, null, null, null) });
        BrainCognitionWriteData Write(string summary, string tag) => new BrainCognitionWriteData
        {
            operation = "create", summary = summary, about = "合成对象", scope = "测试", exceptions = "仅测试",
            domains = new List<string> { "ass" }, identity_slot = "self", confidence = 0.7f, strength = 0.6f,
            tag_ids = new List<string> { tag }, evidence_moment_ids = new List<string> { source.Id }
        };
        string Json(params BrainCognitionWriteData[] writes) => TraceJson.ToJson(new { cognitions = writes });
        void Run(AgentSequenceLlm llm) => ((Task)formation.Invoke(null, new object[] {
            context, store.LoadPairIdentity(), llm, day, new List<EventIndexRecord>(), new List<EventEntryRecord>(),
            new List<MomentRecord> { source } })).GetAwaiter().GetResult();

        var bad = Write("修正标签后保留原来的完整理解", wrongTag);
        var corrected = Write(bad.summary, tagId);
        var repair = new AgentSequenceLlm(Json(bad), Json(corrected), ConsolidationNewReply(corrected.summary));
        Run(repair);
        var saved = graph.GetCognitionNodes(100).Single(x => x.Summary == bad.summary);
        var correction = repair.Messages[1].Last().content;
        Require(repair.Requests.Count == 3 && correction.Contains("cognitions[0].tag_ids[0]") &&
            correction.Contains("竖线左侧") && repair.Messages[1][0].content.Contains(tagId + " | 合成测试标签") &&
            !correction.Contains(wrongTag) && store.GetCognitionTagIds(new[] { saved.Id })[saved.Id].SequenceEqual(new[] { tagId }) &&
            graph.GetCognitionEvidence(new[] { saved.Id }).Single().MomentId == source.Id,
            "真实Migration将具体标签错误送回LLM一次，候选完整保留，重新校验后保存正确标签及原依据");

        var before = graph.GetCognitionNodes(100).Count;
        var sibling = Write("同批有效条目在其他条目失败时也不提前写入", tagId);
        var failed = Write("连续失败不能落库", wrongTag);
        var repeated = new AgentSequenceLlm(Json(sibling, failed), Json(sibling, failed));
        string failure = null;
        try { Run(repeated); } catch (InvalidOperationException ex) { failure = ex.Message; }
        Require(failure != null && failure.Contains("首次错误") && failure.Contains("纠正后错误") &&
            failure.Contains("cognitions[1].tag_ids[0]") && !failure.Contains(wrongTag) &&
            repeated.Requests.Count == 2 && graph.GetCognitionNodes(100).Count == before,
            "连续标签错误只纠正一次，保留两次具体原因，整批不部分写入、不默默清空标签");

        var changedError = Write("标签修好也必须复查原始证据", tagId);
        changedError.evidence_moment_ids[0] = "unknown-private-evidence";
        var invalidEvidence = new AgentSequenceLlm(Json(failed), Json(changedError));
        failure = null;
        try { Run(invalidEvidence); } catch (InvalidOperationException ex) { failure = ex.Message; }
        Require(failure != null && failure.Contains("tag_ids[0]") && failure.Contains("evidence_moment_ids[0]") &&
            !failure.Contains("unknown-private-evidence") && invalidEvidence.Requests.Count == 2 &&
            graph.GetCognitionNodes(100).Count == before,
            "纠正响应仍经过完整依据校验，错误变化不触发第三次请求或保存");

        var noTag = Write("模型明确选择不挂可选标签仍可保存", wrongTag);
        var noTagJson = Json(noTag); noTag.tag_ids.Clear();
        var optional = new AgentSequenceLlm(noTagJson, Json(noTag), ConsolidationNewReply(noTag.summary));
        Run(optional);
        var optionalSaved = graph.GetCognitionNodes(100).Single(x => x.Summary == noTag.summary);
        Require(optional.Requests.Count == 3 && store.GetCognitionTagIds(new[] { optionalSaved.Id }).Values.All(x => x.Count == 0),
            "LLM可以在纠错时明确返回空可选标签，正文及原始依据仍经校验保存");
        var direct = new AgentSequenceLlm(Json(Write("合法结果无需再纠错", tagId)), ConsolidationNewReply("合法结果无需再纠错"));
        Run(direct);
        Require(direct.Requests.Count == 2 && direct.Requests[1].Contains("认知写入前比较"), "合法认知不增加格式纠错，仍经过写入前语义比较");
        Console.WriteLine("Cognition repair Migration checks passed: targeted retry, valid tags, optional empty tags, strict evidence and no partial writes.");
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
                var ownerPreview = MemoryRecallLogic.Preview(owner, 5);
                var publicRecallTurn = new TraceTurnContext(pub.ContextConversationId,
                    Moment(pub.ContextConversationId, "公开交流中的雨声"), new(), 0, true, services, environment: environment);
                var publicPreview = MemoryRecallLogic.Preview(publicRecallTurn, 5);
                Require(ownerPreview.Contains("桂花感受从急切走向平静，还有未想明白的部分。") && ownerPreview.Contains(day) &&
                    ownerPreview.Contains("主观回望") && publicPreview.Contains("公开交流中的雨声让我留意到环境变化。") &&
                    !publicPreview.Contains("走向平静") && !publicPreview.Contains("触发原话") && night.Requests.Count == 2,
                    "私密与公开预召回直接使用对应环境已保存的完整日终回望，不注入零散原句或增加总结调用");
                Require(MemoryRecallLogic.Assemble(publicRecallTurn, new MindDecisionData { query = "触发原话" }, 5)
                    .Contains("触发原话slice-public"), "公开原始消息仍可在主动查证时读取");
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
            {
                Require(store.GetRuntimeDayReviews(root, day).Count == 2 && store.GetRuntimeSlices(root, day, true).Count == 1,
                    "重启保留已落地日拼图和未完成切片，分别继续处理");
                await RunRuntimeReviewBatchChecksAsync(store, day, begin, path);
            }
        }
        finally { foreach (var suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(path + suffix)) File.Delete(path + suffix); }
        Console.WriteLine("Runtime slice checks passed: lightweight capture, full-day batches, night review, provenance, privacy, recall, failure and restart.");
    }

    private static async Task RunRuntimeReviewBatchChecksAsync(SqliteMemoryManager store, string day, long begin, string path)
    {
        string Summary(string text) => TraceJson.ToJson(new { summary = text });
        void Seed(string root, int count, int size = 10, long? at = null)
        {
            for (var i = 0; i < count; i++)
            {
                var id = root + "-" + i;
                if (store.GetEvidenceMoments(new[] { id }).Count == 0)
                    store.SaveMoment(new MomentRecord { Id = id, ConversationId = root, Role = "user", Content = id,
                        CreatedUnixMs = (at ?? begin) + i });
                store.AppendRuntimeSlice(new RuntimeSliceRecord { RootConversationId = root, ContextConversationId = root,
                    MomentId = id, SnapshotJson = TraceJson.ToJson(new { inner = id + new string('材', size) }) });
            }
        }
        Seed("small-bounded", 4);
        var longText = new string('长', 3532);
        var small = new AgentSequenceLlm(Summary(longText), TraceJson.ToJson(new { text = "只留下当天关键变化" }));
        Require(await RuntimeSliceLogic.ReviewDayAsync(store, small, "small-bounded", day) == 4 && small.Requests.Count == 2 &&
            !small.Requests[1].Contains(longText) && small.Requests.All(x => x.Contains("small-bounded-0")) &&
            store.GetRuntimeDayReviews("small-bounded", day).Single().Summary == "只留下当天关键变化",
            "少量切片成稿也先验篇幅，只从原件重新取舍一次，长文不入库也不回传");
        Seed("small-failed", 2);
        var bad = new AgentSequenceLlm(Summary(longText), TraceJson.ToJson(new { text = new string('长', 1201) }));
        var failed = false;
        try { await RuntimeSliceLogic.ReviewDayAsync(store, bad, "small-failed", day); }
        catch (ReviewProsePendingException) { failed = true; }
        Require(failed && bad.Requests.Count == 2 && store.GetRuntimeSlices("small-failed", day, true).Count == 2 &&
            store.GetRuntimeDayReviews("small-failed", day).Count == 0, "持续超长保留原始切片，不保存长段或标记完成");
        using (var reopened = new SqliteMemoryManager(path))
        {
            using (var db = new SQLite.SQLiteConnection(path))
                db.Execute("UPDATE plugin_documents SET Json=json_set(Json, '$.NextAttemptUnixMs', 0) WHERE PluginId=?", ReviewWorkLogic.StoreId);
            var resumed = new AgentSequenceLlm(TraceJson.ToJson(new { text = "恢复后从原件形成短回望" }));
            Require(await RuntimeSliceLogic.ReviewDayAsync(reopened, resumed, "small-failed", day) == 2 && resumed.Requests.Count == 1 &&
                !resumed.Requests[0].Contains(longText), "重新打开数据库仍从原件成稿，不循环缩写旧长段");
            var complete = new AgentSequenceLlm();
            await RuntimeSliceLogic.ReviewDayAsync(reopened, complete, "small-failed", day);
            Require(complete.Requests.Count == 0, "合格日回望再次执行零请求");
        }
        // A day with few but large slices uses the same select-before-write route as a crowded day.
        Seed("independent", 3, 8000);
        var independent = new AgentSequenceLlm(Nominate("s1"), Nominate("s1"), Nominate("s1"), Pick("i3"), Summary("末次决定改变了方向"));
        Require(await RuntimeSliceLogic.ReviewDayAsync(store, independent, "independent", day) == 3 && independent.Requests.Count == 5 &&
            independent.Requests.Take(3).SelectMany(x => new[] { x }).All(x => x.Contains("事件提名")) &&
            independent.Requests[4].Contains("independent-2") && !independent.Requests[4].Contains("independent-0") &&
            independent.Requests.All(x => x.Length < 18000) && store.GetRuntimeDayReviews("independent", day).Count == 1 &&
            store.GetRuntimeDaySummaries("independent", day).Single().Summary == "末次决定改变了方向",
            "少量长切片也先逐批提名、全局取舍后只写一篇，全部原件保留而非三段拼接");
        Seed("late-rebuild", 2);
        await RuntimeSliceLogic.ReviewDayAsync(store, new AgentSequenceLlm(Summary("旧稿专有铺陈")), "late-rebuild", day);
        Seed("late-rebuild", 3);
        var late = new AgentSequenceLlm(Summary("新结果改变了当天感受"));
        Require(await RuntimeSliceLogic.ReviewDayAsync(store, late, "late-rebuild", day) == 1 && late.Requests.Count == 1 &&
            !late.Requests[0].Contains("旧稿专有铺陈") && late.Requests[0].Contains("late-rebuild-0") && late.Requests[0].Contains("late-rebuild-2") &&
            store.GetRuntimeDaySummaries("late-rebuild", day).Single().Summary == "新结果改变了当天感受" &&
            TraceJson.FromJson<List<string>>(store.GetRuntimeDaySummaries("late-rebuild", day).Single().SliceIdsJson).Count == 3 &&
            store.GetRuntimeDayReviews("late-rebuild", day).Count == 2,
            "迟到来源基于整天原件替换当前回望，旧稿仅留历史，不拼接也不当新证据");
        Seed("legacy-long", 2);
        var legacySlices = store.GetRuntimeSlices("legacy-long", day);
        using (var legacyDb = new SQLite.SQLiteConnection(path))
        {
            for (var i = 0; i < 2; i++)
            {
                var slice = legacySlices[i]; var id = "legacy-review-" + i;
                legacyDb.Insert(new RuntimeDayReviewRecord { Id = id, RootConversationId = "legacy-long", ContextConversationId = "legacy-long",
                    DayKey = day, MemoryVisibility = slice.MemoryVisibility, Summary = new string('旧', i == 0 ? 1012 : 2153),
                    SliceIdsJson = TraceJson.ToJson(new[] { slice.Id }), MomentIdsJson = TraceJson.ToJson(new[] { slice.MomentId }), FirstUnixMs = slice.CreatedUnixMs });
                slice.ReviewId = id; legacyDb.Update(slice);
            }
        }
        var oldDay = new AgentSequenceLlm(Summary("从原始切片重新形成的完整回望"));
        Require(await RuntimeSliceLogic.ReviewDayAsync(store, oldDay, "legacy-long", day) == 0 && oldDay.Requests.Count == 1 &&
            !oldDay.Requests[0].Contains(new string('旧', 100)) && oldDay.Requests[0].Contains("legacy-long-0") &&
            store.GetRuntimeDayReviews("legacy-long", day).Sum(x => x.Summary.Length) == 3165 &&
            store.GetRuntimeDaySummaries("legacy-long", day).Single().Summary == "从原始切片重新形成的完整回望",
            "旧版超长材料保留原件历史，当前正文回到切片重新形成，不再交回长文压缩");
        Seed("snapshot-race", 1);
        var race = new SelectionProbeLlm { Reply = _ => { Seed("snapshot-race", 2); return Summary("过期生成"); } };
        failed = false;
        try { await RuntimeSliceLogic.ReviewDayAsync(store, race, "snapshot-race", day); }
        catch (InvalidOperationException) { failed = true; }
        Require(failed && race.Requests.Count == 1 && store.GetRuntimeSlices("snapshot-race", day, true).Count == 2 &&
            store.GetRuntimeDaySummaries("snapshot-race", day).Count == 0, "成稿期间新增来源拒绝过期结果，不假报整日完成");
        for (var i = 0; i < 30; i++)
        {
            var at = begin + i * 86400000L;
            var dailyKey = MemoryDayLogic.CurrentDayKey(DateTimeOffset.FromUnixTimeMilliseconds(at));
            var id = "multi-day-" + i;
            store.SaveMoment(new MomentRecord { Id = id, ConversationId = "multi-day", Role = "user", Content = "合成当天经历", CreatedUnixMs = at });
            store.AppendRuntimeSlice(new RuntimeSliceRecord { RootConversationId = "multi-day", ContextConversationId = "multi-day",
                MomentId = id, SnapshotJson = TraceJson.ToJson(new { inner = "当天感受" + i }) });
            var model = new AgentSequenceLlm(Summary("当天成稿标记" + i));
            await RuntimeSliceLogic.ReviewDayAsync(store, model, "multi-day", dailyKey);
            Require(model.Requests.Count == 1 && !model.Requests[0].Contains("当天成稿标记") && model.Requests[0].Length < 2000,
                "连续30日各自从原件成稿，不接入前日正文累加，输入规模保持稳定");
        }
    }
}
