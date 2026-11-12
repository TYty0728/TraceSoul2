using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using TraceSoul2.Data;
using TraceSoul2.Util;

internal static partial class Program
{
    private static void RunSubjectiveReviewChecks(Assembly assembly)
    {
        foreach (var mode in new[] { "normal", "empty_cognition", "empty_events", "resume", "bad_reference" })
        {
            using var f = new DailyFixture(assembly) { ExistingVersion = 5 };
            var sources = f.Seed(3);
            sources[0].Content = "完整正文只读一次" + new string('原', 12000);
            sources[1].Content = "我愿意听你说。"; sources[1].Role = "assistant";
            sources[2].Content = "一起散步。";
            var slots = new[] { "personality", "self", "other", "relation" };
            using (var db = new SQLite.SQLiteConnection(f.BrainPath))
            {
                foreach (var m in sources) db.Update(m);
                foreach (var slot in slots) db.Execute("UPDATE identity_cards SET Body=?, Origin='manual' WHERE Slot=?",
                    "当下身份视角标记_" + slot, slot);
                db.Execute("UPDATE identity_cards SET Body=? WHERE Slot='user_profile'", "额外档案标记不展开");
                db.Execute("UPDATE identity_cards SET Body=? WHERE Slot='expression_habit'", "历史语感标记不展开");
            }
            var calls = new int[4]; var failOnce = mode == "resume"; string identity = null;
            var llm = new SelectionProbeLlm { Reply = messages => {
                var system = messages[0].content;
                var stage = Enumerable.Range(1, 3).Single(i => system.Contains("【" + i + "/3")); calls[stage]++;
                var prefix = system.Substring(0, system.IndexOf("【" + stage + "/3", StringComparison.Ordinal));
                identity ??= prefix;
                Require(prefix == identity && prefix.StartsWith("我是同伴") &&
                    slots.All(s => prefix.Contains("当下身份视角标记_" + s)) && prefix.Contains("第一人称"),
                    "三轮使用同一份四张当前卡片和本人视角，恢复也不丢身份");
                Require(!messages.Any(m => m.content.Contains("额外档案标记不展开") || m.content.Contains("历史语感标记不展开")),
                    "仅展开四张身份卡，不额外回灌其他历史视图");
                if (stage == 1)
                {
                    Require(messages[1].content.Contains(sources[0].Content), "第一轮仍完整读取原文");
                    return mode == "empty_events" ? "{\"events\":[]}" : TraceJson.ToJson(new { events = new[] {
                        new { source_refs = new[] { 3 }, summary = "我更在意一起散步留下的轻松。", quotes = Array.Empty<string>() },
                        new { source_refs = new[] { 1, 2 }, summary = "我们说起顾虑，我愿意认真听。", quotes = new[] { "愿意听你说" } }
                    } });
                }
                Require(!messages.Any(m => m.content.Contains("完整正文只读一次")), "后两轮不重新发送完整原文");
                using var doc = JsonDocument.Parse(messages[1].content); var root = doc.RootElement;
                Require(root.GetProperty("events").GetArrayLength() == 2 && !root.TryGetProperty("existing", out _),
                    "整份速览进入后两轮，不另做筛选或历史认知检索");
                if (stage == 2)
                {
                    Require(!root.TryGetProperty("focus", out _) && !root.TryGetProperty("evidence", out _) &&
                        root.GetProperty("events")[0].GetProperty("summary").GetString().Contains("更在意") &&
                        root.GetProperty("events")[1].GetProperty("quotes")[0].GetProperty("role").GetString() == "assistant",
                        "关注排序、关键原话及真实发言者保留，来源全文留本地");
                    if (failOnce) { failOnce = false; throw new InvalidOperationException("测试中断"); }
                    if (mode == "empty_cognition") return "{\"cognitions\":[]}";
                    return TraceJson.ToJson(new { cognitions = new[] { new {
                        operation = "create", target_ref = 0, summary = "我愿意与对方直接说出顾虑并认真回应。",
                        identity_slot = "relation", domains = new[] { "relation" }, event_refs = new[] { mode == "bad_reference" ? 99 : 2 },
                        tag_refs = Array.Empty<int>(), scope = "双方相处", exceptions = ""
                    } } });
                }
                Require(!root.TryGetProperty("current", out _) && root.GetProperty("cards").EnumerateArray().All(c => !c.TryGetProperty("body", out _)),
                    "身份视角通过四卡统一注入，状态输入不再追加旧卡或旧runtime副本");
                var card = root.GetProperty("cards").EnumerateArray().FirstOrDefault(c => c.GetProperty("slot").GetString() == "relation");
                var outputCards = mode == "empty_cognition" ? Array.Empty<object>() : new object[] { new {
                    slot = "relation", body = "我与对方愿意坦诚交流顾虑。",
                    cognition_refs = new[] { card.GetProperty("allowed_cognition_refs")[0].GetInt32() }
                } };
                return TraceJson.ToJson(new { cards = outputCards, runtime = new { mood = "我感到轻松而踏实。" } });
            } };
            if (mode == "resume")
            {
                Require(DailyRejects(() => f.Run(llm)) && f.Count("event_entries") == 2, "失败保留第一轮速览");
                using (var db = new SQLite.SQLiteConnection(f.BrainPath))
                    db.Execute("UPDATE identity_cards SET Body='后来修改的自我' WHERE Slot='self'");
                f.Reopen();
            }
            if (mode == "bad_reference")
            {
                Require(DailyRejects(() => f.Run(llm)) && calls[1] == 1 && calls[2] == 2 && calls[3] == 0 &&
                    f.Graph.GetCognitionNodes(20).Count == 0, "三轮流程仍校验来源编号，仅纠正一次且不写错误认知");
                continue;
            }
            f.Run(llm);
            using (var state = f.State())
            {
                Require(state.RootElement.GetProperty("Version").GetInt32() == 5 && state.RootElement.GetProperty("Done").GetBoolean(),
                    "新日期以Version5完成三轮复盘");
                Require(state.RootElement.GetProperty("IdentityContext").GetString() == identity, "身份视角与进度共同持久化");
            }
            Require(calls[1] == 1 && calls[2] == (mode == "empty_events" ? 0 : mode == "resume" ? 2 : 1) &&
                calls[3] == (mode == "empty_events" ? 0 : 1), "正常三次，空结果省略后续，重开只续失败环节");
            if (mode is "normal" or "resume")
            {
                Require(f.Store.LoadIdentityCards(DailyFixture.Root).Single(c => c.Slot == "relation").Body == "我与对方愿意坦诚交流顾虑。",
                    "状态阶段以当日认知更新关系卡");
                var nodes = f.Graph.GetCognitionNodes(20);
                Require(f.Graph.GetCognitionEvidence(nodes.Select(n => n.Id)).Select(e => e.MomentId).ToHashSet()
                    .SetEquals(new[] { sources[0].Id, sources[1].Id }), "排序后的事件编号仍映射到准确原始依据");
            }
            f.Reopen(); var done = new AgentSequenceLlm(); f.Run(done);
            Require(done.Requests.Count == 0, "已完成三轮复盘重开零重复调用");
        }
        Console.WriteLine("Subjective review passed: three requests, four identity cards, ranked overview, provenance and resume.");
    }
}
