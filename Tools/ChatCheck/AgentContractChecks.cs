using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Logic;
using TraceSoul2.Manager;
using TraceSoul2.Plugins;
using TraceSoul2.Util;

internal static partial class Program
{
    private static async Task RunAgentContractChecksAsync()
    {
        Require(typeof(AgentOutputData).BaseType == typeof(object), "模型 DTO 不能继承旧心智状态");
        var schemaText = AgentOutputContractLogic.SchemaJson;
        using var schema = JsonDocument.Parse(schemaText.Substring(schemaText.IndexOf('{')));
        var properties = schema.RootElement.GetProperty("properties");
        foreach (var field in typeof(AgentOutputData).GetFields())
            Require(AgentOutputContractLogic.Prompt.Contains(field.Name), "类型提示必须覆盖真实 DTO 字段：" + field.Name);
        Require(AgentOutputContractLogic.Prompt.Contains("mood_changed, state_force, sleep: boolean") &&
            AgentOutputContractLogic.Prompt.Contains("next_heartbeat_minutes: integer"), "简明类型表不能丢失布尔和整数约束");
        foreach (var retired in new[] { "speak", "beat", "note", "archive", "review", "cognition", "image", "sticker", "voice", "voices", "tool_call", "tool_input", "tags", "query" })
            Require(!properties.TryGetProperty(retired, out _), "旧字段不能进入唯一输出结构：" + retired);
        Require(properties.EnumerateObject().Count() == typeof(AgentOutputData).GetFields().Length &&
            !properties.GetProperty("actions").GetProperty("items").GetProperty("properties").TryGetProperty("execution_id", out _),
            "schema 与模型 DTO 同源，设备执行 ID 不由模型填写");
        var legacy = TraceJson.FromJson<AgentOutputData>("{\"step\":\"finish\",\"reply\":\"我在。\",\"speak\":\"直接回应并安抚\",\"archive\":\"归档\",\"tool_call\":{},\"beat\":\"出门\",\"image\":\"有\"}");
        var runtime = legacy.ToRuntime();
        Require(AgentLoopLogic.Valid(runtime, true, false) && !runtime.speak && !runtime.archive &&
            string.IsNullOrEmpty(runtime.tool_call) && !runtime.WantsImage(), "旧多填字段不反序列化到运行态，也不触发旧流程");

        var samples = new[]
        {
            ("mood_changed", "\"情绪变了\"", "true", "mood_changed", "布尔"),
            ("state_force", "1", "false", "state_force", "布尔"),
            ("sleep", "\"睡一会儿\"", "false", "sleep", "布尔"),
            ("refine", "null", "false", "refine", "布尔"),
            ("next_heartbeat_minutes", "\"半小时\"", "30", "next_heartbeat_minutes", "整数"),
            ("reply", "true", "\"我在。\"", "reply", "字符串"),
            ("actions", "{}", "[]", "actions", "数组"),
            ("attention_links", "[{\"cognition_ids\":\"x\"}]", "[]", "attention_links[].cognition_ids", "数组"),
            ("goal_updates", "[{\"content\":true}]", "[]", "goal_updates[].content", "字符串"),
            ("actions", "[{\"arguments\":[{\"name\":\"x\",\"value\":true}]}]", "[]", "actions[].arguments[].value", "字符串")
        };
        foreach (var (field, bad, good, path, expected) in samples)
        {
            string Build(string value) => "{\"step\":\"finish\",\"" + field + "\":" + value + "}";
            var llm = new AgentSequenceLlm(Build(bad), Build(good));
            await DeepSeekStructuredOutputLogic.CompleteAsync<AgentOutputData>(llm, new(), _ => true, "结构无效", default);
            Require(llm.Requests.Count == 2 && llm.Messages.Last().Last().content.Contains("$." + path) &&
                llm.Messages.Last().Last().content.Contains(expected), "纠正须从真实 DTO 提供字段路径和类型：" + field);
        }
        using var store = new SqliteMemoryManager(":memory:");
        store.SavePairIdentity("小雨", "小光", "雨雨");
        var services = new TracePluginServices(store, new HierarchicalVectorRouterLogic(new FakeEncoder()));
        services.MindJsonFields.Add(_ => "legacy-root-format-marker");
        services.MindPromptAppends.Add(_ => "legacy-mind-format-marker");
        var turn = new TraceTurnContext("contract", Moment("contract", HeartbeatLogic.HeartbeatContent), new(), 0, false, services);
        turn.Workspace.ContextBlocks.Add(new TraceContextBlockData { FacetId = "qq.voice.usage", Content = "legacy-expression-format-marker" });
        var background = new AgentSequenceLlm("{\"step\":\"wait\",\"speak\":\"无须回应\"}");
        var decision = await new AgentLoopLogic(background).RunAsync(turn, "", () => new(), (_, _) => throw new Exception("不应执行"), default);
        Require(!decision.speak && !background.Requests[0].Contains("speak=true") &&
            !background.Requests[0].Contains("legacy-") && background.Requests[0].Contains("唯一输出结构"),
            "后台也由代码推导开口状态，旧心跳/插件协议不能重入新契约");
        var inbound = new TraceTurnContext("contract", Moment("contract", "在吗"), new(), 0, true, services);
        var reply = new AgentSequenceLlm("{\"step\":\"finish\",\"reply\":\"我在。\",\"speak\":\"直接回应并安抚\"}");
        var answered = await new AgentLoopLogic(reply).RunAsync(inbound, "", () => new(), (_, _) => throw new Exception("不应执行"), default);
        Require(reply.Requests.Count == 1 && answered.speak && ((MindDecisionData)answered).speak,
            "遗留 speak 字符串不再导致纠正，开口值由正文推导并供旧消费者读取");
        await CheckAgentSemanticRepairAsync(inbound);
        Console.WriteLine("Agent contract checks passed: independent DTO/schema, retired fields, typed repairs, nested arguments, heartbeat and legacy protocol isolation.");
    }

