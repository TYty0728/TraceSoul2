using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Logic;
using TraceSoul2.Manager;
using TraceSoul2.Plugins;
using TraceSoul2.Plugins.Builtin;
using TraceSoul2.Prompts;

internal static partial class Program
{
    private static async Task RunAgentPromptChecksAsync()
    {
        using var store = new SqliteMemoryManager(":memory:");
        store.SavePairIdentity("小雨", "小光", "雨雨");
        var services = new TracePluginServices(store, new HierarchicalVectorRouterLogic(new FakeEncoder()));
        using var manager = new TracePluginManager(store, services);
        manager.RegisterExternal(new TimeSchedulerPlugin());
        manager.RegisterExternal(new InnerLifePlugin());
        var turn = new TraceTurnContext("prompt-check", Moment("prompt-check", "在吗"), new(), 0, true, services);
        store.SaveMoment(new MomentRecord { Id = "last-companion", ConversationId = turn.ConversationId,
            Role = "小光", Content = "上一轮已经回应", CreatedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 60000 });
        await manager.BuildContextBlocksAsync(turn, default);
        Require(!turn.Workspace.ContextBlocks.Single(x => x.FacetId == "time.context").Content.Contains("至今没有她的新回复"),
            "新入站不能同时被时间上下文说成对方没有回复");
        turn.Workspace.ContextBlocks.Add(new TraceContextBlockData { FacetId = "camera.observation", Title = "相机状态", Content = "保留当前镜头观察" });
        turn.Workspace.ContextBlocks.Add(new TraceContextBlockData { FacetId = "camera.copy", Title = "副本", Content = "保留当前镜头观察" });
        turn.Workspace.ContextBlocks.Add(new TraceContextBlockData { FacetId = "voice.usage", Content = "不应注入的旧语音协议" });
        var context = AgentPromptContextLogic.Context(turn);
        var time = turn.Workspace.ContextBlocks.Single(x => x.FacetId == "time.context").Content;
        Require(context.Split(time).Length == 2 && context.Split("保留当前镜头观察").Length == 2 &&
            !context.Contains("不应注入的旧语音协议") && !context.Contains("【当下与未来"),
            "状态、观察仅各出现一次，无目标不输出空目录，旧 usage 不进入 Agent");

        for (var i = 0; i < 30; i++)
        {
            var id = services.Executions.Start(turn.ConversationId,
                new BrainCapabilityCallData { capability_id = i % 2 == 0 ? "dialogue.print" : "qq.sticker.send" }, "qq", default);
            services.Executions.AcceptResult(id, new TraceCapabilityResultData { Status = "success", Summary = "旧运行痕迹" });
        }
        var llm = new AgentSequenceLlm("{\"step\":\"finish\",\"reply\":\"我在。\"}");
        await new AgentLoopLogic(llm).RunAsync(turn, "", () => new(), (_, _) => throw new Exception("不应执行"), default);
        Require(!llm.Requests[0].Contains("旧运行痕迹") && !llm.Requests[0].Contains("qq.sticker.send") &&
            !llm.Requests[0].Contains("execution.cancel") && !llm.Requests[0].Contains("【联网】"),
            "已结束的内部账本、无可取消执行时的取消能力、不可用联网说明不应注入");
        Require(services.Executions.List(turn.ConversationId).Count == 30, "过滤提示不能删除真实账本");

        var activeId = services.Executions.Start(turn.ConversationId,
            new BrainCapabilityCallData { capability_id = "test.voice", group_id = "greeting" }, "qq", default);
        services.Executions.AcceptResult(activeId, new TraceCapabilityResultData { Status = "running" });
        services.Executions.Report(new TraceExecutionReceiptData { ExecutionId = activeId, Sequence = 1, Status = "running", ConfirmedContent = "已经播放的前缀" });
        var active = new AgentSequenceLlm("{\"step\":\"finish\",\"reply\":\"听着呢。\"}");
        await new AgentLoopLogic(active).RunAsync(turn, "", () => new(), (_, _) => throw new Exception("不应执行"), default);
        Require(active.Requests[0].Contains(activeId) && active.Requests[0].Contains("已经播放的前缀") &&
            active.Requests[0].Contains("execution.cancel") && !active.Requests[0].Contains("ConversationId"),
            "真实进行中的执行保留取消 ID、状态和播放证据，内部元数据不注入");
        services.Executions.Report(new TraceExecutionReceiptData { ExecutionId = activeId, Sequence = 2, Status = "completed", Summary = "设备确认完成" });
        var receipt = new TraceTurnContext(turn.ConversationId, new MomentRecord { Id = "receipt", SourcePluginId = "runtime.execution",
            SourceEventId = activeId, Content = "动作完成事件", PayloadJson = "设备确认完成", Role = "system_event" }, new(), 0, false, services);
        var done = new AgentSequenceLlm("{\"step\":\"wait\"}");
        await new AgentLoopLogic(done).RunAsync(receipt, "", () => new(), (_, _) => throw new Exception("不应执行"), default);
        Require(done.Requests[0].Contains("【设备确认回执】") && done.Requests[0].Contains("设备确认完成"),
            "过滤旧终态不能丢失当前唤醒的设备完成回执");

        var tool = new TraceContributionDescriptorData { Id = "test.read", Kind = TraceContributionKindValues.CallableNerve,
            Description = "读资料", ParametersJsonSchema = "{\"required\":[\"query\"],\"properties\":{\"query\":{\"type\":\"string\"}}}" };
        var catalog = AgentPromptContextLogic.Catalog(new[] { tool });
        Require(catalog.Contains(tool.ParametersJsonSchema) && !catalog.Contains("when_to_use") && !catalog.Contains("null"),
            "插件参数原样展示，不重复 JSON 转义或注入空元数据");
        var continuation = new AgentSequenceLlm("{\"step\":\"continue\",\"inner\":\"新产生的关注需要延续\",\"reply\":\"不应回放的中间草稿\",\"actions\":[{\"call_id\":\"read-1\",\"capability_id\":\"test.read\",\"arguments\":[{\"name\":\"query\",\"value\":\"具体资料\"}]}]}",
            "{\"step\":\"finish\",\"reply\":\"看到了。\"}");
        await new AgentLoopLogic(continuation).RunAsync(turn, "", () => new() { tool }, (_, _) => Task.FromResult(
            new TraceCapabilityResultData { Status = "success", Payload = "唯一实际检索结果" }), default);
        Require(!continuation.Requests[1].Contains("不应回放的中间草稿") &&
            continuation.Requests[1].Contains("新产生的关注需要延续") &&
            continuation.Requests[1].Split("唯一实际检索结果").Length == 2 && continuation.Requests[1].Contains("read-1"),
            "续推仅回放一次可关联的真实调用与结果，不复制整份输出或草稿");
        var pending = new AgentTurnStateLogic();
        pending.Update(new AgentOutputData { sleep = true, next_heartbeat_minutes = 30 }, new[] { "sleep", "next_heartbeat_minutes" });
        pending.Update(new AgentOutputData { sleep = false, next_heartbeat_minutes = 0 }, new[] { "sleep", "next_heartbeat_minutes" });
        Require(pending.Values["sleep"] is false && (int)pending.Values["next_heartbeat_minutes"] == 0,
            "后续状态提案可撤回睡眠或时间，不能只累加 true 和非零值");
        var statePrelude = "{\"step\":\"continue\",\"sleep\":true,\"mood_changed\":true,\"mood\":\"轻松\",\"inner\":\"想分享这一刻\",\"next_heartbeat_minutes\":90,\"actions\":[{\"call_id\":\"state-1\",\"capability_id\":\"test.read\",\"arguments\":[{\"name\":\"query\",\"value\":\"一次\"}]}]}";
        var middle = "{\"step\":\"continue\",\"actions\":[{\"call_id\":\"state-2\",\"capability_id\":\"test.read\",\"arguments\":[{\"name\":\"query\",\"value\":\"二次\"}]}]}";
        foreach (var mode in new[] { "inherit", "wake", "clear" })
        {
            var clear = mode == "clear";
            var sequence = new AgentSequenceLlm(statePrelude, middle, clear
                ? "{\"step\":\"finish\",\"reply\":\"好了\",\"sleep\":false,\"next_heartbeat_minutes\":0,\"inner\":\"\",\"attention_links\":[]}"
                : mode == "wake" ? "{\"step\":\"finish\",\"reply\":\"好了\",\"sleep\":false}"
                : "{\"step\":\"finish\",\"reply\":\"好了\"}");
            var resolved = await new AgentLoopLogic(sequence).RunAsync(turn, "", () => new() { tool },
                (_, _) => Task.FromResult(new TraceCapabilityResultData { Status = "success" }), default);
            Require(resolved.sleep == (mode == "inherit") && resolved.next_heartbeat_minutes == (mode == "wake" ? 90 : 0) &&
                    resolved.inner == (clear ? "" : "想分享这一刻") && resolved.mood_changed && resolved.mood == "轻松" &&
                    resolved.actions.Count == 0 && sequence.Requests.Count == 3,
                "跨两轮行动区分字段省略与显式false/0/空字符串；只继承状态，不继承 actions");
        }
        Console.WriteLine("Agent prompt checks passed: context ownership, empty sections, active executions, current receipts, concise tools and result-only continuation.");
    }

