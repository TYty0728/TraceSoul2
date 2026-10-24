using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Logic;
using TraceSoul2.Manager;
using TraceSoul2.Plugins;
using TraceSoul2.Util;

internal static partial class Program
{
    private static string ConsolidationNewReply(string summary) => TraceJson.ToJson(new {
        decisions = new[] { new { index = 0, kind = "new", target_id = "", before = "", after = summary, explanation = "本项是独立的新认识" } } });
    private static string IdentityChangeReply(string id, bool changed = true) => TraceJson.ToJson(new {
        basis_ids = new[] { id }, effect = changed ? "changed" : "unchanged", reason = changed ? "认识及适用边界发生变化" : "同一认识的新印证，正文仍然贴切",
        deltas = changed ? new object[] { new { kind = "revise", before = "旧理解", after = "当前理解及边界", cognition_ids = new[] { id } } } : Array.Empty<object>() });
    private sealed class AllSemanticEncoder : IVectorEncoder
    {
        public string ModelId => "controlled-semantic-check";
        public int Dimensions => 2;
        public float[] Encode(string text, VectorTextPurpose purpose) => new float[] { 1, 0 };
    }
    private static async Task RunIdentityEvolutionChecksAsync()
    {
        await RunCognitionComparisonChecksAsync();
        var directory = Path.Combine(Path.GetTempPath(), "identity-evolution-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var store = new SqliteMemoryManager(Path.Combine(directory, "brain.db"));
            const string root = "tracesoul2";
            store.SavePairIdentity("本人", "同伴", "称呼"); store.LoadIdentityCards(root);
            MomentRecord Source(int day)
            {
                var m = new MomentRecord { Id = "day-" + day, ConversationId = root, Role = "user", Content = "一次真实的独立印证", CreatedUnixMs = day + 1 };
                store.SaveMoment(m); return m;
            }
            BrainCognitionWriteData Write(MomentRecord m, string text) => new() { operation = "create", summary = text,
                domains = new() { "relation" }, identity_slot = "relation", about = "两人的关系", scope = "双方可以回应时", exceptions = "忙碌时需要说明",
                evidence_moment_ids = new() { m.Id }, confidence = .7f, strength = .5f };
            var first = Source(0);
            var node = store.CommitCognitions(first.Id, new[] { Write(first, "我们能够在短暂分开后恢复联系") }).Single();
            var formation = new AgentSequenceLlm(ReorganizationAssessment(node.Id), IdentityChangeReply(node.Id), ReorganizationProposal(node.Id));
            var state = await IdentityEvolutionLogic.EvaluateAsync(store, root, "relation", "2020-01-01", formation, CancellationToken.None);
            IdentityEvolutionLogic.Commit(store, root, "relation", state);
            var card = store.LoadIdentityCards(root).Single(x => x.Slot == "relation");
            var initialBody = card.Body; var initialRevision = card.Revision;
            Require(formation.Requests.Count == 3 && formation.Messages[2].All(x => !x.content.Contains(initialBody)), "只从认识成稿，不把旧正文递归输入写作");
            for (var day = 1; day <= 30; day++)
            {
                var m = Source(day);
                store.CommitCognitions(m.Id, new[] { new BrainCognitionWriteData { operation = "reinforce", target_id = node.Id,
                    evidence_moment_ids = new() { m.Id }, confidence = .8f } });
                Require(IdentityProjectionLogic.Current(store.LoadIdentityCards(root).Single(x => x.Slot == "relation"), store.GetCognitionNodes(5000)),
                    "新证据不使未改变含义的身份依据失效");
                var support = new AgentSequenceLlm(IdentityChangeReply(node.Id, false));
                state = await IdentityEvolutionLogic.EvaluateAsync(store, root, "relation", "2020-01-" + (day + 1).ToString("00"), support, CancellationToken.None);
                IdentityEvolutionLogic.Commit(store, root, "relation", state);
                var current = store.LoadIdentityCards(root).Single(x => x.Slot == "relation");
                Require(support.Requests.Count == 1 && support.Requests[0].Contains("变化判断") && current.Body == initialBody && current.Revision == initialRevision,
                    "连续30次独立印证只评估变化，正文和版本不增长，不重新归并或成稿");
            }
            Require(store.GetCognitionNodes(5000).Count == 1 && store.ReadIdentityReorganizationEvidence(new[] { node.Id }).Count == 31,
                "长期重复印证保留31条证据，认识仍为一个节点");
            var noCalls = new AgentSequenceLlm();
            Require(await IdentityEvolutionLogic.EvaluateAsync(store, root, "relation", "2020-02-02", noCalls, CancellationToken.None) == null && noCalls.Requests.Count == 0,
                "认识与依据都未变时不调用身份模型");

            // A card already invalidated by the old revision stamp is rebound only after an explicit semantic no-change decision.
            using (var db = new SQLite.SQLiteConnection(Path.Combine(directory, "brain.db")))
            {
                var legacy = store.LoadIdentityCards(root).Single(x => x.Slot == "relation");
                legacy.CognitionSourcesJson = TraceJson.ToJson(new Dictionary<string, string> { [node.Id] = "obsolete-exact-source-revision" });
                db.Update(legacy);
            }
            var rebind = new AgentSequenceLlm(IdentityChangeReply(node.Id, false));
            state = await IdentityEvolutionLogic.EvaluateAsync(store, root, "relation", "2020-02-02", rebind, CancellationToken.None);
            IdentityEvolutionLogic.Commit(store, root, "relation", state);
            Require(IdentityProjectionLogic.Current(store.LoadIdentityCards(root).Single(x => x.Slot == "relation"), store.GetCognitionNodes(5000)) &&
                store.LoadIdentityCards(root).Single(x => x.Slot == "relation").Body == initialBody,
                "旧版本号导致的过时卡经语义确认后只更新依据，不要求重写正文");

            var duplicateSource = Source(31);
            var proposal = Write(duplicateSource, "分开一会儿后我们仍能重新接上交流");
            var same = new AgentSequenceLlm(TraceJson.ToJson(new { decisions = new[] { new { index = 0, kind = "same", target_id = node.Id,
                before = node.Summary, after = node.Summary, explanation = "含义及条件一致，只换了表达" } } }));
            var resolved = await CognitionConsolidationLogic.ResolveAsync(store, same, new[] { duplicateSource }, Array.Empty<CognitionSliceRecord>(),
                new List<BrainCognitionWriteData> { proposal }, () => new AllSemanticEncoder(), null, CancellationToken.None);
            store.CommitCognitions(duplicateSource.Id, resolved.Writes);
            Require(resolved.Writes.Single().operation == "reinforce" && store.GetCognitionNodes(5000).Count == 1,
                "以候选理解二次召回旧节点，同义create转为原节点证据加强");

            var reviseSource = Source(32); var revision = Write(reviseSource, "我们能恢复交流，同时需要提前说明忙碌的边界");
            revision.operation = "revise"; revision.target_id = node.Id; revision.scope = "忙碌前说明可联系时间";
            var updated = store.CommitCognitions(reviseSource.Id, new[] { revision }).Single();
            Require(!IdentityProjectionLogic.Current(card, store.GetCognitionNodes(5000)), "真实条件修订使旧身份投影失效");
            var revisedLlm = new AgentSequenceLlm(ReorganizationAssessment(updated.Id), IdentityChangeReply(updated.Id), ReorganizationProposal(updated.Id));
            state = await IdentityEvolutionLogic.EvaluateAsync(store, root, "relation", "2020-02-03", revisedLlm, CancellationToken.None);
            IdentityEvolutionLogic.Commit(store, root, "relation", state);
            Require(IdentityProjectionLogic.Current(store.LoadIdentityCards(root).Single(x => x.Slot == "relation"), store.GetCognitionNodes(5000)) &&
                store.GetCognitionNodes(5000).Single(x => x.Id == node.Id).Status == "superseded", "修订后的认识形成新投影，旧版和证据保留");

            var challenge = Source(33);
            store.CommitCognitions(challenge.Id, new[] { new BrainCognitionWriteData { operation = "weaken", target_id = updated.Id,
                evidence_moment_ids = new() { challenge.Id }, confidence = .3f } });
            var currentCard = store.LoadIdentityCards(root).Single(x => x.Slot == "relation");
            Require(!IdentityProjectionLogic.Current(currentCard, store.GetCognitionNodes(5000)), "反证削弱后不继续注入旧的确定结论");
            var weakNoChange = new AgentSequenceLlm(ReorganizationAssessment(updated.Id), IdentityChangeReply(updated.Id, false));
            var weakRejected = false;
            try { await IdentityEvolutionLogic.EvaluateAsync(store, root, "relation", "2020-02-03-counter", weakNoChange, CancellationToken.None); }
            catch (InvalidOperationException) { weakRejected = true; }
            Require(weakRejected && !IdentityProjectionLogic.Current(currentCard, store.GetCognitionNodes(5000)),
                "受反证削弱的认识不能通过unchanged恢复旧确定结论");
            var uncertainBody = TraceJson.ToJson(new { body = "我们对分开后恢复联系的信任仍在调整，需要进一步沟通和新的印证。",
                change = "反证使确定程度下降", understanding_indices = new[] { 0 }, cognition_ids = new[] { updated.Id } });
            var uncertain = new AgentSequenceLlm(ReorganizationAssessment(updated.Id), IdentityChangeReply(updated.Id), uncertainBody);
            state = await IdentityEvolutionLogic.EvaluateAsync(store, root, "relation", "2020-02-03-counter-new", uncertain, CancellationToken.None);
            IdentityEvolutionLogic.Commit(store, root, "relation", state);
            currentCard = store.LoadIdentityCards(root).Single(x => x.Slot == "relation");
            Require(IdentityProjectionLogic.Current(currentCard, store.GetCognitionNodes(5000)) && currentCard.Body.Contains("仍在调整"),
                "反证后的开放认识可以有依据地接回身份，而非丢掉整个关系认识");
            var recovery = Source(34);
            store.CommitCognitions(recovery.Id, new[] { new BrainCognitionWriteData { operation = "reinforce", target_id = updated.Id,
                evidence_moment_ids = new() { recovery.Id }, confidence = .7f } });
            Require(!IdentityProjectionLogic.Current(currentCard, store.GetCognitionNodes(5000)), "后续加强不能抹掉曾出现的反证而自动复活旧投影");

            // Detail classification affects the identity reading view, while original nodes remain queryable.
            using (var db = new SQLite.SQLiteConnection(Path.Combine(directory, "brain.db")))
                for (var i = 0; i < 50; i++)
                {
                    db.Insert(new CognitionSliceRecord { Id = "detail-" + i, Status = "active", IdentitySlot = "relation", Summary = "DETAIL_SENTINEL_" + i,
                        ContextConversationId = root, MemoryVisibility = "private" });
                    db.Insert(new CognitionEvidenceRecord { Id = "detail-e-" + i, CognitionId = "detail-" + i, MomentId = first.Id, Relation = "supports" });
                }
            using (var db = new SQLite.SQLiteConnection(Path.Combine(directory, "brain.db")))
            {
                db.Insert(new CognitionSliceRecord { Id = "historical-paraphrase", Status = "active", IdentitySlot = "relation", Summary = "DUPLICATE_IDENTITY_SENTINEL",
                    ContextConversationId = root, MemoryVisibility = "private" });
                db.Insert(new CognitionEvidenceRecord { Id = "old-paraphrase-e", CognitionId = "historical-paraphrase", MomentId = first.Id, Relation = "supports" });
            }
            var seen = new HashSet<string>();
            var classification = new SelectionProbeLlm { Reply = messages => {
                using var json = System.Text.Json.JsonDocument.Parse(messages.Last().content);
                if (messages[0].content.Contains("全局归并"))
                    return TraceJson.ToJson(new { groups = new[] { json.RootElement.GetProperty("understandings").EnumerateArray()
                        .Select(x => x.GetProperty("index").GetInt32()).ToArray() } });
                if (messages[0].content.Contains("单组认识成稿"))
                    return TraceJson.ToJson(new { meaning = "当前关系理解", scope = "必要条件", exceptions = "反证边界", certainty = "开放" });
                if (messages[0].content.Contains("认识归并"))
                {
                    var input = json.RootElement.GetProperty("cognitions").EnumerateArray().Select(x => x.GetProperty("Id").GetString()).ToList();
                    foreach (var id in input) seen.Add(id);
                    return TraceJson.ToJson(new {
                        understandings = input.Any(id => id == updated.Id || id == "historical-paraphrase") ? new[] { new { meaning = "当前关系理解", scope = "必要条件", exceptions = "反证边界", certainty = "开放", cognition_ids = input.Where(id => id == updated.Id || id == "historical-paraphrase").ToArray() } } : Array.Empty<object>(),
                        details = input.Where(id => id != updated.Id && id != "historical-paraphrase").Select(id => new { id, reason = "只记录一次经历" }).ToList() });
                }
                return IdentityChangeReply(updated.Id, false);
            } };
            state = await IdentityEvolutionLogic.EvaluateAsync(store, root, "relation", "2020-02-04", classification, CancellationToken.None);
            IdentityEvolutionLogic.Commit(store, root, "relation", state);
            var services = new TracePluginServices(store, new HierarchicalVectorRouterLogic(new FakeEncoder()));
            var turn = new TraceTurnContext(root, first, new(), 0, true, services,
                environment: new EnvironmentSnapshotData { RootConversationId = root, ContextConversationId = root, Visibility = "private" });
            Require(seen.Contains("detail-49") && !IdentityProjectionLogic.Build(turn).Contains("DETAIL_SENTINEL_") && !IdentityProjectionLogic.Build(turn).Contains("DUPLICATE_IDENTITY_SENTINEL") &&
                store.GetCognitionNodes(5000).Any(x => x.Summary == "DETAIL_SENTINEL_49"),
                "完整处理40条以外的材料，已识别细节不再绕过身份卡注入，原认知仍可查证");

            var beforeBulk = store.LoadIdentityCards(root).Single(x => x.Slot == "relation");
            using (var db = new SQLite.SQLiteConnection(Path.Combine(directory, "brain.db")))
                db.RunInTransaction(() => {
                    for (var i = 0; i < 1500; i++)
                    {
                        db.Insert(new MomentRecord { Id = "bulk-proof-" + i, ConversationId = root, Role = "user", Content = "独立的后续印证", CreatedUnixMs = 10000 + i });
                        db.Insert(new CognitionEvidenceRecord { Id = "bulk-e-" + i, CognitionId = updated.Id, MomentId = "bulk-proof-" + i, Relation = "supports" });
                    }
                });
            var bulk = new AgentSequenceLlm(IdentityChangeReply(updated.Id, false));
            state = await IdentityEvolutionLogic.EvaluateAsync(store, root, "relation", "2020-03-01", bulk, CancellationToken.None);
            IdentityEvolutionLogic.Commit(store, root, "relation", state);
            Require(bulk.Requests.Count == 1 && bulk.Requests[0].Length < 12000 && !bulk.Requests[0].Contains("bulk-proof-") &&
                bulk.Requests[0].Contains("evidence_summary") && store.ReadIdentityReorganizationEvidence(new[] { updated.Id }).Count >= 1500 &&
                store.LoadIdentityCards(root).Single(x => x.Slot == "relation").Body == beforeBulk.Body,
                "1500条证据保留在库，模型只读有界证据摘要，身份正文不随历史证据增长");

            var malformed = new AgentSequenceLlm("{\"decisions\":[]}"); var rejected = false;
            try { await CognitionConsolidationLogic.ResolveAsync(store, malformed, new[] { Source(35) }, new[] { updated },
                new List<BrainCognitionWriteData> { proposal }, () => new AllSemanticEncoder(), null, CancellationToken.None); }
            catch (InvalidOperationException) { rejected = true; }
            Require(rejected, "语义比较遗漏候选时不能提交原create兜底");
        }
        finally { Directory.Delete(directory, true); }
        await RunIdentityGlobalGroupingChecksAsync();
        Console.WriteLine("Identity evolution passed: 30-day stability, growing evidence, paraphrase consolidation, real revision, counterevidence and no stale revival.");
    }