    private static async Task CheckAgentSemanticRepairAsync(TraceTurnContext turn, string suppliedJson = null)
    {
        var bad = suppliedJson ?? "{\"step\":\"finish\",\"reply\":\"还未发送的草稿\",\"actions\":[{\"call_id\":\"image-1\",\"capability_id\":\"qq.imagegen.generate\",\"arguments\":[{\"name\":\"prompt\",\"value\":\"合成测试场景\"}]}]}";
        var output = TraceJson.FromJson<AgentOutputData>(bad);
        var error = AgentLoopLogic.ValidationError(output.ToRuntime(), true, false);
        Require(error?.Contains("$.step 与 $.actions") == true, "finish 与 actions 冲突必须准确定位");
        output.step = "continue";
        Require(AgentLoopLogic.Valid(output.ToRuntime(), true, false), "仅改为 continue 应通过行动契约");
        var fixedJson = JsonSerializer.Serialize(output, new JsonSerializerOptions { IncludeFields = true });
        var descriptors = output.actions.Select(x => new TraceContributionDescriptorData
            { Id = x.capability_id, Kind = TraceContributionKindValues.Effector, Organ = BodyOrganValues.Image,
                BodyId = string.IsNullOrEmpty(x.body_id) ? "qq" : x.body_id, Description = "离线测试能力" }).ToList();
        var calls = 0;
        var repaired = new AgentSequenceLlm(bad, fixedJson, "{\"step\":\"finish\",\"reply\":\"已收到模拟结果。\"}");
        await new AgentLoopLogic(repaired).RunAsync(turn, "", () => descriptors, (call, _) =>
        {
            Require(repaired.Requests.Count == 2, "纠正通过前不能执行行动");
            calls++;
            return Task.FromResult(new TraceCapabilityResultData { CallId = call.call_id, CapabilityId = call.capability_id,
                Status = "success", Summary = "模拟完成" });
        }, default);
        Require(calls == output.actions.Count && repaired.Messages[1].Last().content.Contains(error),
            "纠正须收到具体冲突原因，行动只执行一次");
        var repeated = new AgentSequenceLlm(bad, bad);
        calls = 0;
        string failure = null;
        try
        {
            await new AgentLoopLogic(repeated).RunAsync(turn, "", () => descriptors, (_, _) =>
            { calls++; throw new Exception("无效结果不能执行"); }, default);
        }
        catch (InvalidOperationException ex) { failure = ex.Message; }
        Require(calls == 0 && failure?.Contains("首次错误：" + error) == true && failure.Contains("纠正后错误：" + error),
            "连续错误须保留具体原因，且零副作用");
        foreach (var sample in new[]
        {
            ("{\"step\":\"continue\"}", "$.actions"),
            ("{\"step\":\"wait\"}", "$.step=wait"),
            ("{\"step\":\"finish\",\"refine\":true}", "$.reply"),
            ("{\"step\":\"continue\",\"refine\":true,\"actions\":[{}]}", "$.refine"),
            ("{\"step\":\"continue\",\"actions\":[{\"call_id\":\"x\",\"capability_id\":\"memory.recall\",\"arguments\":[{\"name\":\"query\"},{\"name\":\"QUERY\"}]}]}", "$.actions[0].arguments")
        })
            Require(AgentLoopLogic.ValidationError(TraceJson.FromJson<AgentOutputData>(sample.Item1).ToRuntime(), true, false)
                ?.Contains(sample.Item2) == true, "语义错误须指出相关字段：" + sample.Item2);
    }

    private static async Task RunAgentRepairDumpAsync(string path)
    {
        var raw = File.ReadAllText(path);
        var start = raw.IndexOf('{');
        Require(start >= 0, "日志没有 JSON 对象");
        string json;
        // Utf8JsonReader 不能跨 await 保存。
        using (var document = JsonDocument.Parse(raw.Substring(start).Trim().TrimEnd('`').Trim()))
            json = document.RootElement.GetRawText();
        using var store = new SqliteMemoryManager(":memory:");
        store.SavePairIdentity("小雨", "小光", "雨雨");
        var services = new TracePluginServices(store, new HierarchicalVectorRouterLogic(new FakeEncoder()));
        var turn = new TraceTurnContext("repair-dump", Moment("repair-dump", "离线测试输入"), new(), 0, true, services);
        await CheckAgentSemanticRepairAsync(turn, json);
        Console.WriteLine("Agent repair replay passed: finish/actions conflict, corrected continuation, no action before validation; no API or chat output.");
    }

    private static void RunAgentDumpReplay(string path)
    {
        var raw = File.ReadAllText(path); var start = raw.IndexOf('{');
        Require(start >= 0, "日志没有 JSON 对象");
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(raw.Substring(start)));
        using var document = JsonDocument.ParseValue(ref reader);
        var output = TraceJson.FromJson<AgentOutputData>(document.RootElement.GetRawText());
        Require(output != null && AgentLoopLogic.Valid(output.ToRuntime(), true, false), "日志未通过新 Agent 契约");
        Console.WriteLine("Agent dump replay passed (read-only; no chat text printed): " + Path.GetFileName(path));
    }
}
