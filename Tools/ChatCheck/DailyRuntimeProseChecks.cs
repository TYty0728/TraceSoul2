using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using TraceSoul2.Data;
using TraceSoul2.Util;

internal static partial class Program
{
    private static string RuntimeProseDraft(string narrative, string mood = "平静", string lens = "尊重当天意愿", string body = null)
    {
        var node = JsonNode.Parse(DailyRuntime);
        node["runtime"]["narrative"] = narrative; node["runtime"]["mood"] = mood; node["runtime"]["relationship_lens"] = lens;
        if (body != null) node["cards"][0]["body"] = body;
        return node.ToJsonString();
    }
    private static bool IsRuntimeFit(List<DeepSeekMessageData> messages) => messages[0].content.Contains("单字段压缩");
    private static void RunDailyRuntimeProseChecks(Assembly assembly)
    {
        using (var f = new DailyFixture(assembly) { ExistingEngine = "legacy" })
        {
            f.Seed(2); AttachJev(f, new ReviewDecisionProbe()); var fits = 0; var drafts = 0;
            var original = new string('知', 113) + new string('，', 11);
            var llm = new SelectionProbeLlm { Reply = m => {
                if (IsRuntimeFit(m))
                {
                    fits++; using var input = JsonDocument.Parse(m[1].content); var x = input.RootElement;
                    Require(x.GetProperty("field").GetString() == "runtime.narrative" && x.GetProperty("max_length").GetInt32() == 80 &&
                        !x.TryGetProperty("hard_limit", out _) && !x.TryGetProperty("target_length", out _) &&
                        x.GetProperty("current_length").GetInt32() == (fits == 1 ? 124 : 123),
                        "局部提示只暴露80字符上限并提供真实长度，不同时给目标和容错上限");
                    return TraceJson.ToJson(new { text = fits == 1 ? new string('知', 113) + new string('，', 10) : "听见当天的早餐意愿，尊重具体选择。" });
                }
                if (m[0].content.Contains("4/4"))
                {
                    drafts++;
                    Require(m[0].content.Contains("narrative不超过80字符") && m[0].content.Contains("mood不超过50字符") &&
                        m[0].content.Contains("relationship_lens不超过130字符") && m[0].content.Contains("cards合计0～4项") &&
                        m[0].content.Contains("1～12个") && !m[0].content.Contains("120") && !m[0].content.Contains("建议目标"),
                        "初始提示只有较低硬上限，明确卡片及引用个数");
                    return RuntimeProseDraft(original);
                }
                Require(m[0].content.Contains("标点、空格、换行"), "前序步骤同样明确字符计数口径");
                return JevLlmReply(m);
            } };
            f.Run(llm);
            var live = (InnerRuntimeData)f.StoreCall("LoadOrCreateInnerRuntime", DailyFixture.Root);
            Require(drafts == 1 && fits == 2 && live.Narrative == "听见当天的早餐意愿，尊重具体选择。" && live.Mood == "平静" &&
                live.RelationshipLens == "尊重当天意愿" && f.Graph.GetCognitionNodes(20).Count == 1,
                "复现124/123字符后只压缩叙事，合格心情/关系/认知保持并完成整日");
        }
        foreach (var length in new[] { 120, 121 })
        {
            using var f = new DailyFixture(assembly) { ExistingEngine = "legacy" }; f.Seed(2); var fits = 0;
            var narrative = new string('字', length - 3) + "😀。";
            f.Run(new SelectionProbeLlm { Reply = m => {
                if (IsRuntimeFit(m)) { fits++; return "{\"text\":\"已经保留完整含义。\"}"; }
                return m[0].content.Contains("4/4") ? RuntimeProseDraft(narrative) : DailyReply(m);
            } });
            Require(fits == (length == 120 ? 0 : 1), "提示80而程序保留120容错，标点和emoji按实际字符串长度判断");
        }
        foreach (var mode in new[] { "transport", "budget" })
        {
            using var f = new DailyFixture(assembly) { ExistingEngine = "legacy" }; f.Seed(2);
            var before = TraceJson.ToJson(f.Store.LoadIdentityCards(DailyFixture.Root));
            var first = new SelectionProbeLlm { Reply = m => {
                if (IsRuntimeFit(m))
                {
                    using var input = JsonDocument.Parse(m[1].content);
                    var field = input.RootElement.GetProperty("field").GetString();
                    if (field == "runtime.mood") throw mode == "budget" ?
                        (Exception)Activator.CreateInstance(assembly.GetType("TraceSoul2.Migrate.ReviewBudgetYieldException", true)) : new InvalidOperationException("injected transport failure");
                    return TraceJson.ToJson(new { text = field == "runtime.narrative" ? "已压缩叙事" : "已压缩卡片" });
                }
                return m[0].content.Contains("4/4") ? RuntimeProseDraft(new string('知', 124), new string('静', 81), new string('近', 201), new string('他', 601)) : DailyReply(m);
            } };
            if (mode == "budget") f.Run(first); else Require(DailyRejects(() => f.Run(first)), "注入局部压缩断连");
            using (var state = f.State()) Require(!state.RootElement.GetProperty("RuntimeDone").GetBoolean() &&
                state.RootElement.GetProperty("RuntimeDraft").GetProperty("runtime").GetProperty("narrative").GetString() == "已压缩叙事" &&
                TraceJson.ToJson(f.Store.LoadIdentityCards(DailyFixture.Root)) == before, "合格草稿字段持久化，但卡片/状态尚不提前提交");
            f.Reopen(); var fields = new List<string>();
            f.Run(new SelectionProbeLlm { Reply = m => {
                Require(IsRuntimeFit(m), "重开复用草稿，不重做事件、认知或整个状态包");
                using var input = JsonDocument.Parse(m[1].content); fields.Add(input.RootElement.GetProperty("field").GetString());
                return "{\"text\":\"保持清晰。\"}";
            } });
            Require(fields.SequenceEqual(new[] { "runtime.mood", "runtime.relationship_lens" }) &&
                f.Store.LoadIdentityCards(DailyFixture.Root).Single(c => c.Slot == "other").Body == "已压缩卡片" &&
                f.Graph.GetCognitionNodes(20).Count == 1, "只补剩余超长字段，正式写入一次，已合格字段原样保留");
        }
        foreach (var invalid in new[] { "{\"text\":\" \"}", "{\"text\":\"短\",\"slot\":\"self\"}", TraceJson.ToJson(new { text = new string('长', 123) }) })
        {
            using var f = new DailyFixture(assembly) { ExistingEngine = "legacy" }; f.Seed(2); var fits = 0;
            var llm = new SelectionProbeLlm { Reply = m => {
                if (IsRuntimeFit(m)) { fits++; return invalid; }
                return m[0].content.Contains("4/4") ? RuntimeProseDraft(new string('长', 124)) : DailyReply(m);
            } };
            Require(DailyRejects(() => f.Run(llm)) && fits == 2, "空白/越权字段/持续超长只允许两次局部修复，不截字、不无限重试");
            using (var state = f.State()) Require(!state.RootElement.GetProperty("RuntimeDone").GetBoolean() &&
                state.RootElement.GetProperty("RuntimeDraft").GetProperty("runtime").GetProperty("narrative").GetString().Length == 124,
                "压缩失败仍保存原草稿，不伪装状态完成");
            f.Reopen(); var retry = new SelectionProbeLlm { Reply = m => { Require(IsRuntimeFit(m), "人工续做仅补失败字段"); return "{\"text\":\"有效叙事。\"}"; } }; f.Run(retry);
            Require(retry.Requests.Count == 1, "人工续做有机会重新压缩，不被失败缓存锁死");
        }
        using (var f = new DailyFixture(assembly) { ExistingEngine = "legacy" })
        {
            f.Seed(2);
            Require(DailyRejects(() => f.Run(new SelectionProbeLlm { Reply = m => {
                if (IsRuntimeFit(m)) throw new InvalidOperationException("injected interruption before fit");
                return m[0].content.Contains("4/4") ? RuntimeProseDraft(new string('长', 124)) : DailyReply(m);
            } })), "保存待压缩草稿后中断");
            f.Store.SaveIdentityCard(DailyFixture.Root, "other", "实时聊天更新的卡片", null);
            f.Reopen(); var fresh = new SelectionProbeLlm { Reply = m => {
                Require(!IsRuntimeFit(m) && m[0].content.Contains("4/4") && m[1].content.Contains("实时聊天更新的卡片"),
                    "身份卡版本已变，旧草稿失效，必须依据新卡重新生成");
                return EmptyDailyRuntime;
            } };
            f.Run(fresh);
            Require(fresh.Requests.Count == 1 && f.Store.LoadIdentityCards(DailyFixture.Root).Single(c => c.Slot == "other").Body == "实时聊天更新的卡片",
                "新上下文不复用旧草稿覆盖实时卡片");
        }
        Console.WriteLine("Daily runtime prose passed: character counting, prompt margins, field-only repair, durable drafts, stale-card invalidation, budget/transport resume and bounded failures.");
    }
}