    // 只读评估旧 dump 的固定规则、能力目录和账本投影；不打印或保存聊天/身份/记忆原文，不调用模型。
    private static void RunAgentPromptAudit(string directory)
    {
        var options = new JsonSerializerOptions { IncludeFields = true };
        var results = new List<object>();
        foreach (var file in Directory.GetFiles(directory, "*-request.json").OrderBy(x => x, StringComparer.Ordinal).TakeLast(24))
        {
            using var request = JsonDocument.Parse(File.ReadAllText(file));
            var messages = request.RootElement.GetProperty("messages").EnumerateArray()
                .Where(x => x.GetProperty("content").ValueKind == JsonValueKind.String)
                .Select(x => x.GetProperty("content").GetString()).ToList();
            var stable = messages.FirstOrDefault(x => x.StartsWith("【Agent 当下】", StringComparison.Ordinal));
            var dynamic = messages.FirstOrDefault(x => x.Contains("【当前可用能力】") && x.Contains("【执行账本："));
            if (stable == null || dynamic == null) continue;
            var capStart = dynamic.IndexOf("【当前可用能力】", StringComparison.Ordinal);
            var ledgerStart = dynamic.IndexOf("【执行账本：", capStart, StringComparison.Ordinal);
            using var catalogJson = JsonDocument.Parse(dynamic.Substring(capStart + "【当前可用能力】".Length, ledgerStart - capStart - "【当前可用能力】".Length));
            var catalog = catalogJson.RootElement.EnumerateArray().Select(x => new TraceContributionDescriptorData
            {
                Id = x.GetProperty("id").GetString(), Description = x.GetProperty("description").GetString(),
                BodyId = x.GetProperty("body_id").GetString(), Organ = x.GetProperty("organ").GetString(),
                Kind = string.IsNullOrEmpty(x.GetProperty("organ").GetString()) ? TraceContributionKindValues.CallableNerve : TraceContributionKindValues.Effector,
                ParametersJsonSchema = x.GetProperty("parameters").GetString(),
                HasExternalSideEffect = x.GetProperty("external_effect").GetBoolean(),
                WhenToUse = x.GetProperty("when_to_use").GetString(), WhenNotToUse = x.GetProperty("when_not_to_use").GetString()
            }).ToList();
            var arrayStart = dynamic.IndexOf('[', ledgerStart);
            var bytes = Encoding.UTF8.GetBytes(dynamic.Substring(arrayStart));
            var reader = new Utf8JsonReader(bytes);
            using var ledgerJson = JsonDocument.ParseValue(ref reader);
            var ledgerLength = Encoding.UTF8.GetCharCount(bytes, 0, (int)reader.BytesConsumed);
            var entries = JsonSerializer.Deserialize<List<TraceExecutionSnapshotData>>(ledgerJson.RootElement.GetRawText(), options);
            if (!entries.Any(x => !TraceExecutionRegistry.Terminal(x.Status))) catalog.RemoveAll(x => x.Id == "execution.cancel");
            var oldParts = stable.Length + (arrayStart + ledgerLength - capStart);
            var newParts = AgentLoopPrompts.Header.Length + 1 + AgentLoopLogic.BuildStable(null).Length +
                AgentPromptContextLogic.Catalog(catalog).Length + AgentPromptContextLogic.Executions(entries).Length +
                (catalog.Any(x => x.Id == "web.search" || x.Id == "web.read") ? AgentLoopPrompts.Search.Length + 1 : 0);
            results.Add(new { request = Path.GetFileName(file), original_chars = messages.Sum(x => x.Length),
                projected_chars = messages.Sum(x => x.Length) - oldParts + newParts, old_sections = oldParts, new_sections = newParts,
                completed_ledger_entries = entries.Count(x => TraceExecutionRegistry.Terminal(x.Status)) });
        }
        Require(results.Count > 0, "未找到可统计的旧版 Agent 请求");
        Console.WriteLine(JsonSerializer.Serialize(new { note = "UTF-16 chars; only rules/catalog/ledger replaced; private context unchanged; not a live model test", results }));
    }
}