    private static async Task RunIdentityGlobalGroupingChecksAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "identity-global-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "brain.db");
        const string root = "tracesoul2", day = "2020-04-01", key = day + ":" + root + ":relation";
        const int count = 160;
        var originalBody = "";
        try
        {
            using (var store = new SqliteMemoryManager(path))
            {
                store.SavePairIdentity("本人", "同伴", "称呼");
                originalBody = store.LoadIdentityCards(root).Single(x => x.Slot == "relation").Body;
                using (var db = new SQLite.SQLiteConnection(path))
                    db.RunInTransaction(() => {
                        for (var i = 0; i < count; i++)
                            db.Insert(new CognitionSliceRecord { Id = "global-source-" + i.ToString("000"), Status = "active", IdentitySlot = "relation",
                                Summary = "独立认识" + i + new string('知', 350), Scope = "双方能够回应时", Exceptions = "有反证时需保留边界",
                                ContextConversationId = root, MemoryVisibility = "private" });
                    });
                var batchCalls = 0;
                var legacy = new SelectionProbeLlm { Reply = messages => {
                    if (messages[0].content.Contains("全局归并")) throw new IOException("模拟旧版全局输出失败，保留全部分批进度");
                    batchCalls++;
                    using var json = System.Text.Json.JsonDocument.Parse(messages.Last().content);
                    return TraceJson.ToJson(new { understandings = json.RootElement.GetProperty("cognitions").EnumerateArray().Select(x => new {
                        meaning = x.GetProperty("Summary").GetString(), scope = x.GetProperty("Scope").GetString(),
                        exceptions = x.GetProperty("Exceptions").GetString(), certainty = "有条件成立", cognition_ids = new[] { x.GetProperty("Id").GetString() }
                    }).ToArray(), details = Array.Empty<object>() });
                } };
                try { await IdentityEvolutionLogic.EvaluateAsync(store, root, "relation", day, legacy, CancellationToken.None); }
                catch (IOException) { }
                var saved = PuzzleViewLogic.Read<IdentityEvolutionLogic.State>(store.LoadPluginDocument(IdentityEvolutionLogic.StoreId, key));
                Require(batchCalls > 1 && saved.batchesDone == batchCalls && saved.understandings.Count == count && !saved.globallyGrouped,
                    "160项认识在全局失败前逐批保存，能够模拟旧版已有进度");
                var oldJson = System.Text.Json.Nodes.JsonNode.Parse(TraceJson.ToJson(saved)).AsObject();
                oldJson.Remove("globalPlan"); oldJson.Remove("globalResults");
                store.SavePluginDocument(IdentityEvolutionLogic.StoreId, key, oldJson.ToJsonString());

                var validGroups = new List<List<int>> { new() { 0, 80 }, new() { 1, 81 } };
                validGroups.AddRange(Enumerable.Range(0, count).Except(new[] { 0, 1, 80, 81 }).Select(x => new List<int> { x }));
                var invalidPlans = new[] {
                    "{\"groups\":[[0",
                    TraceJson.ToJson(new { groups = new[] { Enumerable.Range(0, count - 1).ToArray() } }),
                    TraceJson.ToJson(new { groups = new[] { Enumerable.Range(0, count - 1).Append(0).ToArray() } }),
                    TraceJson.ToJson(new { groups = new[] { Enumerable.Range(0, count - 1).Append(count).ToArray() } })
                };
                foreach (var reply in invalidPlans)
                {
                    var invalid = new AgentSequenceLlm(reply, reply); var rejected = false;
                    try { await IdentityEvolutionLogic.EvaluateAsync(store, root, "relation", day, invalid, CancellationToken.None); }
                    catch (InvalidOperationException) { rejected = true; }
                    saved = PuzzleViewLogic.Read<IdentityEvolutionLogic.State>(store.LoadPluginDocument(IdentityEvolutionLogic.StoreId, key));
                    Require(rejected && invalid.Requests.Count == 2 && invalid.Requests.All(x => x.Contains("全局归并")) &&
                        saved.batchesDone == batchCalls && saved.globalPlan == null && saved.understandings.Count == count,
                        "截断、遗漏、重复和越界编号最多纠正一次，不写坏计划或重做分批");
                }
                var merge = TraceJson.ToJson(new { meaning = "跨批同义认识归为一个当前理解", scope = "双方可以回应", exceptions = "忙碌时保留边界", certainty = "开放" });
                var overlong = TraceJson.ToJson(new { meaning = "OVERSIZED_MERGE_SENTINEL" + new string('长', 1400), scope = "条件", exceptions = "反证", certainty = "开放" });
                var interrupted = new AgentSequenceLlm(TraceJson.ToJson(new { groups = validGroups }), merge, overlong, overlong);
                var failed = false;
                try { await IdentityEvolutionLogic.EvaluateAsync(store, root, "relation", day, interrupted, CancellationToken.None); }
                catch (InvalidOperationException) { failed = true; }
                saved = PuzzleViewLogic.Read<IdentityEvolutionLogic.State>(store.LoadPluginDocument(IdentityEvolutionLogic.StoreId, key));
                Require(failed && interrupted.Requests.Count == 4 && saved.globalPlan.groups.Count == count - 2 && saved.globalResults.Count == 1 &&
                    saved.globalResults[0].cognition_ids.SequenceEqual(new[] { "global-source-000", "global-source-080" }) &&
                    !interrupted.Requests[0].Contains("global-source-") && !interrupted.Requests[3].Contains("OVERSIZED_MERGE_SENTINEL") &&
                    !saved.globallyGrouped && store.LoadIdentityCards(root).Single(x => x.Slot == "relation").Body == originalBody,
                    "全局仅返回编号；仅合并项成稿，来源由程序完整带回；单组失败保留计划和已成稿项，纠正不携带长草稿");
            }
            using (var store = new SqliteMemoryManager(path))
            {
                var original = store.GetCognitionNodes(5000).Single(x => x.Id == "global-source-081").Summary;
                var concurrent = new SelectionProbeLlm { Reply = _ => {
                    using var db = new SQLite.SQLiteConnection(path);
                    db.Execute("UPDATE cognition_slices SET Summary=? WHERE Id=?", "生成期间认识已变化", "global-source-081");
                    return TraceJson.ToJson(new { meaning = "并发期间的旧候选", scope = "条件", exceptions = "反证", certainty = "开放" });
                } };
                var staleRejected = false;
                try { await IdentityEvolutionLogic.EvaluateAsync(store, root, "relation", day, concurrent, CancellationToken.None); }
                catch (InvalidOperationException) { staleRejected = true; }
                using (var db = new SQLite.SQLiteConnection(path))
                    db.Execute("UPDATE cognition_slices SET Summary=? WHERE Id=?", original, "global-source-081");
                var protectedState = PuzzleViewLogic.Read<IdentityEvolutionLogic.State>(store.LoadPluginDocument(IdentityEvolutionLogic.StoreId, key));
                Require(staleRejected && concurrent.Requests.Count == 1 && protectedState.globalResults.Count == 1 &&
                    store.LoadIdentityCards(root).Single(x => x.Slot == "relation").Body == originalBody,
                    "单组生成期间来源变化拒绝旧结果，已有进度和身份原卡不受影响");
                var resumed = new AgentSequenceLlm(TraceJson.ToJson(new { meaning = "第二组的当前理解", scope = "明确的条件", exceptions = "保留反证", certainty = "开放" }),
                    IdentityChangeReply("global-source-000", false));
                var state = await IdentityEvolutionLogic.EvaluateAsync(store, root, "relation", day, resumed, CancellationToken.None);
                Require(resumed.Requests.Count == 2 && resumed.Requests[0].Contains("单组认识成稿") && resumed.Requests[1].Contains("变化判断") &&
                    state.globallyGrouped && state.understandings.Count == count - 2 && state.globalPlan == null && state.globalResults.Count == 0 &&
                    state.understandings.SelectMany(x => x.cognition_ids).Distinct().Count() == count && state.understandings[2].meaning.StartsWith("独立认识2") &&
                    store.GetCognitionNodes(5000).Count == count && store.LoadIdentityCards(root).Single(x => x.Slot == "relation").Body == originalBody,
                    "跨实例续做只补未完成合并，全部来源守恒，单项原文直接保留，不重请求计划、旧批或合格合并");
                var noCalls = new AgentSequenceLlm();
                var repeated = await IdentityEvolutionLogic.EvaluateAsync(store, root, "relation", day, noCalls, CancellationToken.None);
                Require(noCalls.Requests.Count == 0 && repeated.globallyGrouped, "已完成全局与变化判断恢复零请求");
                repeated.globallyGrouped = false; repeated.decision = null;
                repeated.globalPlan = new IdentityEvolutionLogic.GroupingPlan { groups = new() { Enumerable.Range(0, repeated.understandings.Count).ToList() } };
                store.SavePluginDocument(IdentityEvolutionLogic.StoreId, key, TraceJson.ToJson(repeated));
                var oneGroup = new AgentSequenceLlm(TraceJson.ToJson(new { meaning = "全部材料综合成同一当前理解", scope = "保留共同条件", exceptions = "保留完整反证边界", certainty = "开放" }),
                    IdentityChangeReply("global-source-000", false));
                var consolidated = await IdentityEvolutionLogic.EvaluateAsync(store, root, "relation", day, oneGroup, CancellationToken.None);
                Require(oneGroup.Requests.Count == 2 && !oneGroup.Requests[0].Contains("global-source-") &&
                    consolidated.understandings.Count == 1 && consolidated.understandings[0].cognition_ids.Count == count,
                    "大组合并只生成一项认识，完整来源映射不占模型输出预算");
            }
        }
        finally { Directory.Delete(directory, true); }
    }
}
