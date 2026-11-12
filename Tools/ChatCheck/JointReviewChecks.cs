using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using TraceSoul2.Data;
using TraceSoul2.Util;

internal static partial class Program
{
    private static void RunJointReviewChecks(Assembly assembly)
    {
        foreach (var version in new[] { 7, 8 })
        foreach (var mode in new[] { "normal", "no_cognition", "empty", "bad_ref", "cross_conversation", "rollback", "resume", "world", "single_ref", "duplicate_id", "missing_id" })
        {
            if (version == 7 && mode is "duplicate_id" or "missing_id") continue;
            using var f = new DailyFixture(assembly) { ExistingEngine = version == 7 ? "batch-v1" : null, ExistingVersion = version == 7 ? 7 : null };
            var sources = f.Seed(3); sources[0].Content = "联合阅读全文标记" + new string('原', 12000);
            sources[0].Type = "expression"; sources[0].Role = "assistant";
            sources[1].Type = "expression"; sources[1].Role = "assistant"; sources[1].SourceEventId = "night-residue:2020-08-19";
            sources[2].Type = "message";
            if (mode == "cross_conversation") sources[2].ConversationId = "另一会话";
            using (var db = new SQLite.SQLiteConnection(f.BrainPath))
            {
                foreach (var source in sources) db.Update(source);
                foreach (var slot in new[] { "personality", "self", "other", "relation" })
                    db.Execute("UPDATE identity_cards SET Body=?,Origin='manual' WHERE Slot=?", "联合四卡_" + slot, slot);
            }
            var calls = new int[4]; var failOnce = mode == "resume"; string prefix = null;
            var llm = new SelectionProbeLlm { Reply = messages => {
                var system = messages[0].content;
                var stage = Enumerable.Range(1, 3).Single(n => system.Contains("【" + n + "/3")); calls[stage]++;
                var identity = system.Substring(0, system.IndexOf("【" + stage + "/3", StringComparison.Ordinal)); prefix ??= identity;
                Require(prefix == identity && new[] { "personality", "self", "other", "relation" }.All(s => prefix.Contains("联合四卡_" + s)),
                    "联合阅读、回顾、状态保持同一四卡视角");
                if (stage == 1)
                {
                    Require(messages[1].content.Contains(sources[0].Content), "联合阅读完整输入双方来源");
                    using var input = JsonDocument.Parse(messages[1].content);
                    var rows = ReadReviewSources(input.RootElement);
                    if (version == 8)
                    {
                        Require(rows.All(r => r.GetProperty("recorded_at").GetString().EndsWith("+08:00")) &&
                            rows.Select((r, i) => DateTimeOffset.Parse(r.GetProperty("recorded_at").GetString(),
                                System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds() == sources[i].CreatedUnixMs).All(x => x),
                            "记录时间以含毫秒与UTC+8的可读日期无损呈现");
                        Require(rows[0].GetProperty("source_nature").GetString().StartsWith("助手发出的表达") &&
                            rows[1].GetProperty("source_nature").GetString().StartsWith("夜间余温回顾来源") &&
                            rows[2].GetProperty("source_nature").GetString().StartsWith("收到的消息") &&
                            rows[0].GetProperty("role").GetString() == "assistant" && rows[1].GetProperty("role").GetString() == "assistant",
                            "已知回顾标记区别于普通表达，助手主动连续消息完整保留");
                    }
                    else Require(rows.All(r => r.GetProperty("time").ValueKind == JsonValueKind.Number), "Version7保持原始时间协议");
                    if (mode == "empty") return "{\"events\":[],\"cognitions\":[]}";
                    return TraceJson.ToJson(new {
                        events = new[] {
                            new { event_ref = mode == "missing_id" ? 0 : 40, summary = "原事件一：我们交流顾虑。", source_refs = new[] { 1, 2 } },
                            new { event_ref = mode == "duplicate_id" ? 40 : 20, summary = "原事件二：我们一起散步。", source_refs = new[] { 3 } }
                        },
                        cognitions = mode == "no_cognition" ? Array.Empty<object>() : new object[] { new {
                            summary = "我新明白，安心来自彼此愿意表达和听见。", identity_slot = mode == "world" ? "" : "relation",
                            event_refs = mode == "bad_ref" ? new[] { 1, 99 } : mode == "single_ref" ? new[] { version == 8 ? 20 : 2 }
                                : version == 8 ? new[] { 20, 40 } : new[] { 1, 2 }
                        } }
                    });
                }
                Require(!messages.Any(m => m.content.Contains("联合阅读全文标记")), "只有联合阅读请求包含原始全文");
                using var doc = JsonDocument.Parse(messages[1].content); var root = doc.RootElement;
                if (stage == 2)
                {
                    Require(system.Contains("日终回顾") && root.GetProperty("events").GetArrayLength() == 2 &&
                        root.GetProperty("cognitions").GetArrayLength() == (mode == "no_cognition" ? 0 : 1),
                        "同次产出的独立事件与认知进入回顾，不逐事件再请求认知");
                    if (failOnce) { failOnce = false; throw new InvalidOperationException("回顾中断"); }
                    var refs = root.GetProperty("cognitions").EnumerateArray().Select(n => n.GetProperty("cognition_ref").GetInt32()).ToArray();
                    return TraceJson.ToJson(new { summary = "今天我们说开顾虑，也一起散步，我更理解了彼此倾听的意义。", cognition_refs = refs });
                }
                Require(root.TryGetProperty("reflection", out _) && !root.TryGetProperty("events", out _) && !root.TryGetProperty("cognitions", out _),
                    "状态仅接收回顾而不重发前两份列表");
                return "{\"cards\":[],\"runtime\":{\"mood\":\"踏实\"}}";
            } };
            if (mode == "rollback")
            {
                using (var db = new SQLite.SQLiteConnection(f.BrainPath)) db.Execute(
                    "CREATE TRIGGER fail_joint BEFORE INSERT ON cognition_slices BEGIN SELECT RAISE(ABORT,'joint write failure'); END");
                var rejected = false; try { f.Run(llm); } catch (SQLite.SQLiteException) { rejected = true; }
                Require(rejected && f.Count("event_entries") == 2 && f.Graph.GetCognitionNodes(20).Count == 0,
                    "认知写入故障回滚认知，事件和同次生成的待写草稿已持久化");
                using (var state = f.State()) Require(state.RootElement.GetProperty("JointDraft").ValueKind == JsonValueKind.Object &&
                    state.RootElement.GetProperty("CognitionEvents").GetArrayLength() == 0, "待写草稿与未完成标记一致");
                using (var db = new SQLite.SQLiteConnection(f.BrainPath)) db.Execute("DROP TRIGGER fail_joint");
                f.Reopen();
            }
            if (mode == "resume")
            {
                Require(DailyRejects(() => f.Run(llm)) && f.Graph.GetCognitionNodes(20).Count == 1, "回顾失败不丢联合阅读成果");
                f.Reopen();
            }
            if (mode is "bad_ref" or "cross_conversation" or "duplicate_id" or "missing_id")
            {
                Require(DailyRejects(() => f.Run(llm)) && calls[1] == 2 && calls[2] == 0 &&
                    f.Count("event_entries") == 0 && f.Graph.GetCognitionNodes(20).Count == 0,
                    "联合输出先整体校验，非法认知引用不提前写事件，至多一次纠正");
                continue;
            }
            f.Run(llm);
            Require(calls[1] == 1 && calls[2] == (mode == "empty" ? 0 : mode == "resume" ? 2 : 1) && calls[3] == (mode == "empty" ? 0 : 1),
                "正常三次，认知落库恢复不重复全文调用，空结果按需省略后续");
            using (var state = f.State()) Require(state.RootElement.GetProperty("Version").GetInt32() == version &&
                state.RootElement.GetProperty("Done").GetBoolean() && state.RootElement.GetProperty("JointDraft").ValueKind == JsonValueKind.Null,
                "联合阅读版本完成后清除待写草稿");
            if (mode is not "empty" and not "no_cognition")
            {
                var node = f.Graph.GetCognitionNodes(20).Single();
                Require(f.Graph.GetCognitionEvidence(new[] { node.Id }).Select(e => e.MomentId).ToHashSet()
                    .SetEquals((mode == "single_ref" ? sources.Skip(2) : sources).Select(s => s.Id)),
                    "认知按明确编号而非位置关联依据，支持乱序、不连续编号与单事件引用");
                Require(node.Domains == (mode == "world" ? "world" : "relation") && string.IsNullOrEmpty(node.Scope) && string.IsNullOrEmpty(node.Exceptions),
                    "分类域由程序映射，不要求模型给每个理解制造范围与例外");
            }
            f.Reopen(); var done = new AgentSequenceLlm(); f.Run(done); Require(done.Requests.Count == 0, "完成后重开零重复");
        }
        Console.WriteLine("Joint review passed: one full read for independent events and insights, three requests, references, durable draft and resume.");
    }
}
