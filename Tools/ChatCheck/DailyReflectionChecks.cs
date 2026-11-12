using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using TraceSoul2.Data;
using TraceSoul2.Util;

internal static partial class Program
{
    private static void RunDailyReflectionChecks(Assembly assembly)
    {
        foreach (var mode in new[] { "normal", "no_cognition", "resume", "bad_ref", "empty_reflection", "unbacked_card" })
        {
            using var f = new DailyFixture(assembly) { ExistingVersion = 6 };
            var sources = f.Seed(3);
            sources[0].Content = "全文只在第一轮出现" + new string('原', 12000);
            using (var db = new SQLite.SQLiteConnection(f.BrainPath))
            {
                db.Update(sources[0]);
                foreach (var slot in new[] { "personality", "self", "other", "relation" })
                    db.Execute("UPDATE identity_cards SET Body=?,Origin='manual' WHERE Slot=?", "四卡起点_" + slot, slot);
            }
            var calls = new int[5]; var failOnce = mode == "resume"; string identity = null;
            const string reflection = "今天我们把顾虑说开了，我新体会到认真倾听让彼此更踏实。";
            var llm = new SelectionProbeLlm { Reply = messages => {
                var system = messages[0].content;
                var stage = Enumerable.Range(1, 4).Single(i => system.Contains("【" + i + "/4")); calls[stage]++;
                var prefix = system.Substring(0, system.IndexOf("【" + stage + "/4", StringComparison.Ordinal)); identity ??= prefix;
                Require(prefix == identity && new[] { "personality", "self", "other", "relation" }.All(s => prefix.Contains("四卡起点_" + s)),
                    "包括新增回顾在内的四轮保持同一身份起点");
                if (stage == 1)
                {
                    Require(messages[1].content.Contains(sources[0].Content), "第一轮读全文");
                    return "{\"events\":[{\"summary\":\"细节材料标记：一起交流顾虑\",\"source_refs\":[1,2]},{\"summary\":\"细节材料标记：一起散步\",\"source_refs\":[3]}]}";
                }
                Require(!messages.Any(m => m.content.Contains("全文只在第一轮出现")), "后三轮不重读全文");
                using var doc = JsonDocument.Parse(messages[1].content); var root = doc.RootElement;
                if (stage == 2)
                {
                    if (mode == "no_cognition") return "{\"cognitions\":[]}";
                    return TraceJson.ToJson(new { cognitions = new[] {
                        new { operation = "create", target_ref = 0, summary = "采纳认知标记：我愿意与对方交流顾虑。", identity_slot = "relation", domains = new[] { "relation" }, event_refs = new[] { 1 }, tag_refs = Array.Empty<int>() },
                        new { operation = "create", target_ref = 0, summary = "未采纳认知标记：我在散步中感到轻松。", identity_slot = "relation", domains = new[] { "relation" }, event_refs = new[] { 2 }, tag_refs = Array.Empty<int>() }
                    } });
                }
                if (stage == 3)
                {
                    Require(root.GetProperty("events").GetArrayLength() == 2 && root.TryGetProperty("cognitions", out var c),
                        "回顾环节负责收拢事件与本轮认知");
                    var selected = root.GetProperty("cognitions").EnumerateArray().Where(n => n.GetProperty("summary").GetString().StartsWith("采纳认知标记"))
                        .Select(n => n.GetProperty("cognition_ref").GetInt32()).ToArray();
                    return TraceJson.ToJson(new { summary = mode == "empty_reflection" ? " " : reflection,
                        cognition_refs = mode == "bad_ref" ? new[] { 99 } : selected });
                }
                Require(root.GetProperty("reflection").GetString() == reflection && !root.TryGetProperty("events", out _) &&
                    !root.TryGetProperty("cognitions", out _) && !messages.Any(m => m.content.Contains("细节材料标记") || m.content.Contains("认知标记")),
                    "最终环节只读回顾与身份视角，不再次收到事件或认知正文");
                Require(root.GetProperty("cards").EnumerateArray().All(c => !c.TryGetProperty("allowed_cognition_refs", out _)),
                    "最终模型不需要猜未见过的认知编号，来源由程序关联");
                if (failOnce) { failOnce = false; throw new InvalidOperationException("最终环节测试中断"); }
                if (mode == "no_cognition") return "{\"cards\":[],\"runtime\":{\"mood\":\"踏实\"}}";
                return TraceJson.ToJson(new { cards = new[] { new { slot = mode == "unbacked_card" ? "self" : "relation", body = "我们愿意说出顾虑，也愿意听见彼此。" } },
                    runtime = new { narrative = "我想把心里的话坦然告诉对方。", mood = "踏实" } });
            } };
            if (mode == "resume")
            {
                Require(DailyRejects(() => f.Run(llm)), "最终环节中断被保留");
                using (var state = f.State()) Require(state.RootElement.GetProperty("Reflection").GetProperty("summary").GetString() == reflection,
                    "已完成回顾持久化，可独立恢复最终环节");
                f.Reopen();
            }
            if (mode is "bad_ref" or "empty_reflection" or "unbacked_card")
            {
                Require(DailyRejects(() => f.Run(llm)) && (mode == "unbacked_card" ? calls[4] == 2 : calls[3] == 2 && calls[4] == 0),
                    "无效回顾/来源/无依据卡片仅纠正一次，最终状态不提前提交");
                using var state = f.State(); Require(!state.RootElement.GetProperty("RuntimeDone").GetBoolean(), "失败不完成状态");
                continue;
            }
            f.Run(llm);
            Require(calls[1] == 1 && calls[2] == 1 && calls[3] == 1 && calls[4] == (mode == "resume" ? 2 : 1),
                "正常四轮，恢复只续最终环节，不重读或重做回顾");
            using (var state = f.State())
            {
                Require(state.RootElement.GetProperty("Version").GetInt32() == 6 && state.RootElement.GetProperty("Done").GetBoolean(),
                    "Version6完成回顾后更新状态");
                if (mode != "no_cognition")
                {
                    Require(state.RootElement.GetProperty("RuntimeDraft").GetProperty("cards")[0].GetProperty("cognition_refs").GetArrayLength() == 1,
                        "自动关联只包含回顾实际采纳的同slot认知");
                    var card = f.Store.LoadIdentityCards(DailyFixture.Root).Single(c => c.Slot == "relation");
                    var nodes = f.Graph.GetCognitionNodes(20);
                    Require(card.Body == "我们愿意说出顾虑，也愿意听见彼此。" &&
                        card.CognitionSourcesJson.Contains(nodes.Single(n => n.Summary.StartsWith("采纳认知标记")).Id) &&
                        !card.CognitionSourcesJson.Contains(nodes.Single(n => n.Summary.StartsWith("未采纳认知标记")).Id),
                        "落卡保留回顾来源，未采纳认知不混入依据");
                }
            }
            f.Reopen(); var done = new AgentSequenceLlm(); f.Run(done); Require(done.Requests.Count == 0, "已完成重开零重复");
        }
        Console.WriteLine("Daily reflection passed: four identity views, concise final input, grounded cards, empty cognition and independent resume.");
    }
}
