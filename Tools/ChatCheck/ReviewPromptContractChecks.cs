using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Logic;
using TraceSoul2.Util;

internal static partial class Program
{
    private static void RunReviewPromptContractChecks(Assembly assembly)
    {
        var detail = "summary字段错误，允许修正的内容：" + new string('测', 300) + "反馈末尾必须保留";
        var draft = TraceJson.ToJson(new BrainCognitionWriteData { summary = "原稿" });
        var repairCalls = 0;
        var repairClient = new SelectionProbeLlm { Reply = messages => {
            if (++repairCalls == 1) return draft;
            Require(messages.Any(m => m.role == "assistant" && m.content == draft) && messages.Last().content.Contains(detail),
                "局部纠正保留完整失败草稿和超过240字符的字段反馈");
            return TraceJson.ToJson(new BrainCognitionWriteData { summary = "修正稿" });
        } };
        var repaired = DeepSeekStructuredOutputLogic.CompleteAsync<BrainCognitionWriteData>(repairClient,
            new() { new("system", "合成纠正测试"), new("user", "合成材料") }, null, "输出无效", default,
            validationError: x => x.summary == "原稿" ? detail : null, preserveValidContent: true).GetAwaiter().GetResult();
        Require(repairCalls == 2 && repaired.summary == "修正稿", "局部纠正模式只增加一次请求");
        // Real pipeline repairs must distinguish missing content, count errors and overruns.
        foreach (var mode in new[] { "empty", "long", "dimensions", "examples", "root" })
        {
            using var f = new DailyFixture(assembly) { ExistingEngine = "legacy" }; f.Seed(2); var calls = 0;
            f.Run(new SelectionProbeLlm { Reply = m => {
                if (!m[0].content.Contains("1/4")) return DailyReply(m);
                if (++calls == 2)
                {
                    var repair = m.Last().content;
                    var field = mode switch { "dimensions" => "dimension_ids", "examples" => "positive_examples", "root" => "JSON", _ => "summary" };
                    Require(repair.Contains(field) && (repair.Contains("新正文必须更短") == (mode == "long")),
                        "空值/数组数量/根类型不能误走压缩；真实超长才要求缩短");
                    return DailyEvents;
                }
                if (mode == "root") return "[]";
                var obj = JsonNode.Parse(DailyEvents); var e = obj["events"][0];
                if (mode == "empty") e["summary"] = "";
                if (mode == "long") e["summary"] = new string('字', 601);
                if (mode == "dimensions") e["new_tags"][0]["dimension_ids"] = JsonNode.Parse("[\"owner\",\"subject\",\"about\",\"predicate\",\"object\",\"scope\",\"context\",\"quality\",\"time\"]");
                if (mode == "examples") e["new_tags"][0]["positive_examples"] = new JsonArray();
                return obj.ToJsonString();
            } });
            Require(calls == 2 && f.Count("event_entries") == 2, "一次纠正成功后只保存有效事件");
        }
        using (var f = new DailyFixture(assembly) { ExistingEngine = "legacy" })
        {
            f.Seed(2);
            f.Run(new SelectionProbeLlm { Reply = m => {
                if (m[0].content.Contains("4/4"))
                {
                    using var input = JsonDocument.Parse(m[1].content);
                    var node = input.RootElement.GetProperty("cognitions")[0];
                    Require(node.GetProperty("scope").GetString() == "早餐选择" &&
                        node.GetProperty("exceptions").GetString() == "意愿变化后重新确认" && node.TryGetProperty("about", out _) &&
                        m[0].content.Contains("完整新版"), "实际卡片请求包含限定与替换语义");
                }
                return DailyReply(m);
            } });
            // Reuse yesterday's node; duplicate target feedback must use the model's ref contract.
            var day = "2020-08-21"; var nextSources = f.Seed(2, day); var calls = 0;
            nextSources[0].Content = f.Graph.GetCognitionNodes(20).Single().Summary;
            using (var db = new SQLite.SQLiteConnection(f.BrainPath)) db.Update(nextSources[0]);
            f.Run(new SelectionProbeLlm { Reply = m => {
                if (!m[0].content.Contains("3/4")) return DailyReply(m);
                var obj = JsonNode.Parse(DailyCognition); var item = obj["cognitions"][0];
                item["operation"] = "reinforce"; item["target_ref"] = 1;
                if (++calls == 1) obj["cognitions"].AsArray().Add(item.DeepClone());
                else Require(m.Last().content.Contains("target_ref") && !m.Last().content.Contains("target_id"),
                    "底层重复目标错误转换为模型可见字段");
                return obj.ToJsonString();
            } }, day);
            Require(calls == 2 && f.Graph.GetCognitionNodes(20).Count == 1, "印证复用旧节点，无重复创建");
        }

        var method = assembly.GetType("TraceSoul2.Migrate.DayBuilder", true)
            .GetMethod("GenerateJevDraft", BindingFlags.NonPublic | BindingFlags.Static);
        foreach (var sample in new[] {
            ("{}", "summary缺失"),
            ("{\"summary\":12,\"scope\":\"\",\"exceptions\":\"\"}", "summary必须为字符串"),
            (TraceJson.ToJson(new { summary = new string('字', 601), scope = "", exceptions = "" }), "summary当前601字符"),
            ("{\"summary\":\"\",\"scope\":\"限定\",\"exceptions\":\"\"}", "summary为空时"),
            ("{\"summary\":\"理解\",\"scope\":\"\",\"exceptions\":\"\",\"operation\":\"create\"}", "三个不同字段") })
        {
            var calls = 0;
            var llm = new SelectionProbeLlm { Reply = m => {
                if (++calls == 1) return sample.Item1;
                Require(m.Last().content.Contains(sample.Item2) && !m.Last().content.Contains("最多600"),
                    "JEV草稿具体指出字段、错误原因与较低上限");
                return JevDraftText;
            } };
            ((Task)method.Invoke(null, new object[] { llm, new { evidence = "合成测试" } })).GetAwaiter().GetResult();
            Require(calls == 2, "JEV草稿只纠正一次");
        }
        RunPublicEvidenceBoundCheck(assembly);
        Console.WriteLine("Review prompt contracts passed: typed/count/length repair, card conditions, mapped refs, JEV diagnostics and public evidence merging.");
    }

