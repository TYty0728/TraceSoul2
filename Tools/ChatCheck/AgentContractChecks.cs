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
        var schemaText = AgentOutputContractLogic.Prompt;
        using var schema = JsonDocument.Parse(schemaText.Substring(schemaText.IndexOf('{')));
        var properties = schema.RootElement.GetProperty("properties");
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
        Console.WriteLine("Agent contract checks passed: independent DTO/schema, retired fields, typed repairs, nested arguments, heartbeat and legacy protocol isolation.");
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
