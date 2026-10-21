using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Logic;
using TraceSoul2.Manager;
using TraceSoul2.Util;

internal static partial class Program
{
    private static void RunDailyCoreProgressChecks(Assembly assembly)
    {
        var directory = Path.Combine(Path.GetTempPath(), "daily-core-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "brain.db");
        var contextType = assembly.GetType("TraceSoul2.Migrate.MigrationContext", true);
        var builder = assembly.GetType("TraceSoul2.Migrate.DayBuilder", true);
        var storeType = assembly.GetType("TraceSoul2.Manager.SqliteMemoryManager", true);
        var migrationType = assembly.GetType("TraceSoul2.Migrate.MigrationDb", true);
        MethodInfo Method(string name) => builder.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
        object actualStore = null, migration = null;
        IDisposable context = null;
        IMemoryStore store = null;
        void Open()
        {
            context = (IDisposable)Activator.CreateInstance(contextType);
            actualStore = Activator.CreateInstance(storeType, path);
            migration = Activator.CreateInstance(migrationType, Path.Combine(directory, "migration.db"), path);
            contextType.GetProperty("Store").SetValue(context, actualStore);
            contextType.GetProperty("Migration").SetValue(context, migration);
            contextType.GetProperty("Providers").SetValue(context, Activator.CreateInstance(
                assembly.GetType("TraceSoul2.Host.LlmProviderStore", true), Path.Combine(directory, "providers.json")));
            contextType.GetProperty("CognitionEncoder").SetValue(context, new BagOfCharsVectorEncoder());
            store = (IMemoryStore)actualStore;
            store.SavePairIdentity("本人", "同伴", "称呼");
            store.LoadIdentityCards("tracesoul2");
        }
        List<MomentRecord> Seed(string day, int count)
        {
            var start = MemoryDayLogic.StartOf(day).AddHours(2).ToUnixTimeMilliseconds();
            var moments = Enumerable.Range(0, count).Select(i => new MomentRecord {
                Id = day + "-" + i, ConversationId = "tracesoul2", Role = "user",
                Content = "一起核对计划与饮水的测试经历。", CreatedUnixMs = start + i }).ToList();
            foreach (var moment in moments) store.SaveMoment(moment);
            return moments;
        }
        object Source(string day, List<MomentRecord> moments) => Method("OpenCoreProgress").Invoke(null,
            new object[] { context, day, moments, null });
        void Await(MethodInfo method, params object[] args) => ((Task)method.Invoke(null, args)).GetAwaiter().GetResult();
        bool Rejected(Action action)
        {
            try { action(); return false; }
            catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { return true; }
            catch (InvalidOperationException) { return true; }
        }
        string Repair(string body, string id = "append:0") => TraceJson.ToJson(new { items = new[] { new { id, body } } });
        string Event(string summary) => TraceJson.ToJson(new {
            perception_summary = "记录这段测试经历", new_tags = new[] { new {
                name = "计划核对", definition = "共同检查计划", domain_ids = new[] { "relation" }, dimension_ids = new[] { "predicate" } } },
            event_writes = new[] { new { event_summary = summary, entry_summary = "共同检查安排", new_tag_names = new[] { "计划核对" } } } });
        void Run(string day, AgentSequenceLlm llm)
        {
            contextType.GetProperty("Llm").SetValue(context, llm);
            Await(builder.GetMethod("RunAsync"), context, new[] { "--day", day });
        }
        try
        {
            Open();
            var sources = Seed("2020-08-10", 1);
            var source = Source("2020-08-10", sources);
            var stateType = builder.GetNestedType("ObservationProgress");
            var state = Activator.CreateInstance(stateType);
            var draft = TraceJson.ToJson(new { perception_summary = "有效观察", event_writes = new[] {
                new { event_summary = "合格索引原文", entry_summary = new string('好', 80) } },
                event_appends = new[] { new { index_alias = "unchanged-id", entry_summary = new string('长', 98) } } });
            var first = new AgentSequenceLlm(draft, Repair(new string('长', 89)));
            var messages = new List<DeepSeekMessageData> { new("user", "仅测试") };
            Require(Rejected(() => Await(Method("CompleteObservationAsync"), context, first, source, "draft", state, messages)) &&
                first.Requests.Count == 2 && !first.Requests[1].Contains("合格索引原文"),
                "事件98字定向重写为89字仍暂停；仅提交超长字段，不重新生成整批");
            var saved = store.LoadPluginDocument("daily-core-progress-v1", "draft");
            state = JsonSerializer.Deserialize(saved, stateType, new JsonSerializerOptions { IncludeFields = true });
            var bad = new AgentSequenceLlm(Repair("错误字段", "write:0:event"));
            Require(Rejected(() => Await(Method("CompleteObservationAsync"), context, bad, source, "draft", state, messages)) &&
                store.LoadPluginDocument("daily-core-progress-v1", "draft") == saved,
                "修复不得替换合格字段或丢弃已保存草稿");
            var resumed = new AgentSequenceLlm(Repair("后来共同补齐安排"));
            Await(Method("CompleteObservationAsync"), context, resumed, source, "draft", state, messages);
            using (var json = JsonDocument.Parse(store.LoadPluginDocument("daily-core-progress-v1", "draft")))
                Require(resumed.Requests.Count == 1 && json.RootElement.GetProperty("Output").GetProperty("event_writes")[0]
                    .GetProperty("entry_summary").GetString() == new string('好', 80) &&
                    json.RootElement.GetProperty("Output").GetProperty("event_appends")[0].GetProperty("index_alias").GetString() == "unchanged-id",
                    "从草稿恢复只调用一次短句模型，80字边界、别名及其他内容原样保留");

            sources[0].Role = "system_event";
            using (var db = new SQLite.SQLiteConnection(path)) db.Update(sources[0]);
            var stale = new AgentSequenceLlm();
            Require(Rejected(() => Await(Method("CompleteObservationAsync"), context, stale, source, "draft", state, messages)) &&
                stale.Requests.Count == 0, "来源角色变动阻止复用草稿且不消耗模型");

            // Real pipeline, interruption after one committed observation, then process restart.
            const string day = "2020-08-11";
            Seed(day, 19);
            var interrupted = new AgentSequenceLlm(Event("首批共同核对安排"));
            Require(Rejected(() => Run(day, interrupted)), "第二事件批失败应停止当日流程");
            using (var db = new SQLite.SQLiteConnection(path))
                Require(db.Table<EventEntryRecord>().Count() == 1 && db.Table<LifeTagRecord>().Count() > 0,
                    "中途失败保留首批事件和标签");
            var tagCounts = store.GetActiveLifeTags().ToDictionary(x => x.Id, x => x.ActivationCount);
            context.Dispose(); context = null; Open();
            var next = new AgentSequenceLlm(Event("末批补齐安排"), "{\"detail\":\"测试细节原文\"}",
                "{\"detail\":\"测试细节原文\"}", "{\"cognitions\":[]}");
            Require(Rejected(() => Run(day, next)), "模拟在后续身份阶段停止");
            using (var db = new SQLite.SQLiteConnection(path))
                Require(db.Table<EventEntryRecord>().Count() == 2 && db.Table<EventEntryRecord>().ToList().All(x => x.Detail == "测试细节原文") &&
                    db.Table<EventIndexRecord>().Count() == 2 && next.Requests.Count == 5,
                    "重启从第二批续做，首批事件不重复，细节各生成一次，认知完成后保存核心");
            Require(!string.IsNullOrEmpty(store.LoadPluginDocument("daily-review-recovery-v1", "core:" + day)),
                "身份失败前核心检查点已经持久化");
            foreach (var tag in store.GetActiveLifeTags().Where(x => tagCounts.ContainsKey(x.Id)))
                Require(tag.ActivationCount <= tagCounts[tag.Id] + 1, "恢复首批不重复激活标签");

            // A checkpoint and its writes must roll back together, including nested tag transactions.
            var atomic = migrationType.GetMethod("CommitCoreProgress");
            var marker = "rollback";
            Require(Rejected(() => atomic.Invoke(migration, new object[] { (Action)(() => {
                migrationType.GetMethod("AppendEventEntry").Invoke(migration, new object[] {
                    new EventEntryRecord { Id = marker, IndexId = "rollback", SourceMomentId = "none" } });
                migrationType.GetMethod("SaveProgressDocument").Invoke(migration, new object[] { "test", marker, "saved" });
                throw new InvalidOperationException("injected interruption");
            }) })), "注入事务中断");
            using (var db = new SQLite.SQLiteConnection(path))
                Require(db.Find<EventEntryRecord>(marker) == null && string.IsNullOrEmpty(store.LoadPluginDocument("test", marker)),
                    "事件和进度同事务回滚，不留下伪完成标记");

            // A completed detail cannot be regenerated after a later stage fails.
            var detailEntry = new EventEntryRecord { Id = "detail-check", Detail = "已经保存的细节" };
            var detailSource = Source("2020-08-12", Seed("2020-08-12", 1));
            var digest = Method("Digest");
            store.SavePluginDocument("daily-core-progress-v1", "detail-check", TraceJson.ToJson(new {
                Stamp = "detail-source", BodyStamp = (string)digest.Invoke(null, new object[] { detailEntry.Detail }) }));
            Require((bool)Method("RestoreDetail").Invoke(null, new object[] { context, detailSource, "detail-check", "detail-source", detailEntry }),
                "已完成细节直接恢复");
            detailEntry.Detail = "外部修订";
            Require(Rejected(() => Method("RestoreDetail").Invoke(null, new object[] {
                context, detailSource, "detail-check", "detail-source", detailEntry })), "细节被修改时不覆盖正文");

            // More than 256 moments are fingerprinted in full, not truncated by recall limits.
            const string cognitionDay = "2020-08-13";
            var cognitionSources = Seed(cognitionDay, 260);
            var cognitionSource = Source(cognitionDay, cognitionSources);
            var formation = Method("RunCognitionFormationWithRecoveryAsync");
            var cogOutput = TraceJson.ToJson(new { cognitions = new[] { new { operation = "create", summary = "我会共同核对计划",
                domains = new[] { "ass" }, evidence_moment_ids = new[] { cognitionSources[0].Id }, confidence = .7, strength = .6 } } });
            var firstCognition = new AgentSequenceLlm(cogOutput);
            Require(Rejected(() => Await(formation, context, store.LoadPairIdentity(), firstCognition, cognitionDay,
                new List<EventIndexRecord>(), new List<EventEntryRecord>(), cognitionSources, cognitionSource)),
                "后续认知批失败停止且保留首批检查点");
            var beforeCognitions = storeType.GetMethod("CountCognitions").Invoke(actualStore, null);
            var batches = ((System.Collections.IEnumerable)assembly.GetType("TraceSoul2.Logic.RuntimeSliceLogic", true)
                .GetMethod("EvidenceBatches").Invoke(null, new object[] { actualStore, cognitionSources })).Cast<object>().Count();
            var resumeCognition = new AgentSequenceLlm(Enumerable.Repeat("{\"cognitions\":[]}", batches - 1).ToArray());
            Await(formation, context, store.LoadPairIdentity(), resumeCognition, cognitionDay,
                new List<EventIndexRecord>(), new List<EventEntryRecord>(), cognitionSources, cognitionSource);
            Require(resumeCognition.Requests.Count == batches - 1 &&
                Equals(beforeCognitions, storeType.GetMethod("CountCognitions").Invoke(actualStore, null)),
                "认知恢复跳过已提交批，不重放已完成写入");
            var noCalls = new AgentSequenceLlm();
            Await(formation, context, store.LoadPairIdentity(), noCalls, cognitionDay,
                new List<EventIndexRecord>(), new List<EventEntryRecord>(), cognitionSources, cognitionSource);
            Require(noCalls.Requests.Count == 0, "全部认知检查点完成后恢复不再调用模型");
            cognitionSources[^1].Content = "最后一条来源被修改";
            using (var db = new SQLite.SQLiteConnection(path)) db.Update(cognitionSources[^1]);
            Require(Rejected(() => Method("EnsureCoreProgress").Invoke(null, new[] { (object)context, cognitionSource })),
                "第260条来源修订同样使检查点失效");

            // Pre-checkpoint versions are never silently replayed over partial saved events.
            var legacySources = Seed("2020-08-14", 1);
            using (var db = new SQLite.SQLiteConnection(path))
                db.Insert(new EventEntryRecord { Id = "legacy-partial", IndexId = "old", SourceMomentId = legacySources[0].Id });
            Require(Rejected(() => Source("2020-08-14", legacySources)), "旧版部分事件无来源检查点时停止整日重复构筑");
            Console.WriteLine("Daily core progress checks passed: targeted drafts, restart, atomic writes, detail and cognition resume, source and legacy guards.");
        }
        finally { context?.Dispose(); Directory.Delete(directory, true); }
    }
}
