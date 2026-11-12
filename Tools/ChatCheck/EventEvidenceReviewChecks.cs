using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using TraceSoul2.Data;
using TraceSoul2.Util;

internal static partial class Program
{
    private static void RunEventEvidenceReviewChecks(Assembly assembly)
    {
        foreach (var mode in new[] { "normal", "merge", "empty_selection", "no_cognition", "no_quotes", "bad_quote", "quote_source", "ambiguous_quote", "mixed_quotes", "bad_event_ref", "cross_conversation", "rollback" })
        {
            using var f = new DailyFixture(assembly) { ExistingVersion = 4 };
            var sources = f.Seed(3);
            var fullOnly = "只应在第一步出现的完整正文" + new string('原', 12000) + "全文结尾";
            sources[0].Content = fullOnly;
            sources[1].Content = "我愿意听你说。"; sources[1].Role = "assistant";
            sources[1].EvidenceType = EvidenceTypeValues.DialogueExplicit;
            sources[2].Content = "今天散步很开心。";
            if (mode == "ambiguous_quote") sources[0].Content = fullOnly += "愿意听你说";
            if (mode == "cross_conversation") sources[2].ConversationId = "另一个私密会话";
            using (var db = new SQLite.SQLiteConnection(f.BrainPath)) foreach (var m in sources) db.Update(m);
            var stageCalls = new int[5];
            var llm = new SelectionProbeLlm { Reply = messages => {
                var title = messages[0].content;
                var stage = Enumerable.Range(1, 4).Single(i => title.Contains(i + "/4")); stageCalls[stage]++;
                if (stage == 1)
                {
                    using var input = JsonDocument.Parse(messages[1].content);
                    var rows = ReadReviewSources(input.RootElement);
                    Require(rows.Count == 3 && rows[0].GetProperty("text").GetString() == fullOnly &&
                        rows[1].GetProperty("role").GetString() == "assistant", "新流程第一步完整读取双方及主动表达");
                    return TraceJson.ToJson(new { events = new[] {
                        new { summary = "双方谈到遇到顾虑时愿意及时说出来。", source_refs = new[] { 1, 2 },
                            quotes = mode == "no_quotes" ? null : mode == "mixed_quotes"
                                ? new[] { "愿意听你说", "未说过的承诺", "今天散步", "愿意听你说", null, " " }
                                : new[] { mode == "bad_quote" ? "未说过的承诺" : mode == "quote_source" ? "今天散步" : "愿意听你说" } },
                        new { summary = "一起散步，留下轻松的感受。", source_refs = new[] { 3 }, quotes = Array.Empty<string>() }
                    } });
                }
                Require(!messages.Any(m => m.content.Contains("只应在第一步出现的完整正文") || m.content.Contains("全文结尾")),
                    "选择、认知和状态阶段均无全量原文再注入");
                if (stage == 2) return TraceJson.ToJson(new {
                    selected_refs = mode == "empty_selection" ? Array.Empty<int>() : mode is "merge" or "cross_conversation" ? new[] { 1, 2 } : new[] { 1 },
                    focus = "理解双方遇到顾虑时的表达与回应。"
                });
                if (stage == 3)
                {
                    using var input = JsonDocument.Parse(messages[1].content);
                    var root = input.RootElement; var e = root.GetProperty("events")[0];
                    Require(!root.TryGetProperty("evidence", out _) && !root.TryGetProperty("existing", out _) &&
                        !e.TryGetProperty("source_refs", out _) && root.GetProperty("focus").GetString().Contains("顾虑"),
                        "第三步只带事件、关键原话、判断方向和主题，原始来源编号由程序保管");
                    Require(messages[1].content.Length < 4000, "短事件不会因其关联的长原文膨胀第三步输入");
                    var omitted = mode is "no_quotes" or "bad_quote" or "quote_source" or "ambiguous_quote";
                    Require(e.GetProperty("quotes").GetArrayLength() == (omitted ? 0 : 1),
                        "可选引语仅保留本事件内逐字且来源唯一的片段，并去重；无法定位不引发整批重试");
                    if (!omitted) Require(e.GetProperty("quotes")[0].GetProperty("text").GetString() == "愿意听你说" &&
                        e.GetProperty("quotes")[0].GetProperty("role").GetString() == "assistant", "关键原话与真实表达者一起传递");
                    if (mode == "no_cognition") return "{\"cognitions\":[]}";
                    if (stageCalls[3] == 2 && mode != "rollback") Require(messages[^1].content.Contains("event_refs"), "引用纠正使用本轮实际可见的事件字段");
                    return TraceJson.ToJson(new { cognitions = new[] { new {
                        operation = "create", target_ref = 0, summary = "双方愿意及时说出顾虑并认真回应。",
                        identity_slot = "relation", domains = new[] { "relation" },
                        event_refs = mode == "bad_event_ref" ? new[] { 99 } : mode is "merge" or "cross_conversation" ? new[] { 1, 2 } : new[] { 1 },
                        tag_refs = Array.Empty<int>(), scope = "双方相处", exceptions = ""
                    } } });
                }
                using var runtime = JsonDocument.Parse(messages[1].content);
                Require(runtime.RootElement.GetProperty("events").GetArrayLength() == 2 &&
                    messages[1].content.Contains("一起散步"), "未进入认知提炼的普通经历仍能影响当前感受");
                return "{\"cards\":[],\"runtime\":{\"mood\":\"轻松\"}}";
            } };
            if (mode == "rollback")
            {
                using (var db = new SQLite.SQLiteConnection(f.BrainPath)) db.Execute(
                    "CREATE TRIGGER fail_event_cognition BEFORE INSERT ON plugin_documents WHEN NEW.PluginId='daily-review' AND " +
                    "json_array_length(json_extract(NEW.Json,'$.CognitionEvents'))>0 BEGIN SELECT RAISE(ABORT,'injected failure'); END");
                var failed = false;
                try { f.Run(llm); } catch (SQLite.SQLiteException) { failed = true; }
                Require(failed && f.Graph.GetCognitionNodes(20).Count == 0,
                    "事件关联认知及其检查点在写入故障时一并回滚");
                using (var state = f.State()) Require(state.RootElement.GetProperty("CognitionEvents").GetArrayLength() == 0,
                    "失败提交不标记事件已提炼");
                using (var db = new SQLite.SQLiteConnection(f.BrainPath)) db.Execute("DROP TRIGGER fail_event_cognition");
                f.Reopen();
            }
            if (mode is "bad_event_ref" or "cross_conversation")
            {
                Require(DailyRejects(() => f.Run(llm)) && f.Graph.GetCognitionNodes(20).Count == 0,
                    "越界引用和跨会话依据仍拒绝提交");
                Require(stageCalls[3] == 2,
                    "事实或来源错误只纠正一次，保存边界明确");
                continue;
            }
            f.Run(llm);
            using (var state = f.State()) Require(state.RootElement.GetProperty("Version").GetInt32() == 4 &&
                state.RootElement.GetProperty("Done").GetBoolean(), "新日期使用Version4并正常完成");
            Require(stageCalls[1] == 1 && stageCalls[2] == 1 && stageCalls[4] == 1 &&
                stageCalls[3] == (mode == "empty_selection" ? 0 : mode == "rollback" ? 2 : 1),
                "全文只读一次，各后续环节一次；恢复仅补失败的认知步骤");
            var nodes = f.Graph.GetCognitionNodes(20);
            if (mode is "empty_selection" or "no_cognition") Require(nodes.Count == 0, "日常经历可以只保存而没有认知");
            else
            {
                var ids = f.Graph.GetCognitionEvidence(nodes.Select(n => n.Id)).Select(e => e.MomentId).ToHashSet();
                Require(nodes.Count == 1 && ids.SetEquals(mode == "merge" ? sources.Select(m => m.Id) : new[] { sources[0].Id, sources[1].Id }),
                    "认知引用事件后准确映射回原始来源，未引用事件的消息不混入依据");
            }
            f.Reopen(); var resumed = new AgentSequenceLlm(); f.Run(resumed);
            Require(resumed.Requests.Count == 0 && f.Count("event_entries") == 2, "新检查点重开后零重复请求、零重复事件");
        }
        Console.WriteLine("Event-evidence review passed: one full read, grounded quotes, event provenance, everyday runtime, validation, rollback and resume.");
    }
}
