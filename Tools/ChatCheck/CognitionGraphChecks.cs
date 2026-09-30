using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using TraceSoul2.Data;
using TraceSoul2.Logic;
using TraceSoul2.Manager;
using TraceSoul2.Plugins;
using TraceSoul2.Plugins.Builtin;

internal static partial class Program
{
    private static void RunCognitionMigrationContractChecks(string assemblyPath)
    {
        // 读取真实 Migration DTO/Prompt，并用显式临时依赖核对夜间批处理；不加载运行配置。
        var assembly = System.Reflection.Assembly.LoadFrom(Path.GetFullPath(assemblyPath));
        RunDayCardReviewChecks(assembly);
        var prompts = assembly.GetType("TraceSoul2.Migrate.ReplayPrompts", true);
        var outputType = prompts.GetNestedType("CognitionFormationOutputData");
        var options = new JsonSerializerOptions { IncludeFields = true };
        var field = outputType.GetField("cognitions");
        Require(field.GetValue(JsonSerializer.Deserialize("{}", outputType, options)) == null,
            "真实日构建DTO缺字段必须保持null，让输出校验触发纠正");
        Require(((System.Collections.IList)field.GetValue(JsonSerializer.Deserialize("{\"cognitions\":[]}", outputType, options))).Count == 0,
            "真实日构建DTO保留显式空数组");
        var evidence = new List<MomentRecord> { new MomentRecord { Id = "visible-evidence", Role = "user", Content = "仅测试用的完整原文",
            CreatedUnixMs = 1, Realm = TraceRealmValues.ExternalWorld, EvidenceType = EvidenceTypeValues.UserReported } };
        var prompt = (string)prompts.GetMethod("BuildCognitionFormationPrompt").Invoke(null, new object[]
        {
            PairIdentity.Missing, "2026-09-27", new List<CognitionSliceRecord>(), new List<LifeTagRecord>(),
            new List<EventIndexRecord>(), new List<EventEntryRecord>(), "他", evidence
        });
        Require(prompt.Contains("visible-evidence") && prompt.Contains(evidence[0].Content) && prompt.Contains("evidence_moment_ids") &&
            prompt.Contains("user=他") && prompt.Contains("world=世界"), "真实日构建Prompt必须提供证据原文、ID及四领域规则");
        var cardType = prompts.GetNestedType("CardUpdateData");
        Require(cardType?.GetField("cognition_ids") != null, "实际 Migration 身份摘要 DTO 必须包含认知依据");
        var migrationPrompts = assembly.GetType("TraceSoul2.Prompts.CorePrompts+Migration", true);
        var cardRules = (string)migrationPrompts.GetField("DayCardRules").GetRawConstantValue();
        Require(cardRules.Contains("cognition_ids") && cardRules.Contains("cards: []") && prompt.Contains("identity_slot"),
            "实际身份复盘和认知形成契约必须连接依据，允许无依据时不更新");
        RunRuntimeSliceMigrationChecks(assembly);
        Console.WriteLine("Cognition migration contract checks passed: missing-field rejection, explicit empty array and evidence prompt.");
    }

