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
        string Event(string summary, string sourceId) => TraceJson.ToJson(new {
            perception_summary = "记录这段测试经历", new_tags = new[] { new {
                name = "计划核对", definition = "共同检查计划", domain_ids = new[] { "relation" }, dimension_ids = new[] { "predicate" } } },
            event_writes = new[] { new { event_focus = "核对安排的新进展", source_moment_ids = new[] { sourceId }, event_summary = summary, entry_summary = "共同检查安排", new_tag_names = new[] { "计划核对" } } } });
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
            Require(Rejected(() => Await(Method("CompleteObservationAsync"), context, first, source, "draft", state, messages, null, null)) &&
                first.Requests.Count == 2 && !first.Requests[1].Contains("合格索引原文"),
                "事件98字定向重写为89字仍暂停；仅提交超长字段，不重新生成整批");
            var saved = store.LoadPluginDocument("daily-core-progress-v1", "draft");
            state = JsonSerializer.Deserialize(saved, stateType, new JsonSerializerOptions { IncludeFields = true });
            var bad = new AgentSequenceLlm(Repair("错误字段", "write:0:event"));
            Require(Rejected(() => Await(Method("CompleteObservationAsync"), context, bad, source, "draft", state, messages, null, null)) &&
                store.LoadPluginDocument("daily-core-progress-v1", "draft") == saved,
                "修复不得替换合格字段或丢弃已保存草稿");
            var resumed = new AgentSequenceLlm(Repair("后来共同补齐安排"));
            Await(Method("CompleteObservationAsync"), context, resumed, source, "draft", state, messages, null, null);
            using (var json = JsonDocument.Parse(store.LoadPluginDocument("daily-core-progress-v1", "draft")))
                Require(resumed.Requests.Count == 1 && json.RootElement.GetProperty("Output").GetProperty("event_writes")[0]
                    .GetProperty("entry_summary").GetString() == new string('好', 80) &&
                    json.RootElement.GetProperty("Output").GetProperty("event_appends")[0].GetProperty("index_alias").GetString() == "unchanged-id",
                    "从草稿恢复只调用一次短句模型，80字边界、别名及其他内容原样保留");

            sources[0].Role = "system_event";
            using (var db = new SQLite.SQLiteConnection(path)) db.Update(sources[0]);
            var stale = new AgentSequenceLlm();
            Require(Rejected(() => Await(Method("CompleteObservationAsync"), context, stale, source, "draft", state, messages, null, null)) &&
                stale.Requests.Count == 0, "来源角色变动阻止复用草稿且不消耗模型");

            // Real pipeline, interruption after one committed observation, then process restart.
            const string day = "2020-08-11";
            Seed(day, 19);
            var interrupted = new AgentSequenceLlm(Event("首批共同核对安排", day + "-0"));
            Require(Rejected(() => Run(day, interrupted)), "第二事件批失败应停止当日流程");
            using (var db = new SQLite.SQLiteConnection(path))
                Require(db.Table<EventEntryRecord>().Count() == 1 && db.Table<LifeTagRecord>().Count() > 0,
                    "中途失败保留首批事件和标签");
            var tagCounts = store.GetActiveLifeTags().ToDictionary(x => x.Id, x => x.ActivationCount);
            context.Dispose(); context = null; Open();
            var next = new AgentSequenceLlm(Event("末批补齐安排", day + "-18"), "{\"detail\":\"测试细节原文\"}",
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
            var firstCognition = new AgentSequenceLlm(cogOutput, ConsolidationNewReply("我会共同核对计划"));
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
            // Event-specific evidence and prose budgets are exercised through the actual migration assembly.
            var replay = assembly.GetType("TraceSoul2.Migrate.ReplayPrompts", true);
            var eventType = assembly.GetType("TraceSoul2.Migrate.ReplayPrompts+DayEventOutputData", true);
            var probeSources = Seed("2020-08-15", 2);
            probeSources[0].Content = "只属于饮水事件的原句";
            probeSources[1].Content = "只属于修电脑事件的原句";
            string Structure(string raw) => (string)Method("ObservationStructureError").Invoke(null, new object[] {
                JsonSerializer.Deserialize(raw, eventType, new JsonSerializerOptions { IncludeFields = true }), probeSources, new List<EventIndexRecord>() });
            Require(Structure(Event("只记饮水", probeSources[0].Id)) == null &&
                Structure(Event("引用批外来源", "foreign")) != null &&
                Structure("{\"perception_summary\":\"测试\",\"event_writes\":[{\"event_summary\":\"一件事\",\"entry_summary\":\"一件事\"}]}") != null,
                "新事件必须逐项引用本批原句，缺失和批外引用均拒绝");
            var selectedSources = (List<MomentRecord>)Method("ResolveEventSources").Invoke(null,
                new object[] { new List<string> { probeSources[0].Id }, probeSources });
            var detailPrompt = (string)replay.GetMethod("BuildDetailPrompt").Invoke(null, new object[] {
                store.LoadPairIdentity(), "人格", "档案", selectedSources, "饮水", "她倒了一杯水", 0, 200 });
            Require(detailPrompt.Contains(probeSources[0].Content) && !detailPrompt.Contains(probeSources[1].Content),
                "细节只拿这一件事的原句，同批其他事件不扩散到每条正文");
            var detailMessages = new List<DeepSeekMessageData> { new("system", detailPrompt), new("user", "写这件事") };
            var longDetail = new AgentSequenceLlm(TraceJson.ToJson(new { detail = new string('旧', 3000) }),
                TraceJson.ToJson(new { detail = new string('长', 201) }));
            Require(Rejected(() => Await(Method("CompleteDetailAsync"), context, longDetail, "bounded-detail", "stamp", detailMessages,
                System.Threading.CancellationToken.None)) && longDetail.Requests.Count == 2 &&
                !longDetail.Requests[1].Contains(new string('旧', 3000)) && !longDetail.Requests[1].Contains(probeSources[1].Content),
                "超长细节只从原句重写一次，不回传长草稿、不截断落库");
            var repairedDetail = new AgentSequenceLlm("{\"detail\":\"她给自己倒了水，我松了一口气。\"}");
            Await(Method("CompleteDetailAsync"), context, repairedDetail, "bounded-detail", "stamp", detailMessages,
                System.Threading.CancellationToken.None);
            Require(repairedDetail.Requests.Count == 1 && repairedDetail.Requests[0].Contains("重新取舍"),
                "细节草稿失败后恢复只补这一项，不从最初生成再循环");
            var cachedDetail = new AgentSequenceLlm();
            Await(Method("CompleteDetailAsync"), context, cachedDetail, "bounded-detail", "stamp", detailMessages,
                System.Threading.CancellationToken.None);
            Require(cachedDetail.Requests.Count == 0, "已合格细节草稿再次恢复零请求");
            // Actual multi-threaded completions reproduce the shared prepared-insert race.
            var detailMethod = Method("CompleteDetailAsync");
            var logType = assembly.GetType("TraceSoul2.Migrate.ReplayCallLogRecord", true);
            var parallelTasks = Enumerable.Range(0, 64).Select(i => Task.Run(() => {
                var body = "并发细节" + i;
                Await(detailMethod, context, new AgentSequenceLlm(TraceJson.ToJson(new { detail = body })),
                    "parallel-detail-" + i, "parallel-source", detailMessages, System.Threading.CancellationToken.None);
                var record = Activator.CreateInstance(logType);
                logType.GetProperty("Id").SetValue(record, "parallel-" + i);
                logType.GetProperty("DayKey").SetValue(record, "2020-08-17");
                logType.GetProperty("OutputJson").SetValue(record, body);
                migrationType.GetMethod("SaveCallLog").Invoke(migration, new[] { record });
            })).ToArray();
            Task.WhenAll(parallelTasks).GetAwaiter().GetResult();
            Require(Enumerable.Range(0, 64).All(i => {
                using var savedDraft = JsonDocument.Parse(store.LoadPluginDocument("daily-core-progress-v1", "parallel-detail-" + i + ":draft"));
                return savedDraft.RootElement.GetProperty("Body").GetString() == "并发细节" + i;
            }), "64项真正并发完成时草稿各自完整保存，无SQLite共享语句冲突");
            var parallelLogs = (System.Collections.IEnumerable)migrationType.GetMethod("GetCallLogs").Invoke(migration, new object[] { "2020-08-17" });
            Require(parallelLogs.Cast<object>().Count() == 64, "并发细节调用留痕不丢失、不串写");

            // One refusal keeps the validated event; its sibling and subsequent recovery still proceed.
            const string refusalDay = "2020-08-18";
            var refusalSources = Seed(refusalDay, 2);
            var twoEvents = TraceJson.ToJson(new { perception_summary = "两件测试事件", new_tags = new[] { new {
                name = "计划核对", definition = "共同检查计划", domain_ids = new[] { "relation" }, dimension_ids = new[] { "predicate" } } },
                event_writes = refusalSources.Select((sourceMoment, i) => new { event_focus = "独立测试事件" + i,
                    source_moment_ids = new[] { sourceMoment.Id }, event_summary = "测试事件" + i,
                    entry_summary = "当时记录的事件" + i, new_tag_names = new[] { "计划核对" } }).ToArray() });
            var refusalRun = new AgentSequenceLlm(twoEvents, "抱歉，我无法满足这个请求。", "{\"detail\":\"同批正常细节\"}");
            Require(Rejected(() => Run(refusalDay, refusalRun)), "模拟内容兜底完成后在认知阶段中断");
            using (var db = new SQLite.SQLiteConnection(path))
            {
                var entries = db.Table<EventEntryRecord>().ToList().Where(x => refusalSources.Any(m => m.Id == x.SourceMomentId)).ToList();
                Require(entries.Count == 2 && entries.Any(x => x.Detail == x.Summary) && entries.Any(x => x.Detail == "同批正常细节") &&
                    entries.All(x => !x.Detail.Contains("抱歉")), "拒绝仅沿用该事件短句，同批正常细节继续，拒绝话术不写入记忆");
            }
            context.Dispose(); context = null; Open();
            var refusalResume = new AgentSequenceLlm();
            Require(Rejected(() => Run(refusalDay, refusalResume)) && refusalResume.Requests.Count == 1 &&
                refusalResume.Requests[0].Contains("cognitions"), "重启后复用已保存事件和拒绝兜底，首次调用直接进入认知");
            var refusalJson = new AgentSequenceLlm("{\"detail\":\"抱歉，我无法满足这个请求。\"}");
            Require(Rejected(() => Await(detailMethod, context, refusalJson, "refused-draft", "refusal-stamp", detailMessages,
                System.Threading.CancellationToken.None)) && refusalJson.Requests.Count == 1, "JSON细节字段内的拒绝不被当成有效记忆或反复纠正");
            var refusedNoCalls = new AgentSequenceLlm();
            Require(Rejected(() => Await(detailMethod, context, refusedNoCalls, "refused-draft", "refusal-stamp", detailMessages,
                System.Threading.CancellationToken.None)) && refusedNoCalls.Requests.Count == 0, "提交前中断后复用已保存拒绝标记，零请求");
            Require(Rejected(() => Await(detailMethod, context, refusedNoCalls, "refused-draft", "changed-stamp", detailMessages,
                System.Threading.CancellationToken.None)), "拒绝兜底仍校验来源版本");
            var malformedDetail = new AgentSequenceLlm("not json", "still not json");
            Require(Rejected(() => Await(detailMethod, context, malformedDetail, "malformed-detail", "stamp", detailMessages,
                System.Threading.CancellationToken.None)) && string.IsNullOrEmpty(store.LoadPluginDocument("daily-core-progress-v1", "malformed-detail:draft")),
                "两次坏JSON仍停止，不伪造拒绝或完成检查点");
            using (var cancelled = new System.Threading.CancellationTokenSource())
            {
                cancelled.Cancel();
                var wasCancelled = false;
                try { Await(detailMethod, context, new AgentSequenceLlm("抱歉，我无法满足这个请求。"), "cancelled-refusal", "stamp", detailMessages, cancelled.Token); }
                catch (OperationCanceledException) { wasCancelled = true; }
                Require(wasCancelled && string.IsNullOrEmpty(store.LoadPluginDocument("daily-core-progress-v1", "cancelled-refusal:draft")),
                    "取消优先于内容兜底，不把取消请求保存成拒绝");
            }
            // The comparison can fail after proposal persistence; restart must not re-form that proposal.
            const string comparisonDay = "2020-08-19";
            var comparisonSources = Seed(comparisonDay, 1);
            var comparisonSource = Source(comparisonDay, comparisonSources);
            var legacyNode = store.CommitCognitions(comparisonSources[0].Id, new[] { new BrainCognitionWriteData {
                operation = "create", summary = "一起核对计划与饮水的测试经历形成旧认识", domains = new() { "relation" },
                identity_slot = "", evidence_moment_ids = new() { comparisonSources[0].Id }, confidence = .7f, strength = .5f } }).Single();
            const string comparisonSummary = "我愿意和对方共同核对安排，并说明关系中的边界";
            var comparisonProposal = TraceJson.ToJson(new { cognitions = new[] { new { operation = "create", summary = comparisonSummary,
                domains = new[] { "relation" }, identity_slot = "relation", evidence_moment_ids = new[] { comparisonSources[0].Id }, confidence = .7, strength = .5 } } });
            var wrongPurpose = TraceJson.ToJson(new { decisions = new[] { new { index = 0, kind = "revise", target_id = legacyNode.Id,
                before = legacyNode.Summary, after = comparisonSummary, explanation = "测试故意引用空用途目标" } } });
            var compareFailure = new AgentSequenceLlm(comparisonProposal, wrongPurpose, wrongPurpose);
            var compareCount = storeType.GetMethod("CountCognitions").Invoke(actualStore, null);
            Require(Rejected(() => Await(formation, context, store.LoadPairIdentity(), compareFailure, comparisonDay,
                new List<EventIndexRecord>(), new List<EventEntryRecord>(), comparisonSources, comparisonSource)) && compareFailure.Requests.Count == 3 &&
                Equals(compareCount, storeType.GetMethod("CountCognitions").Invoke(actualStore, null)),
                "实际Migration先存候选，比较两次跨用途失败仍零认知写入");
            context.Dispose(); context = null; Open();
            var compareResume = new AgentSequenceLlm(ConsolidationNewReply(comparisonSummary));
            Await(formation, context, store.LoadPairIdentity(), compareResume, comparisonDay,
                new List<EventIndexRecord>(), new List<EventEntryRecord>(), comparisonSources, comparisonSource);
            Require(compareResume.Requests.Count == 1 && compareResume.Requests[0].Contains("认知写入前比较") &&
                !Equals(compareCount, storeType.GetMethod("CountCognitions").Invoke(actualStore, null)) &&
                ((ICognitionGraphStore)store).GetCognitionNodes(5000).Single(x => x.Id == legacyNode.Id).IdentitySlot == "",
                "跨实例续做复用候选，仅比较一次并提交新认识，不改旧空用途节点");
            var completedCompare = new AgentSequenceLlm();
            Await(formation, context, store.LoadPairIdentity(), completedCompare, comparisonDay,
                new List<EventIndexRecord>(), new List<EventEntryRecord>(), comparisonSources, comparisonSource);
            Require(completedCompare.Requests.Count == 0, "已提交认知检查点重复续做零调用");
            var ladder = assembly.GetType("TraceSoul2.Migrate.DayLadderLogic", true);
            var rankType = assembly.GetType("TraceSoul2.Migrate.LadderRankOutputData", true);
            string RankError(string raw) => (string)ladder.GetMethod("RankError", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null,
                new object[] { JsonSerializer.Deserialize(raw, rankType, new JsonSerializerOptions { IncludeFields = true }), 3, 3 });
            Require(RankError("{\"items\":[{\"index_alias\":\"i1\"},{\"index_alias\":\"i1\"}]}") != null &&
                RankError("{\"items\":[{\"index_alias\":\"foreign\"}]}") != null &&
                RankError(TraceJson.ToJson(new { items = new[] { new { index_alias = "i1", reason = new string('长', 61) } } })) != null,
                "榜单拒绝重复/未知指针和超长原因，不静默重复上榜或截断条件");
            var rankCandidates = Enumerable.Range(0, 150).Select(i => new CognitionSliceRecord {
                Id = "rank-node-" + i, Summary = "独立认识" + i + "：" + new string('条', 85) + "；仅在明确约定时成立，拒绝时不适用。",
                CreatedUnixMs = i }).ToList();
            var seenRankCandidates = new HashSet<string>();
            var rankProbe = new SelectionProbeLlm { Reply = request => {
                if (request[0].content.Contains("【复盘候选入围】"))
                {
                    using var data = JsonDocument.Parse(request[1].content);
                    foreach (var item in data.RootElement.EnumerateArray()) seenRankCandidates.Add(item.GetProperty("description").GetString());
                    return Pick(data.RootElement[data.RootElement.GetArrayLength() - 1].GetProperty("id").GetString());
                }
                var alias = request[0].content.Split('\n').Where(x => x.StartsWith("i") && x.Contains(" | ")).Last().Split(' ')[0];
                return TraceJson.ToJson(new { items = new[] { new { index_alias = alias, reason = "改变了对条件的理解" } } });
            } };
            var rankTask = (Task)ladder.GetMethod("RankDayCognitionsAsync").Invoke(null,
                new object[] { context, store.LoadPairIdentity(), "2020-08-16", rankCandidates, rankProbe });
            rankTask.GetAwaiter().GetResult();
            var rankResult = (List<LadderItemRecord>)rankTask.GetType().GetProperty("Result").GetValue(rankTask);
            Require(seenRankCandidates.Count == 150 && rankResult.Single().RefId == "rank-node-149" &&
                rankResult[0].Label == rankCandidates[^1].Summary && rankProbe.Requests.All(x => x.Length < 9000),
                "实际日榜150项全部参与分批比较，末项也可入围，长认识的条件不被80字截掉，模型输入有界");
            Require(Rejected(() => builder.GetMethod("DedupeCognitions").Invoke(null, new object[] { context })),
                "旧文字相似度去重入口停用，不能把不同条件或反证合掉来掩盖累积");
            Console.WriteLine("Daily core progress checks passed: targeted drafts, restart, atomic writes, detail and cognition resume, source and legacy guards.");
        }
        finally { context?.Dispose(); Directory.Delete(directory, true); }
    }
}
