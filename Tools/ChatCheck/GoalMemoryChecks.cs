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

                var future = Create("周末整理照片", "self", "goal");
                future.starts_at = DateTimeOffset.UtcNow.AddDays(2).ToString("o");
                GoalMemoryLogic.Apply(turn, new() { future });
                Require(GoalMemoryLogic.BuildContext(turn).Contains("\"timing\":\"future\"") &&
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
                await new AgentLoopLogic(actionLlm).RunAsync(loopTurn, "", () => new(), (_, _) =>
                {
                    Require(GoalMemoryLogic.Read(store, "goal-before-action").Single().Content == preference,
                        "明确要求必须在后续行动前持久化");
                    return Task.FromResult(new TraceCapabilityResultData { Status = "failed", Summary = "离线失败" });
                }, default);
                Require(actionLlm.Requests.Count == 2 && actionLlm.Requests[1].Contains(preference),
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
                store.SavePluginDocument("kernel.goals", "broken-goals", "{}");
                try { GoalMemoryLogic.Read(store, "broken-goals"); throw new Exception("损坏文档应报错"); }
                catch (JsonException) { }
                Require(store.LoadPluginDocument("kernel.goals", "broken-goals") == "{}", "损坏目标文档不能当空表覆盖");
            }
        }
        finally { Delete(path); Delete(path + "-wal"); Delete(path + "-shm"); }
        Console.WriteLine("Goal memory checks passed: immediate persistence, silence, restart, context, scope, dates, revision, cancellation, evidence, atomicity, replay and capacity.");
    }
}