    private static void RunCognitionGraphChecks()
    {
        var path = Path.Combine(Path.GetTempPath(), "tracesoul-graph-" + Guid.NewGuid().ToString("N") + ".db");
        string carriedId;
        try
        {
            // 用真实旧表验证增量迁移，不依靠新类型先创建完整 schema。
            using (var old = new SQLite.SQLiteConnection(path))
            {
                old.Execute("CREATE TABLE cognition_slices (Id TEXT PRIMARY KEY, OwnerId TEXT, Summary TEXT, Subtype TEXT, Confidence REAL, Status TEXT, Revision INTEGER, CreatedUnixMs INTEGER, UpdatedUnixMs INTEGER)");
                old.Execute("INSERT INTO cognition_slices VALUES ('legacy', 'ass', '历史兼容认知', 'standard', 0.6, 'active', 2, 1, 1)");
                old.Execute("CREATE TABLE cognition_evidence (Id TEXT PRIMARY KEY, CognitionId TEXT, FactId TEXT, MomentId TEXT, Relation TEXT, Weight REAL)");
                old.Execute("INSERT INTO cognition_evidence VALUES ('legacy-proof', 'legacy', '', 'legacy-source', 'supports', 1)");
            }
            using (var store = new SqliteMemoryManager(path))
            {
                Require(store.GetCognitionNodes(20).Single(x => x.Id == "legacy").Revision == 2 &&
                    store.GetCognitionEvidence(new[] { "legacy" }).Single().MomentId == "legacy-source",
                    "增量迁移保留旧节点版本和已有证据，不伪造旧证据时间");
                store.SavePairIdentity("小雨", "小光", "雨雨");
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                MomentRecord Evidence(string id, string content)
                {
                    var m = new MomentRecord { Id = id, ConversationId = "puzzle", Role = "小雨", Content = content,
                        Realm = TraceRealmValues.ExternalWorld, EvidenceType = EvidenceTypeValues.UserReported,
                        CreatedUnixMs = now, SourcePluginId = "check.graph" };
                    store.SaveMoment(m); return m;
                }
                var m1 = Evidence("graph-1", "青穹杯我一般不喝热水，不过冬天可以。");
                var m2 = Evidence("graph-2", "我改主意了，青穹杯也可以装热水。");
                var m3 = Evidence("graph-3", "请记住要先确认温度，杯子不是关键。");
                BrainCognitionWriteData Create(string summary, string id = "graph-1") => new BrainCognitionWriteData
                {
                    operation = "create", domains = new List<string> { "user", "relation" }, summary = summary,
                    about = "对方饮水偏好", scope = "平时", exceptions = "冬天需另问", confidence = 0.8f, strength = 0.6f,
                    evidence_moment_ids = new List<string> { id }, trace_cues = new List<string> { "青穹杯" }, association_strength = 0.9f
                };
                var first = store.CommitCognitions(m3.Id, new[] { Create("我理解对方平时使用青穹杯时偏爱常温水，但这只是一项可以被新经历修订的偏好。") }).Single();
                Require(first.Summary.Length > 19 && first.Domains == "relation,user", "完整认知保留多领域，不能截成19字口号");
                Require(store.GetCognitionEvidence(new[] { first.Id }).Single().MomentId == m1.Id,
                    "每条认知绑定实际选中证据，不能挂到批次最后一条");
                using (var fixture = new SQLite.SQLiteConnection(path))
                    fixture.Insert(new FactSliceRecord { Id = "same-source-fact", Summary = "同一经历的事实切片",
                        SourceMomentId = m1.Id, Status = "active" });
                var sameSource = store.CommitCognitions(m1.Id, new[] { new BrainCognitionWriteData
                    { operation = "reinforce", target_id = first.Id, confidence = 1,
                        evidence_fact_ids = new List<string> { "same-source-fact" } } }).Single();
                Require(sameSource.Revision == first.Revision && sameSource.Strength == first.Strength,
                    "同一个Moment换成Fact引用不能当成新经历再次增强认知");
                var scoped = Create(first.Summary); scoped.scope = "冬季户外";
                var scopedNode = store.CommitCognitions(m1.Id, new[] { scoped }).Single();
                Require(scopedNode.Id != first.Id, "相同正文不同适用范围不能合并成同一个理解");
                Require(store.CommitCognitions(m1.Id, new[] { scoped }).Single().Id == scopedNode.Id,
                    "同文同范围重放不重复建立节点");
                store.CommitCognitions(m1.Id, new[] { new BrainCognitionWriteData { operation = "retire", target_id = scopedNode.Id,
                    evidence_moment_ids = new List<string> { m1.Id } } });
                var before = store.CountCognitions();
                var bad = Create("错误证据", "missing");
                try { store.CommitCognitions(m1.Id, new[] { Create("事务内新认知"), bad }); throw new Exception("Expected invalid evidence"); }
                catch (InvalidOperationException) { }
                Require(store.CountCognitions() == before, "一批认知证据无效必须整批回滚");
                var weaker = new BrainCognitionWriteData { operation = "weaken", target_id = first.Id, confidence = 0.4f,
                    evidence_moment_ids = new List<string> { m2.Id } };
                for (var i = 0; i < 20; i++)
                {
                    var extra = Evidence("support-" + i, "重复场景里的支持性观察。");
                    store.CommitCognitions(extra.Id, new[] { new BrainCognitionWriteData { operation = "reinforce", target_id = first.Id,
                        confidence = 0.8f, evidence_moment_ids = new List<string> { extra.Id } } });
                }
                var weakened = store.CommitCognitions(m2.Id, new[] { weaker }).Single();
                Require(weakened.Status == "weakened" && store.GetCognitionEvidence(new[] { first.Id }).Any(x => x.Relation == "challenges"),
                    "削弱需要保留反证，不得记成支持");
                var replay = store.CommitCognitions(m2.Id, new[] { weaker }).Single();
                Require(replay.Revision == weakened.Revision && replay.Strength == weakened.Strength, "重复证据不得反复削弱");
                var recreate = store.CommitCognitions(m1.Id, new[] { Create(first.Summary) }).Single();
                Require(recreate.Id == first.Id && recreate.Status == "weakened" && recreate.Confidence == weakened.Confidence,
                    "重复create不能绕过反证复活为新的高置信节点");
                var second = store.CommitCognitions(m2.Id, new[] { Create("青穹杯也能用来喝热水，要考虑此刻的实际需求。", m2.Id) }).Single();
                store.CommitCognitions(m2.Id, new[] { new BrainCognitionWriteData { operation = "link", target_id = first.Id,
                    related_id = second.Id, relation = "contradicts", evidence_moment_ids = new List<string> { m2.Id } } });
                var services = new TracePluginServices(store, new HierarchicalVectorRouterLogic(new FakeEncoder()));
                var turn = new TraceTurnContext("puzzle", m1, new List<MomentRecord>(), 0, false, services);
                var preview = MemoryRecallLogic.Preview(turn, 4);
                Require(preview.Contains(first.Id) && preview.Contains(second.Id) && preview.Contains("反证") && preview.Contains("他") &&
                    preview.Contains("例外") && preview.Contains(m2.Id), "无事件、无聊天历史时仍召回认知及冲突、领域和证据");
                var explicitRecall = MemoryRecallLogic.Assemble(turn, new MindDecisionData { query = "青穹杯" }, 4, out var found);
                Require(found && explicitRecall.Contains(first.Id), "显式 memory.recall 与预激活共享独立认知召回");
                var replacement = Create("喝水先确认温度，青穹杯本身不是限制。", m3.Id);
                replacement.operation = "revise"; replacement.target_id = first.Id;
                carriedId = store.CommitCognitions(m3.Id, new[] { replacement }).Single().Id;
                Require(store.GetCognitionNodes(20).Single(x => x.Id == first.Id).Status == "superseded" &&
                    store.GetCognitionEdges(new[] { carriedId }).Any(x => x.Relation == "revises" && x.ToCognitionId == first.Id),
                    "修订创建新版并保留旧版与关系");
                Require(!new CognitionContextRecallSource(store).Retrieve(new ContextRecallQuery { Text = "青穹杯" }).Any(x => x.Id == first.Id),
                    "已替代理解不能以有效认知回流");
                store.CommitCognitions(m3.Id, new[] { new BrainCognitionWriteData { operation = "retire", target_id = second.Id,
                    evidence_moment_ids = new List<string> { m3.Id } } });
                Require(!new CognitionContextRecallSource(store).Retrieve(new ContextRecallQuery { Text = "青穹杯" }).Any(x => x.Id == second.Id),
                    "退役认知不参与当前判断");

                using var manager = new TracePluginManager(store, services);
                manager.RegisterExternal(new DialogueTracePlugin()); manager.RegisterExternal(new InnerLifePlugin());
                manager.RegisterExternal(new TimeSchedulerPlugin()); manager.RegisterExternal(new AgentProbePlugin());
                var identityBefore = string.Join("|", store.LoadIdentityCards("puzzle").OrderBy(x => x.Slot).Select(x => x.Slot + ":" + x.Body + ":" + x.Revision));
                var step = new AgentStepData { step = "finish", reply = "我会先确认温度。", attention = "青穹杯的温度",
                    attention_links = new List<AgentAttentionLinkData> { new AgentAttentionLinkData
                        { attention = "青穹杯的温度", cognition_ids = new List<string> { carriedId, "invented" } } } };
                var llm = new AgentSequenceLlm(JsonSerializer.Serialize(step, new JsonSerializerOptions { IncludeFields = true }));
                new KernelLogic(store, llm, manager).ChatAsync("puzzle", "青穹杯", historyWindowMax: 0).GetAwaiter().GetResult();
                Require(llm.Requests.Count == 1 && llm.Requests[0].Contains(carriedId), "生产Agent在无历史时读取拼图且普通回应仍只需一次生成");
                var held = store.LoadOrCreateInnerRuntime("puzzle").Attention.Single();
                Require(held.source_refs.Contains("cognition:" + carriedId) && !held.source_refs.Contains("cognition:invented"),
                    "runtime只保存本轮真实召回的认知引用");
                var neutral = new TraceTurnContext("puzzle", Evidence("neutral", "xyz"), new List<MomentRecord>(), 0, false, services);
                Require(MemoryRecallLogic.Preview(neutral, 4).Contains(carriedId), "有效关注应参与下一轮召回");
                var runtime = store.LoadOrCreateInnerRuntime("puzzle");
                runtime.Attention[0].UpdatedUnixMs = now - 7L * 60 * 60 * 1000;
                runtime.Revision++;
                store.SaveInnerRuntime(runtime);
                Require(!MemoryRecallLogic.Preview(neutral, 4).Contains(carriedId), "失活注意不能靠旧引用永久续命");

                var visible = CognitionFormationLogic.SelectEvidence(new[] { m1, m2, m3 });
                Require(!CognitionFormationLogic.Valid(null, visible, new List<CognitionSliceRecord>(), new List<LifeTagRecord>()),
                    "缺少认知字段不能默认为无变化");
                Require(CognitionFormationLogic.Valid(Array.Empty<BrainCognitionWriteData>(), visible,
                    new List<CognitionSliceRecord>(), new List<LifeTagRecord>()), "显式空数组是合法的无变化结果");
                Require(!CognitionFormationLogic.Valid(new[] {
                    new BrainCognitionWriteData { operation = "retire", target_id = carriedId, evidence_moment_ids = new List<string> { m1.Id } },
                    new BrainCognitionWriteData { operation = "link", target_id = carriedId, related_id = "other", relation = "related_to", evidence_moment_ids = new List<string> { m1.Id } }
                }, visible, new[] { new CognitionSliceRecord { Id = carriedId }, new CognitionSliceRecord { Id = "other" } },
                    new List<LifeTagRecord>()), "结构校验需模拟顺序，拒绝在批次后面关联刚退役的节点");
                Require(CognitionFormationLogic.Valid(new[] { Create("有依据的理解") }, visible, new List<CognitionSliceRecord>(), new List<LifeTagRecord>()),
                    "日构建允许无标签但有四领域和真实依据的新理解");
                Require(!CognitionFormationLogic.Valid(new[] { bad }, visible, new List<CognitionSliceRecord>(), new List<LifeTagRecord>()),
                    "日构建结构校验拒绝未展示的证据");
                var missing = Create("没有依据"); missing.evidence_moment_ids.Clear();
                Require(!CognitionFormationLogic.Valid(new[] { missing }, visible, new List<CognitionSliceRecord>(), new List<LifeTagRecord>()),
                    "新日构建不能走旧接口的触发Moment兜底");
                var badFact = Create("错误的事实引用"); badFact.evidence_moment_ids.Clear(); badFact.evidence_fact_ids.Add("missing-fact");
                try { store.CommitCognitions(m1.Id, new[] { badFact }); throw new Exception("Expected invalid fact"); }
                catch (InvalidOperationException) { }
                var world = Create("雨后路面可能湿滑，行动之前需要观察地面。", m3.Id);
                world.domains = new List<string> { "world", "ass" }; world.trace_cues.Clear();
                var worldNode = store.CommitCognitions(m3.Id, new[] { world }).Single();
                Require(worldNode.Domains == "ass,world" && PuzzleDomains.Label(worldNode.Domains) == "我、世界",
                    "世界与自我和他者/关系使用同一多领域存储契约");
                Require(CognitionFormationLogic.SelectEvidence(new[] { new MomentRecord { Id = "oversized", Content = new string('x', 25000) }, m1 })
                    .Select(x => x.Id).SequenceEqual(new[] { m1.Id }), "证据预算不得截断原文或阻塞后面的有效经历");
                Require(string.Join("|", store.LoadIdentityCards("puzzle").OrderBy(x => x.Slot).Select(x => x.Slot + ":" + x.Body + ":" + x.Revision)) == identityBefore,
                    "短期注意与感受更新不能自行改写稳定身份");
            }
            using (var reopened = new SqliteMemoryManager(path))
                Require(reopened.GetCognitionNodes(20).Any(x => x.Id == carriedId) &&
                    reopened.LoadOrCreateInnerRuntime("puzzle").Attention.Any(x => x.source_refs.Contains("cognition:" + carriedId)),
                    "重启恢复长期拼图和runtime引用");
        }
        finally { foreach (var suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(path + suffix)) File.Delete(path + suffix); }

        var adapter = new ContextRecallAdapter();
        var source = new DelegateContextRecallSource("test", _ => new[]
        {
            new ContextRecallCandidate { Id = "large", SourceId = "test", Text = new string('x', 1000), Relevance = 1 },
            new ContextRecallCandidate { Id = "normal", SourceId = "test", Text = "普通", Relevance = 0.8f },
            new ContextRecallCandidate { Id = "counter", SourceId = "test", Text = "反证", Relevance = 0.9f, ContradictsCurrent = true }
        });
        var selected = adapter.Recall(new ContextRecallQuery { MaxItems = 2, MaxChars = 30 }, new[] { source });
        Require(selected.Count == 2 && selected.Any(x => x.Id == "counter") && adapter.RenderNatural("", selected).Length <= 30,
            "严格预算跳过超长首项且保留相关反证");
        Require(adapter.Recall(new ContextRecallQuery { MaxItems = 0 }, new[] { source }).Count == 0 &&
            adapter.Recall(new ContextRecallQuery { MaxChars = 0 }, new[] { source }).Count == 0, "零预算必须返回空结果");
        Console.WriteLine("Cognition graph checks passed: four domains, evidence, revision, conflict, runtime recall, legacy migration and bounds.");
    }
}
