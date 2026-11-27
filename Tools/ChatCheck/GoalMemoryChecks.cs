using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Logic;
using TraceSoul2.Manager;
using TraceSoul2.Plugins;
using TraceSoul2.Plugins.Builtin;

internal static partial class Program
{
    private static async Task RunGoalMemoryChecksAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), "tracesoul2-goals-" + Guid.NewGuid().ToString("N") + ".sqlite3");
        const string conversation = "goal-memory";
        const string preference = "可以自然结束，不为续聊追加问题";
        const string quote = "以后别总用问题结尾";
        var json = new JsonSerializerOptions { IncludeFields = true };
        AgentGoalUpdateData Create(string text = preference, string origin = "user", string kind = "preference") => new()
        { operation = "create", kind = kind, content = text, applies_when = "日常聊天，真正需要了解的事仍可问",
            horizon = "ongoing", source = origin, evidence_quote = origin == "user" ? quote : "" };
        string Step(AgentGoalUpdateData update) => JsonSerializer.Serialize(new AgentStepData
        { step = "finish", reply = "记下了。", goal_updates = new() { update } }, json);
        try
        {
            using (var store = new SqliteMemoryManager(path))
            {
                store.SavePairIdentity("小雨", "小光", "雨雨");
                var services = new TracePluginServices(store, new HierarchicalVectorRouterLogic(new FakeEncoder()));
                using var manager = new TracePluginManager(store, services);
                manager.RegisterExternal(new DialogueTracePlugin());
                manager.RegisterExternal(new InnerLifePlugin());
                var llm = new AgentSequenceLlm(Step(Create()));
                await new KernelLogic(store, llm, manager).ChatAsync(conversation, quote);
                Require(llm.Requests.Count == 1 && llm.TextRequests == 0 &&
                    GoalMemoryLogic.Read(store, conversation).Single().Content == preference &&
                    store.GetRecentMoments(conversation, 10).Count == 2,
                    "明确偏好随回应在当轮保存，不增加提取模型");
            }
            using (var store = new SqliteMemoryManager(path))
            {
                var services = new TracePluginServices(store, new HierarchicalVectorRouterLogic(new FakeEncoder()));
                using var manager = new TracePluginManager(store, services);
                manager.RegisterExternal(new DialogueTracePlugin());
                manager.RegisterExternal(new InnerLifePlugin());
                var llm = new AgentSequenceLlm("{\"step\":\"finish\",\"reply\":\"吃过了。\"}");
                await new KernelLogic(store, llm, manager).ChatAsync(conversation, "吃饭了吗", historyWindowMax: 0);
                Require(llm.Requests.Single().Contains(preference) && llm.Requests.Single().Contains("【当下与未来"),
                    "重启后无关话题、关闭近期历史也须带入有效偏好，不依赖普通相关性召回");
                TraceTurnContext Turn(string text, string session = conversation, bool human = true)
                {
                    var moment = Moment(session, text); moment.Id = Guid.NewGuid().ToString("N");
                    moment.Role = human ? "小雨" : "system_event";
                    store.SaveMoment(moment);
                    return new TraceTurnContext(session, moment, new List<MomentRecord>(), 0, human, services);
                }
                var turn = Turn("我改主意了，困惑时请直接问");
                Require(!GoalMemoryLogic.BuildContext(Turn("随便聊聊", "another-person")).Contains(preference), "偏好不能跨会话串用");
                var original = GoalMemoryLogic.Read(store, conversation).Single();
                var revise = Create("困惑时直接问，不必刻意少问");
                revise.operation = "revise"; revise.id = original.Id; revise.evidence_quote = "困惑时请直接问";
                var invalid = Create(); invalid.evidence_quote = "对方没有说过的内容";
                Require(!GoalMemoryLogic.Valid(turn, new() { revise, invalid }), "整批存在伪造依据必须拒绝");
                var evidenceError = GoalMemoryLogic.ValidationError(turn, new() { revise, invalid });
                Require(evidenceError.Contains("$.goal_updates[1].evidence_quote") && !evidenceError.Contains(invalid.evidence_quote),
                    "目标纠正指出具体项和依据字段，不回显私人原文");
                try { GoalMemoryLogic.Apply(turn, new() { revise, invalid }); throw new Exception("无效批次应失败"); }
                catch (ArgumentException) { }
                Require(GoalMemoryLogic.Read(store, conversation).Single().Status == "active", "无效批次不能部分保存");
                GoalMemoryLogic.Apply(turn, new() { revise });
                GoalMemoryLogic.Apply(turn, new() { revise });
                var records = GoalMemoryLogic.Read(store, conversation);
                var revised = records.Single(x => x.Status == "active");
                Require(records.Count == 2 && records.Single(x => x.Id == original.Id).Status == "superseded" &&
                    revised.ReplacesId == original.Id && revised.EvidenceQuote == revise.evidence_quote &&
                    !GoalMemoryLogic.BuildContext(turn).Contains(preference), "修订保留依据与版本，重放幂等，旧要求不再注入");

                var cancelTurn = Turn("刚才那条要求取消");
                GoalMemoryLogic.Apply(cancelTurn, new() { new() { operation = "cancel", id = revised.Id,
                    source = "user", evidence_quote = "那条要求取消" } });
                Require(!GoalMemoryLogic.BuildContext(cancelTurn).Contains(revised.Content), "明确撤回后不能继续约束后续选择");
                var transientTurn = Turn(quote);
                var transient = Create("这次先听我说"); transient.horizon = "turn";
                GoalMemoryLogic.Apply(transientTurn, new() { transient });
                Require(GoalMemoryLogic.BuildContext(transientTurn).Contains(transient.content) &&
                    !GoalMemoryLogic.BuildContext(Turn("下一轮")).Contains(transient.content), "本轮安排不能变成永久偏好");
                var timed = Create("今天少说一点"); timed.horizon = "until";
                timed.expires_at = DateTimeOffset.UtcNow.AddHours(2).ToString("o");
                GoalMemoryLogic.Apply(transientTurn, new() { timed });
                Require(!GoalMemoryLogic.BuildContext(transientTurn, DateTimeOffset.UtcNow.AddHours(3).ToUnixTimeMilliseconds())
                    .Contains(timed.content), "限时偏好到期自动退出决策");
                timed.expires_at = "2026-12-01T12:00:00";
                Require(!GoalMemoryLogic.Valid(transientTurn, new() { timed }), "时间必须带时区，不能静默猜时区");
                Require(GoalMemoryLogic.ValidationError(transientTurn, new() { timed }).Contains("$.goal_updates[0].expires_at"),
                    "目标时间格式错误须明确指出开始或到期字段");

                var future = Create("周末整理照片", "self", "goal");
                future.starts_at = DateTimeOffset.UtcNow.AddDays(2).ToString("o");
                GoalMemoryLogic.Apply(turn, new() { future });
                Require(GoalMemoryLogic.BuildContext(turn).Contains("未开始，起于") &&
                    GoalMemoryLogic.BuildContext(turn).Contains(future.content), "未来目标持续可见并明确未到开始时间");
                var futureId = GoalMemoryLogic.Read(store, conversation).Single(x => x.Content == future.content).Id;
                var complete = new AgentGoalUpdateData { operation = "complete", id = futureId, source = "self" };
                Require(!GoalMemoryLogic.Valid(turn, new() { complete }), "自己计划完成不能当作完成证据");
                complete.source = "feedback"; complete.evidence_quote = "照片已整理到相册";
                var receipt = new TraceCapabilityResultData { Status = "running", Summary = complete.evidence_quote };
                turn.Workspace.Results.Add(receipt);
                Require(!GoalMemoryLogic.Valid(turn, new() { complete }), "受理不能冒充已完成");
                receipt.Status = "failed";
                Require(!GoalMemoryLogic.Valid(turn, new() { complete }), "失败不能冒充已完成");
                receipt.Status = "success";
                GoalMemoryLogic.Apply(turn, new() { complete });
                Require(!GoalMemoryLogic.BuildContext(turn).Contains(future.content), "真实结果确认后目标退出待办，历史仍保留");
                Require(!GoalMemoryLogic.Valid(Turn(quote, human: false), new() { Create() }) &&
                    !GoalMemoryLogic.Valid(turn, new() { Create(origin: "self") }),
                    "后台/网页事件不能冒充对方的新要求，自行推测不能写成明确偏好");

                var userTurn = Turn(quote, "user-goal");
                GoalMemoryLogic.Apply(userTurn, new() { Create(kind: "goal") });
                var userGoal = GoalMemoryLogic.Read(store, "user-goal").Single();
                Require(!GoalMemoryLogic.Valid(userTurn, new() { new() { operation = "cancel", id = userGoal.Id, source = "self" } }),
                    "自身意愿不能偷偷撤回对方明确提出的目标");
                var execId = services.Executions.Start("user-goal", new BrainCapabilityCallData { capability_id = "test.organ" }, "test", default);
                services.Executions.AcceptResult(execId, new TraceCapabilityResultData { Status = "running" });
                services.Executions.Report(new TraceExecutionReceiptData { ExecutionId = execId, Sequence = 1, Status = "completed",
                    ConfirmedContent = "指定动作已经完成" });
                var receiptTurn = Turn("设备回执", "user-goal", human: false);
                receiptTurn.Moment.SourcePluginId = "runtime.execution"; receiptTurn.Moment.SourceEventId = execId;
                GoalMemoryLogic.Apply(receiptTurn, new() { new() { operation = "complete", id = userGoal.Id,
                    source = "feedback", evidence_quote = "指定动作已经完成" } });
                Require(!GoalMemoryLogic.Read(store, "user-goal").Any(x => x.Status == "active"),
                    "异步设备完成回执也可确认目标，不必等对方再次发言");

                var loopTurn = Turn(quote, "goal-before-action");
                var actionStep = new AgentStepData { step = "continue", goal_updates = new() { Create() },
                    actions = new() { new() { call_id = "test", capability_id = "memory.recall",
                        arguments = new() { new() { name = "query", value = "资料" } } } } };
                var actionLlm = new AgentSequenceLlm(JsonSerializer.Serialize(actionStep, json), "{\"step\":\"finish\",\"reply\":\"没查成，但要求记下了。\"}");
                var appliedDecision = await new AgentLoopLogic(actionLlm).RunAsync(loopTurn, "", () => new(), (_, _) =>
                {
                    Require(GoalMemoryLogic.Read(store, "goal-before-action").Single().Content == preference,
                        "明确要求必须在后续行动前持久化");
                    return Task.FromResult(new TraceCapabilityResultData { Status = "failed", Summary = "离线失败" });
                }, default);
                Require(appliedDecision.applied_goal_updates.Single().content == preference && actionLlm.Requests.Count == 2 && actionLlm.Requests[1].Contains(preference),
                    "工具失败后偏好仍保留并进入同轮下一步，不依赖最终步骤重新填写");
                var refineLlm = new AgentSequenceLlm("简短正文。");
                await new ExpressorLogic(refineLlm).ExpressAsync(loopTurn, manager.GetPlugins(), manager.GetAvailableCatalog(loopTurn),
                    Array.Empty<TraceContextBlockData>(), new AgentStepData { reply = "草稿", step = "finish", refine = true },
                    "", false, null, default);
                Require(refineLlm.Requests.Single().Contains(preference), "专门润色也应遵循已保存的相处偏好");

                var capTurn = Turn("容量检查", "goal-capacity");
                for (var i = 0; i < GoalMemoryLogic.MaxActive; i++)
                    GoalMemoryLogic.Apply(capTurn, new() { Create("目标" + i, "self", "goal") });
                Require(!GoalMemoryLogic.Valid(capTurn, new() { Create("多余目标", "self", "goal") }) &&
                    GoalMemoryLogic.Read(store, "goal-capacity").Count == GoalMemoryLogic.MaxActive,
                    "容量满时明确拒绝新项，不静默丢失有效目标");
                var repair = new AgentSequenceLlm(Step(invalid), Step(Create()));
                await new KernelLogic(store, repair, manager).ChatAsync("goal-repair", quote);
                Require(repair.Requests.Count == 2 && GoalMemoryLogic.Read(store, "goal-repair").Count == 1,
                    "目标字段校验进入现有一次纠正，不执行无效写入");
                await CheckGoalReadingViewAsync(store, services, manager);
                store.SavePluginDocument("kernel.goals", "broken-goals", "{}");
                try { GoalMemoryLogic.Read(store, "broken-goals"); throw new Exception("损坏文档应报错"); }
                catch (JsonException) { }
                Require(store.LoadPluginDocument("kernel.goals", "broken-goals") == "{}", "损坏目标文档不能当空表覆盖");
            }
        }
        finally { Delete(path); Delete(path + "-wal"); Delete(path + "-shm"); }
        Console.WriteLine("Goal memory checks passed: immediate persistence, silence, restart, context, scope, dates, revision, cancellation, evidence, atomicity, replay and capacity.");
    }

    private static async Task CheckGoalReadingViewAsync(SqliteMemoryManager store, TracePluginServices services, TracePluginManager manager)
    {
        const string conversation = "goal-scoped-view";
        const string preference = "固定点藤椒饭，小份刚好";
        const string originalEvidence = "原话：平日订餐时吃藤椒饭那份";
        var topic = new VectorIndexNode("concept.goal.lunch", VectorNodeLevel.Concept, "工作日午餐",
            "午餐外卖和选餐", null, null, null, null, null);
        store.SeedLifeTags(new[] { topic });
        services.SetRouter(new GoalTopicProbe(topic));
        TraceTurnContext Turn(string text, string channel = conversation)
        {
            var moment = Moment(channel, text);
            moment.Role = "小雨";
            store.SaveMoment(moment);
            return new TraceTurnContext(channel, moment, new(), 0, true, services);
        }
        var first = Turn(originalEvidence);
        GoalMemoryLogic.Apply(first, new() { new() {
            operation = "create", kind = "preference", content = preference, source = "user",
            evidence_quote = originalEvidence, horizon = "ongoing", applies_when = "平日订餐",
            tags = new() { topic.Label, "不存在的主题" }
        } });
        var item = GoalMemoryLogic.Read(store, conversation).Single();
        Require(item.TagIds.SequenceEqual(new[] { topic.Id }), "主题引用可选，仅保存已有主题，无效标签不妨碍保存");
        var originalDoc = store.LoadPluginDocument("kernel.goals", conversation);
        var initial = new AgentSequenceLlm("{\"step\":\"finish\",\"reply\":\"我来看看这个函数。\"}");
        await new KernelLogic(store, initial, manager).ChatAsync(conversation, "帮我看看函数", historyWindowMax: 0);
        Require(!initial.Requests.Single().Contains(preference) && !initial.Requests.Single().Contains(originalEvidence) &&
            store.LoadPluginDocument("kernel.goals", conversation) == originalDoc,
            "无关首轮不注入，完整记录和依据仍原样保存");

        var tagged = new AgentSequenceLlm("{\"step\":\"finish\",\"reply\":\"这顿可以点小份。\"}");
        await new KernelLogic(store, tagged, manager).ChatAsync(conversation, "附近外卖怎么选", historyWindowMax: 0);
        Require(tagged.Requests.Single().Contains(preference) && !tagged.Requests.Single().Contains(originalEvidence),
            "后续同主题首轮能通过已有LifeTag激活完整条件，无须原话或独立选择请求");

        var recall = new AgentSequenceLlm(
            "{\"step\":\"continue\",\"actions\":[{\"capability_id\":\"memory.recall\",\"arguments\":[{\"name\":\"kind\",\"value\":\"goals\"},{\"name\":\"query\",\"value\":\"藤椒饭\"}]}]}",
            "{\"step\":\"finish\",\"reply\":\"记得那条偏好。\"}");
        var result = await new KernelLogic(store, recall, manager).ChatAsync(conversation, "还记得那条偏好吗", historyWindowMax: 0);
        var evidence = result.ContributionResults.Single(x => x.CapabilityId == "memory.recall");
        Require(!recall.Requests[0].Contains(preference) && recall.Requests[1].Contains(preference) &&
            evidence.Payload.Contains(item.Id) && evidence.EvidenceRefs.Contains("moment:" + first.Moment.Id) &&
            store.LoadPluginDocument("kernel.goals", conversation) == originalDoc,
            "真实Agent按需查到之前未注入的记录和来源，查询不改变原记录");

        var next = Turn("午饭换清汤面吧");
        GoalMemoryLogic.Apply(next, new() { new() {
            operation = "revise", id = item.Id, kind = "preference", content = "清汤面",
            applies_when = "平日订餐", horizon = "ongoing", source = "user", evidence_quote = next.Moment.Content
        } });
        var query = new BrainCapabilityCallData { capability_id = "memory.recall", arguments = new() {
            new() { name = "kind", value = "goals" }, new() { name = "query", value = item.Id }
        } };
        var previous = ScopedMemoryReadLogic.Read(next, query);
        var other = Turn("另一条私密目标", "other-goals");
        GoalMemoryLogic.Apply(other, new() { new() {
            operation = "create", kind = "goal", content = other.Moment.Content, applies_when = "其他场景",
            horizon = "ongoing", source = "user", evidence_quote = other.Moment.Content
        } });
        Require(previous.Status == "success" && previous.Payload.Contains("已修订") && previous.Payload.Contains(preference) &&
            ScopedMemoryReadLogic.Read(other, query).Status == "empty",
            "修订后仍能查旧版本，原ID不能跨会话读取");
        var revision = GoalMemoryLogic.Read(store, conversation).Single(x => x.Status == "active");
        Require(revision.Content == "清汤面" && revision.ReplacesId == item.Id && revision.TagIds.SequenceEqual(item.TagIds),
            "查回的ID可沿原有写入链修订，同范围省略标签时延续已保存关联");

        var dated = Turn("这周去取相机");
        var now = DateTimeOffset.UtcNow;
        GoalMemoryLogic.Apply(dated, new() { new() {
            operation = "create", kind = "goal", content = "去取相机", source = "user", evidence_quote = dated.Moment.Content,
            applies_when = "这周", horizon = "until", starts_at = now.AddMinutes(20).ToString("o"), expires_at = now.AddHours(2).ToString("o")
        } });
        var reading = GoalMemoryLogic.BuildContext(Turn("待会怎么安排"));
        Require(reading.Contains("近期安排") && reading.Contains("未开始，起于") && reading.Contains("去取相机") &&
            !GoalMemoryLogic.BuildContext(Turn("今天已经结束"), now.AddHours(3).ToUnixTimeMilliseconds()).Contains("去取相机") &&
            GoalMemoryLogic.Read(store, conversation).Any(x => x.Content == "去取相机"),
            "时间窗独立激活，过期仅退出默认读取，保留记录");
        var plannedDay = now.AddMinutes(20).ToOffset(MemoryDayLogic.ChinaOffset).ToString("yyyy-MM-dd");
        query.arguments = new() { new() { name = "kind", value = "goals" }, new() { name = "query", value = "当天安排" },
            new() { name = "date_start", value = plannedDay }, new() { name = "date_end", value = plannedDay } };
        Require(ScopedMemoryReadLogic.Read(next, query).Payload.Contains("去取相机"), "明确日期可按计划时间窗查回条目");

        var album = Turn("今天聊整理相册");
        for (var index = 0; index < 16; index++)
            GoalMemoryLogic.Apply(album, new() { new() {
                operation = "create", kind = "goal", content = $"相册待整理素材 {index}：" + new string('照', 190),
                applies_when = "整理相册时", horizon = "ongoing", source = "self"
            } });
        var albumDoc = store.LoadPluginDocument("kernel.goals", conversation);
        var preview = GoalMemoryLogic.BuildContext(album);
        Require(preview.Length < 2400 && preview.Contains("相册待整理素材") &&
            store.LoadPluginDocument("kernel.goals", conversation) == albumDoc, "注入预算只限视图，满额仍保留完整文档");
        query.arguments = new() { new() { name = "kind", value = "goals" }, new() { name = "query", value = "相册" } };
        var firstPage = ScopedMemoryReadLogic.Read(album, query);
        query.arguments.Add(new() { name = "offset", value = "6" });
        var secondPage = ScopedMemoryReadLogic.Read(album, query);
        Require(firstPage.Payload.Contains("offset=6") && secondPage.Status == "success" &&
            !firstPage.EvidenceRefs.Any(x => x != "moment:" + album.Moment.Id) &&
            !secondPage.Payload.Contains(firstPage.Payload.Split('\n').First(x => x.Contains("相册待整理素材"))),
            "相关记录多时可继续分段读取，读取视图不吞掉预算外条目");
    }

    private sealed class GoalTopicProbe(VectorIndexNode topic) : IHierarchicalVectorRouter
    {
        public VectorRouteResult Route(string query, VectorRouteSettings settings = null) => new(query, null, null, RankConcepts(query));
        public IReadOnlyList<VectorRouteHit> RankConcepts(string query, VectorRouteSettings settings = null) =>
            query.Contains("外卖", StringComparison.Ordinal) ? new[] { new VectorRouteHit(topic, 0.8f, 0.8f, 0, 0) } :
                Array.Empty<VectorRouteHit>();
    }
}