    private static void RunPublicEvidenceBoundCheck(Assembly assembly)
    {
        using var f = new DailyFixture(assembly) { ExistingEngine = "legacy" };
        var old = f.Seed(1, visibility: "public", offset: -1).Single();
        old.Content = "共享空间保持安静";
        using (var db = new SQLite.SQLiteConnection(f.BrainPath)) db.Update(old);
        var initial = new BrainCognitionWriteData { operation = "create", summary = "共享空间保持安静", domains = new() { "world" },
            identity_slot = "", evidence_moment_ids = new() { old.Id }, confidence = .6f, strength = .4f };
        var node = ((List<CognitionSliceRecord>)f.StoreCall("CommitCognitions", old.Id, new[] { initial })).Single();
        old.MemoryStatus = "built";
        using (var db = new SQLite.SQLiteConnection(f.BrainPath)) db.Update(old);
        var sources = f.Seed(250, visibility: "public");
        using (var db = new SQLite.SQLiteConnection(f.BrainPath))
            foreach (var source in sources) { source.Content = "共享空间保持安静"; db.Update(source); }
        var batches = new List<int>();
        var llm = new SelectionProbeLlm { Reply = m => {
            Require(!m[0].content.Contains("认知写入前比较") && !m[0].content.Contains(node.Id), "公开生成不包含旧认识，也不追加比较请求");
            Require(m[0].content.Contains("【公开经历复盘】"), "公开测试不得进入私密复盘");
            var shown = sources.Where(s => m[0].content.Contains(s.Id + "｜")).ToList();
            batches.Add(shown.Count);
            Require(shown.Count == 250, "公开环境250条完整原句一次进入模型，不按200/16拆请求");
            return TraceJson.ToJson(new { cognitions = new[] { new BrainCognitionWriteData {
                operation = "create", summary = "当日公开新认识", domains = new() { "world" }, identity_slot = "",
                evidence_moment_ids = shown.Select(s => s.Id).ToList(), confidence = .7f, strength = .5f } }.Concat(
                    Enumerable.Range(1, 16).Select(i => new BrainCognitionWriteData {
                        operation = "create", summary = "公开认识" + i + new string('知', 650), domains = new() { "world" }, identity_slot = "",
                        scope = new string('域', 350), evidence_moment_ids = shown.Select(s => s.Id).ToList(), confidence = .7f, strength = .5f
                    })).ToList() });
        } };
        f.Run(llm);
        Require(batches.SequenceEqual(new[] { 250 }) && llm.Requests.Count == 1 && f.Graph.GetCognitionNodes(20).Count == 18,
            "公开250条来源和17项操作一次完成，不再切小批、比较或压缩");
        using (var db = new SQLite.SQLiteConnection(f.BrainPath))
            Require(db.ExecuteScalar<int>("SELECT COUNT(DISTINCT MomentId) FROM cognition_evidence WHERE CognitionId=?", node.Id) == 1 &&
                db.ExecuteScalar<int>("SELECT COUNT(*) FROM cognition_evidence") == 1 + 17 * 250,
                "新认识完整绑定250条来源，旧认知证据不变");
        var current = f.Graph.GetCognitionNodes(20).Single(n => n.Id == node.Id);
        Require(current.Revision == node.Revision && current.Strength == node.Strength,
            "不读旧认知的日复盘不推测历史目标或修改其权重");
        f.Reopen(); var noCalls = new AgentSequenceLlm(); f.Run(noCalls);
        Require(noCalls.Requests.Count == 0, "已提交公开批次跨重开不重放");
    }
}
