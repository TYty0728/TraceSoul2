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
    private static async Task RunExistenceChecksAsync()
    {
        await RunRuntimeSliceChecksAsync();
        var path = Path.Combine(Path.GetTempPath(), "tracesoul-existence-" + Guid.NewGuid().ToString("N") + ".db");
        const string root = "existence", secret = "PRIVATE_ORIGIN_SENTINEL_813";
        string publicScope;
        try
        {
            using (var legacy = new SQLite.SQLiteConnection(path))
            {
                legacy.Execute("CREATE TABLE identity_cards (Id TEXT PRIMARY KEY, ConversationId TEXT, Slot TEXT, Body TEXT, Revision INTEGER, SourceMomentId TEXT, UpdatedUnixMs INTEGER)");
                legacy.Execute("INSERT INTO identity_cards VALUES ('legacy|self','legacy','self','旧版人工自我内容',4,'',1)");
            }
            using (var store = new SqliteMemoryManager(path))
            {
                var oldCard = store.LoadIdentityCards("legacy").Single(x => x.Slot == "self");
                Require(oldCard.Body == "旧版人工自我内容" && oldCard.Revision == 4 && oldCard.Origin == "legacy" && oldCard.Pinned,
                    "旧卡增量迁移保留正文和版本，不猜造依据或自动覆盖可能的人控内容");
                store.SavePairIdentity("本人", "同伴", "称呼");
                EnvironmentLogic.SaveSettings(store, new EnvironmentSettings { PublicIdentity = "我喜欢认真倾听，也会坦诚表达不同意见。" });
                var services = new TracePluginServices(store, new HierarchicalVectorRouterLogic(new FakeEncoder()));
                using var manager = new TracePluginManager(store, services);
                manager.RegisterExternal(new DialogueTracePlugin());
                manager.RegisterExternal(new InnerLifePlugin());
                manager.RegisterExternal(new TimeSchedulerPlugin());
                PluginEventData Input(string scope, string content = "聊聊倾听") => new() { PluginId = "builtin.dialogue", Role = "user",
                    Content = content, Environment = new EnvironmentObservationData { PlatformId = "builtin.dialogue", SessionType = "private",
                        SessionId = scope, SpeakerId = scope == "local" ? "local-owner" : "peer", SpeakerName = "参与者",
                        ExclusivePair = true, DirectAddress = true } };
                var privateEnv = EnvironmentLogic.Resolve(store, root, Input("local"));
                var publicEnv = EnvironmentLogic.Resolve(store, root, Input("room-a"));
                var otherEnv = EnvironmentLogic.Resolve(store, root, Input("room-b"));
                publicScope = publicEnv.ContextConversationId;
                TraceTurnContext Turn(EnvironmentSnapshotData env) => new(env.ContextConversationId,
                    Moment(env.ContextConversationId, "聊聊倾听"), new(), 0, true, services, environment: env);
                var owner = Turn(privateEnv); var guest = Turn(publicEnv); var other = Turn(otherEnv);
                var seed = store.SaveIdentityCard(root, "personality", "我温和，但不会随声附和。", "");
                Require(!IdentityProjectionLogic.Build(guest).Contains(seed.Body), "旧人格材料不自动公开");
                PuzzleViewLogic.SetShared(store, seed.Id, PuzzleViewLogic.Stamp(seed), true);
                Require(IdentityProjectionLogic.Build(guest).Contains(seed.Body) && IdentityProjectionLogic.Build(owner).Contains(seed.Body) &&
                    IdentityProjectionLogic.Build(owner).Contains("坦诚表达不同意见"), "共同自我描述和批准的基础人格同时参与所有环境");
                seed = store.SaveIdentityCard(root, "personality", "变化后的私人设定", "");
                Require(!IdentityProjectionLogic.Build(guest).Contains(seed.Body), "人工内容改变后不能继承旧共享批准");
                var source = new MomentRecord { Id = "origin-1", ConversationId = root, Role = "user", Content = secret,
                    MemoryVisibility = "private", CreatedUnixMs = 1000 };
                store.SaveMoment(source);
                BrainCognitionWriteData Write(string evidence, string text) => new() { operation = "create", domains = new() { "ass" },
                    summary = text, subtype = "self_model", identity_slot = "self", about = "主体自身", scope = "有人尚未说完时",
                    exceptions = "紧急事项需要及时提醒", confidence = .8f, strength = .6f, evidence_moment_ids = new() { evidence } };
                var node = store.CommitCognitions(source.Id, new[] { Write(source.Id, "我倾向先听完，再决定是否给建议。") }).Single();
                var card = store.SaveDerivedIdentityCard(root, "self", "我正学会耐心听完。", new[] { node.Id }, source.Id);
                Require(IdentityProjectionLogic.Current(card, store.GetCognitionNodes(50)) && card.Origin == "derived", "身份摘要绑定精确认知版本");
                PuzzleViewLogic.SetShared(store, node.Id, PuzzleViewLogic.Stamp(node), true);
                Require(IdentityProjectionLogic.Build(guest).Contains(node.Summary) && !IdentityProjectionLogic.Build(guest).Contains(secret),
                    "私密经历形成的自我理解经批准后跨环境可见，原件不随摘要公开");
                var recall = MemoryRecallLogic.Assemble(guest, new MindDecisionData { query = "先听完再给建议" }, 10);
                Require(recall.Contains(node.Summary) && !recall.Contains(secret) && !recall.Contains(source.Id),
                    "召回共享理解时也不能从证据链泄露私人原文和ID");
                store.SaveMoment(new MomentRecord { Id = "challenge", ConversationId = root, Role = "user", Content = "紧急时先提醒我", CreatedUnixMs = 2000 });
                var revisedWrite = Write("challenge", "我会倾听，同时在紧急情况下及时提醒。");
                revisedWrite.operation = "revise"; revisedWrite.target_id = node.Id;
                var revised = store.CommitCognitions("challenge", new[] { revisedWrite }).Single();
                Require(!IdentityProjectionLogic.Current(card, store.GetCognitionNodes(50)) &&
                    !IdentityProjectionLogic.Build(owner).Contains(card.Body) && IdentityProjectionLogic.Build(owner).Contains(revised.Summary) &&
                    !IdentityProjectionLogic.Build(guest).Contains(node.Summary) && !IdentityProjectionLogic.Build(guest).Contains(revised.Summary),
                    "反证修订让旧摘要失效，新理解接回自我；旧共享批准不延续到新内容");
                store.SaveIdentityCard(root, "self", "本人固定的自我设定", "");
                var rejected = false;
                try { store.SaveDerivedIdentityCard(root, "self", "不能覆盖", new[] { revised.Id }, "challenge"); }
                catch (InvalidOperationException) { rejected = true; }
                Require(rejected, "复盘不能覆盖本人固定的设定");
                store.SetIdentityPinned(root, "self", false);
                store.SaveDerivedIdentityCard(root, "self", "有依据的新摘要", new[] { revised.Id }, "challenge");
                store.SaveMoment(new MomentRecord { Id = "self-repeat", ConversationId = root, Role = "assistant", Content = revised.Summary });
                var repeated = store.CommitCognitions("self-repeat", new[] { new BrainCognitionWriteData { operation = "reinforce",
                    target_id = revised.Id, evidence_moment_ids = new() { "self-repeat" }, confidence = 1 } }).Single();
                Require(repeated.Strength == revised.Strength && repeated.Revision == revised.Revision, "自我重复不能成为新增外部证明");

                var privateLlm = new AgentSequenceLlm("{\"step\":\"finish\",\"reply\":\"明白\",\"inner\":\"" + secret +
                    "\",\"mood\":\"" + secret + "\",\"mood_changed\":true,\"affect\":\"sad\",\"scene\":\"私密场景\"}");
                await new KernelLogic(store, privateLlm, manager).ChatAsync(root, "刚刚有点难过");
                var publicLlm = new AgentSequenceLlm("{\"step\":\"finish\",\"reply\":\"你好\",\"refine\":true,\"inner\":\"对眼前交流产生好奇\",\"affect\":\"curious\"}", "你好。");
                await new KernelLogic(store, publicLlm, manager).ProcessPluginEventAsync(root, Input("room-a"));
                Require(publicLlm.Requests.Count == 2 && publicLlm.Requests.All(x => x.Contains("低落") && !x.Contains(secret)),
                    "公开决策和润色延续总体情绪，私人原因不进入两次请求");
                Require(SubjectRuntimeLogic.View(owner).Narrative == "对眼前交流产生好奇" &&
                    SubjectRuntimeLogic.View(other).Mood == "好奇" && !RuntimeContextLogic.State(other).Contains(secret),
                    "公开反馈接回同一主体，切回私密保留影响；第三环境只有允许的情绪视图");
                Require(store.LoadOrCreateInnerRuntime(root).OngoingActivity == "私密场景", "全局感受变化不覆盖私密局部场景");
                var oldTurn = Turn(privateEnv); SubjectRuntimeLogic.Begin(oldTurn);
                var newer = Turn(publicEnv); SubjectRuntimeLogic.Begin(newer);
                SubjectRuntimeLogic.Commit(newer, new AgentStepData { affect = "joyful" });
                SubjectRuntimeLogic.Commit(oldTurn, new AgentStepData { affect = "angry" });
                Require(SubjectRuntimeLogic.Read(store, root).Affect == "joyful", "旧轮提交不能覆盖较新的主体状态");
                var beforeReview = SubjectRuntimeLogic.Read(store, root);
                var lateReview = InnerLifeLogic.Reduce(store.LoadOrCreateInnerRuntime(root),
                    new InnerRuntimeWriteData { narrative = "旧复盘不应覆盖" }, "challenge", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                Require(!SubjectRuntimeLogic.CommitReview(store, root, beforeReview.Revision - 1, lateReview,
                    new InnerRuntimeWriteData { narrative = lateReview.Narrative }) &&
                    store.LoadOrCreateInnerRuntime(root).Narrative != lateReview.Narrative,
                    "日复盘与实时对话并发时，旧复盘不覆盖全局或局部内心");
                Require(AgentLoopLogic.ValidationError(new AgentStepData { step = "wait", affect = secret }, false, false)?.Contains("affect") == true,
                    "自由文本不能混入跨环境情绪字段");

                var dayStart = MemoryDayLogic.StartOf("2001-02-03").ToUnixTimeMilliseconds();
                void PublicEvidence(string id, EnvironmentSnapshotData env) => store.SaveMoment(new MomentRecord {
                    Id = id, ConversationId = env.ContextConversationId, Role = "user", Content = "请先听我讲完", CreatedUnixMs = dayStart + 1,
                    MemoryVisibility = "public", EnvironmentJson = TraceJson.ToJson(env) });
                PublicEvidence("public-a", publicEnv); PublicEvidence("public-b", otherEnv);
                var publicWrite = Write("public-a", "我在多人交流时也愿意认真倾听。");
                var reviewLlm = new AgentSequenceLlm(TraceJson.ToJson(new PublicExperienceLogic.Output { cognitions = new() { publicWrite } }),
                    "{\"cognitions\":[]}");
                var built = await PublicExperienceLogic.BuildAsync(store, reviewLlm, dayStart, dayStart + 86400000);
                Require(built == 2 && reviewLlm.Requests.Count == 2 && reviewLlm.Requests.All(x => !x.Contains(secret)) &&
                    !store.GetUnbuiltPublicMemoryDayKeysBefore(dayStart + 86400000).Contains("2001-02-03"),
                    "公开经历按环境整理，无私密卡或原件混入；真实提交后退出队列");
                var publicNode = store.GetCognitionNodes(50).Single(x => x.Summary == publicWrite.summary);
                Require(publicNode.ContextConversationId == publicScope && publicNode.SubjectKey.Contains("peer") &&
                    IdentityProjectionLogic.Build(owner).Contains(publicNode.Summary) && IdentityProjectionLogic.Build(guest).Contains(publicNode.Summary) &&
                    !IdentityProjectionLogic.Build(other).Contains(publicNode.Summary),
                    "公开经历生长到同一拼图，保留真实对象与环境边界");
                var cross = new BrainCognitionWriteData { operation = "reinforce", target_id = revised.Id,
                    confidence = 1, evidence_moment_ids = new() { "public-a" } };
                rejected = false;
                try { store.CommitCognitions("public-a", new[] { cross }); } catch (InvalidOperationException) { rejected = true; }
                Require(rejected, "公开来源不能直接改写私密理解");
                var countBefore = store.GetCognitionNodes(100).Count;
                PublicEvidence("public-rollback", publicEnv);
                rejected = false;
                try { store.CommitPublicExperience(new[] { "public-rollback" }, new[] { Write("origin-1", "错误来源不应保存") }); }
                catch (InvalidOperationException) { rejected = true; }
                Require(rejected && store.GetCognitionNodes(100).Count == countBefore &&
                    store.GetPublicMomentsInRange(dayStart, dayStart + 86400000).Any(x => x.Id == "public-rollback"),
                    "公开整理错误不能消费原件或留下半批认知");
                store.CommitPublicExperience(new[] { "public-rollback" }, Array.Empty<BrainCognitionWriteData>());
                var noCalls = new AgentSequenceLlm();
                Require(await PublicExperienceLogic.BuildAsync(store, noCalls, dayStart, dayStart + 86400000) == 0 && noCalls.Requests.Count == 0,
                    "公开已归档证据重跑不再调用模型或重复生长");
            }
            using (var reopened = new SqliteMemoryManager(path))
            {
                Require(SubjectRuntimeLogic.Read(reopened, root).Affect == "joyful" &&
                    reopened.LoadIdentityCards(root).Single(x => x.Slot == "self").Origin == "derived" &&
                    reopened.GetCognitionNodes(100).Any(x => x.ContextConversationId == publicScope),
                    "重启保留连续状态、身份依据与公开经历的认知归属");
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
        Console.WriteLine("Existence checks passed: common identity, provenance, revisions, audience views, continuous subject, public growth and restart.");
    }
}
