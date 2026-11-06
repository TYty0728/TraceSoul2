using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Util;
using TraceSoul2.Host;
using TraceSoul2.Manager;

internal static partial class Program
{
    private sealed class ReviewDecisionProbe
    {
        public readonly List<string> Questions = new();
        public Func<JsonElement, string, string> Select;
        public Action<JsonElement, JsonElement> Before;
        public Task<JsonElement> Decide(JsonElement state, JsonElement questions, CancellationToken token)
        {
            Questions.Add(string.Join(",", questions.EnumerateObject().Select(q => q.Name)));
            Before?.Invoke(state, questions);
            var result = new Dictionary<string, object>();
            foreach (var q in questions.EnumerateObject())
            {
                var choice = Select?.Invoke(state, q.Name) ?? DefaultChoice(state, q.Name);
                result[q.Name] = new { type = "choice", choice, confidence = 1,
                    probabilities = q.Value.GetProperty("criteria").EnumerateObject().ToDictionary(x => x.Name, x => x.Name == choice ? 1.0 : 0.0) };
            }
            return Task.FromResult(JsonSerializer.SerializeToElement(new { answers = result }));
        }
        public static string DefaultChoice(JsonElement state, string question) => question switch {
            "target" => string.IsNullOrWhiteSpace(state.GetProperty("draft").GetProperty("summary").GetString()) ? "no_change" : "new",
            "operation" => "reinforce", "route" => "other", "user" => "yes",
            _ => question.StartsWith("source_") || question.StartsWith("topic_") ? "yes" : "no"
        };
    }
    private static void AttachJev(DailyFixture fixture, ReviewDecisionProbe probe) => fixture.Context.GetType()
        .GetProperty("ReviewDecisionOverride").SetValue(fixture.Context,
            new Func<JsonElement, JsonElement, CancellationToken, Task<JsonElement>>(probe.Decide));
    private const string JevDraftText = "{\"summary\":\"他早餐想吃面包，选择时应尊重当天意愿。\",\"scope\":\"早餐选择\",\"exceptions\":\"意愿变化后重新确认\"}";
    private const string JevEmptyDraft = "{\"summary\":\"\",\"scope\":\"\",\"exceptions\":\"\"}";
    private static string JevLlmReply(List<DeepSeekMessageData> messages)
    {
        if (messages[0].content.Contains("认知草稿"))
        {
            using var input = JsonDocument.Parse(messages[1].content);
            return input.RootElement.GetProperty("processed").GetArrayLength() == 0 ? JevDraftText : JevEmptyDraft;
        }
        if (messages[0].content.Contains("4/4")) return EmptyDailyRuntime;
        return DailyReply(messages);
    }
    private static void RunJevReviewChecks(Assembly assembly)
    {
        using (var f = new DailyFixture(assembly))
        {
            var sources = f.Seed(2); var probe = new ReviewDecisionProbe(); AttachJev(f, probe);
            var llm = new SelectionProbeLlm { Reply = JevLlmReply }; f.Run(llm);
            var node = f.Graph.GetCognitionNodes(20).Single();
            Require(node.IdentitySlot == "other" && node.Domains == "user" &&
                f.Graph.GetCognitionEvidence(new[] { node.Id }).All(e => e.MomentId == sources[0].Id),
                "JEV只选合法候选，程序绑定真实来源和新认识类别/领域");
            using var state = f.State();
            Require(state.RootElement.GetProperty("Version").GetInt32() == 2 &&
                state.RootElement.GetProperty("CognitionEngine").GetString() == "jev-v1" &&
                state.RootElement.GetProperty("Stamp").GetString().StartsWith("jev-v1:") && state.RootElement.GetProperty("Done").GetBoolean(),
                "JEV执行器与检查点版本固定，旧程序会因来源戳不兼容拒绝重放");
            Require(llm.Requests.Count(r => r.Contains("【3/4 认知草稿】")) == 2 && probe.Questions.Count >= 5,
                "生成模型只作单项草稿，JEV真实参与认知决策与无变化判断");
            f.Reopen(); var empty = new AgentSequenceLlm(); f.Run(empty);
            Require(empty.Requests.Count == 0 && f.Graph.GetCognitionNodes(20).Count == 1, "完成后无JEV配置也不会重跑已完成日期");
        }
        foreach (var slot in new[] { "expression_habit", "" })
        {
            using var f = new DailyFixture(assembly); f.Seed(2);
            f.Run(new AgentSequenceLlm(DailyEvents, DailySelection,
                DailyCognition.Replace("\"identity_slot\":\"other\"", "\"identity_slot\":\"" + slot + "\"").Replace("[\"user\"]", "[\"ass\"]"), EmptyDailyRuntime));
            var node = f.Graph.GetCognitionNodes(20).Single();
            var next = f.Seed(2, "2020-08-21")[0];
            using (var db = new SQLite.SQLiteConnection(f.BrainPath))
                db.Execute("UPDATE moments SET Content=? WHERE Id=?", node.Summary + "\n" + node.About + "\n" + node.Scope + "\n" + node.Exceptions, next.Id);
            var probe = new ReviewDecisionProbe { Select = (input, q) => q == "target" && input.GetProperty("draft").GetProperty("summary").GetString().Length > 0 ? "target_1" : ReviewDecisionProbe.DefaultChoice(input, q) };
            AttachJev(f, probe); f.Run(new SelectionProbeLlm { Reply = JevLlmReply }, "2020-08-21");
            Require(f.Graph.GetCognitionNodes(20).Single().IdentitySlot == slot && !probe.Questions.Contains("route") &&
                f.Graph.GetCognitionEvidence(new[] { node.Id }).Any(e => e.MomentId == next.Id),
                "印证继承目标类别与领域，空类别同样保留，模型不填写冗余类别字段");
        }
        foreach (var errorKind in new[] { "transport", "invalid" })
        {
            using var f = new DailyFixture(assembly); f.Seed(2);
            var bad = new ReviewDecisionProbe();
            if (errorKind == "transport") bad.Before = (_, q) => { if (q.TryGetProperty("source_1", out _)) throw new InvalidOperationException("决策服务连接失败"); };
            else bad.Select = (_, _) => "arbitrary_database_id";
            AttachJev(f, bad);
            Require(DailyRejects(() => f.Run(new SelectionProbeLlm { Reply = JevLlmReply })) && f.Graph.GetCognitionNodes(20).Count == 0,
                "JEV无效答案/传输失败不变成等待或写入，也不自动重试");
            f.Reopen();
            Require(DailyRejects(() => f.Run(new AgentSequenceLlm())), "已固定JEV的日期缺配置时拒绝回退旧LLM执行器");
            var good = new ReviewDecisionProbe(); AttachJev(f, good);
            var resume = new SelectionProbeLlm { Reply = JevLlmReply }; f.Run(resume);
            Require(resume.Requests.Count(r => r.Contains("认知草稿")) == 1 && f.Graph.GetCognitionNodes(20).Count == 1,
                "失败重开复用已保存草稿，只生成后续空草稿，不重复提取/挑选/写入");
            if (errorKind == "transport") Require(good.Questions[0].StartsWith("source_"), "成功JEV目标选择按相同版本复用，不重复收费");
        }
        using (var f = new DailyFixture(assembly))
        {
            f.Seed(2); var probe = new ReviewDecisionProbe(); AttachJev(f, probe);
            using (var db = new SQLite.SQLiteConnection(f.BrainPath)) db.Execute(
                "CREATE TRIGGER fail_jev BEFORE INSERT ON plugin_documents WHEN NEW.PluginId='daily-review' AND NEW.Json LIKE '%\"Status\":\"committed\"%' BEGIN SELECT RAISE(ABORT,'injected JEV checkpoint failure'); END");
            try { f.Run(new SelectionProbeLlm { Reply = JevLlmReply }); } catch (Exception) { }
            Require(f.Graph.GetCognitionNodes(20).Count == 0, "认知写入和单项committed检查点必须一起回滚");
            using (var db = new SQLite.SQLiteConnection(f.BrainPath)) db.Execute("DROP TRIGGER fail_jev");
            f.Reopen(); AttachJev(f, new ReviewDecisionProbe()); f.Run(new SelectionProbeLlm { Reply = JevLlmReply });
            Require(f.Graph.GetCognitionNodes(20).Count == 1, "提交失败后复用已验证决策继续提交一次");
        }
        using (var f = new DailyFixture(assembly))
        {
            f.Seed(17); var probe = new ReviewDecisionProbe { Select = (input, q) =>
                q == "target" && input.GetProperty("material").GetProperty("evidence").GetArrayLength() == 16 ? "defer" : ReviewDecisionProbe.DefaultChoice(input, q) };
            AttachJev(f, probe);
            string Reply(List<DeepSeekMessageData> messages)
            {
                if (messages[0].content.Contains("1/4")) return "{\"events\":[{\"summary\":\"第一组经历\",\"source_refs\":[" +
                    string.Join(",", Enumerable.Range(1, 16)) + "]" + DailyTopicFields + "},{\"summary\":\"第二组经历\",\"source_refs\":[17]" + DailyTopicFields + "}]}";
                if (messages[0].content.Contains("2/4")) return "{\"selected_refs\":[1,2]}";
                return JevLlmReply(messages);
            }
            Require(DailyRejects(() => f.Run(new SelectionProbeLlm { Reply = Reply })) && f.Graph.GetCognitionNodes(20).Count == 1,
                "依据不足保留待处理，独立批次仍可提交，整日不能假报完成");
            using var state = f.State();
            Require(!state.RootElement.GetProperty("Done").GetBoolean() && state.RootElement.GetProperty("CognitionSources").GetArrayLength() == 1,
                "待处理批次不计入已处理来源");
            f.Reopen(); AttachJev(f, new ReviewDecisionProbe { Select = (input, q) => q == "target" ? "skip" : ReviewDecisionProbe.DefaultChoice(input, q) });
            // Explicit no_change is only legal for an empty draft; existing nonempty deferred draft can be skipped first.
            var resume = new SelectionProbeLlm { Reply = messages => messages[0].content.Contains("认知草稿") ? JevEmptyDraft : JevLlmReply(messages) };
            AttachJev(f, new ReviewDecisionProbe { Select = (input, q) => q == "target" && input.GetProperty("draft").GetProperty("summary").GetString().Length > 0 ? "skip" : ReviewDecisionProbe.DefaultChoice(input, q) });
            f.Run(resume);
            Require(f.Graph.GetCognitionNodes(20).Count == 1, "人工续做不重放其它已完成批次");
        }
        using (var f = new DailyFixture(assembly))
        {
            f.Seed(2); var probe = new ReviewDecisionProbe(); AttachJev(f, probe);
            var llm = new SelectionProbeLlm { Reply = messages => messages[0].content.Contains("认知草稿") ? DailyCognition : JevLlmReply(messages) };
            Require(DailyRejects(() => f.Run(llm)) && probe.Questions.Count == 0 && f.Graph.GetCognitionNodes(20).Count == 0 &&
                llm.Requests.Count(r => r.Contains("认知草稿")) == 2, "生成模型尝试返回操作包时仅限定修复一次，不能绕过决策层执行");
        }
        foreach (var change in new[] { "source", "identity" })
        {
            using var f = new DailyFixture(assembly); var source = f.Seed(2)[0];
            AttachJev(f, new ReviewDecisionProbe { Before = (_, questions) => {
                if (!questions.TryGetProperty("route", out _)) return;
                if (change == "source")
                {
                    using var db = new SQLite.SQLiteConnection(f.BrainPath);
                    db.Execute("UPDATE moments SET Content=? WHERE Id=?", "证据在决策期间已修订", source.Id);
                }
                else f.Store.SavePairIdentity("新称呼", "同伴", "称呼");
            } });
            Require(DailyRejects(() => f.Run(new SelectionProbeLlm { Reply = JevLlmReply })) && f.Graph.GetCognitionNodes(20).Count == 0,
                "JEV返回前来源或身份变化，旧决策不得提交");
        }
        using (var f = new DailyFixture(assembly))
        {
            f.Seed(2); AttachJev(f, new ReviewDecisionProbe { Select = (_, _) => "defer" });
            Require(DailyRejects(() => f.Run(new SelectionProbeLlm { Reply = JevLlmReply })), "先保留原始JEV来源快照");
            var late = f.Seed(1, offset: 2)[0];
            f.Reopen(); AttachJev(f, new ReviewDecisionProbe());
            var resume = new SelectionProbeLlm { Reply = JevLlmReply }; f.Run(resume);
            using (var state = f.State()) Require(state.RootElement.GetProperty("SourceIds").GetArrayLength() == 2 &&
                resume.Requests.All(r => !r.Contains(late.Content)), "续做不混入迟到原句，不改变已有草稿的来源快照");
            var next = new SelectionProbeLlm { Reply = messages => messages[0].content.Contains("1/4") ? "{\"events\":[]}" : EmptyDailyRuntime }; f.Run(next);
            using var completed = f.State();
            Require(next.Requests.Count(r => r.Contains("【1/4")) == 1 && next.Requests[0].Contains(late.Content) &&
                completed.RootElement.GetProperty("SourceIds").GetArrayLength() == 3, "迟到原句下一轮单独提取，不被旧完成状态吞掉");
        }
        using (var f = new DailyFixture(assembly))
        {
            var inner = new SelectionProbeLlm { Reply = _ => "{}" };
            var budgetType = assembly.GetType("TraceSoul2.Migrate.ReviewBudgetClient", true);
            var budget = (ILlmClient)Activator.CreateInstance(budgetType, inner, null, 2);
            f.Context.GetType().GetField("budgetClient", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(f.Context, budget);
            var probe = new ReviewDecisionProbe(); AttachJev(f, probe);
            var method = f.Context.GetType().GetMethod("DecideReviewAsync");
            using var questions = JsonDocument.Parse("{\"route\":{\"type\":\"choice\",\"instructions\":\"选择描述对象\",\"criteria\":{\"other\":\"对方\",\"self\":\"自己\"}}}");
            using var state = JsonDocument.Parse("{}");
            void Decide() => ((Task<JsonElement>)method.Invoke(f.Context, new object[] { state.RootElement, questions.RootElement, CancellationToken.None })).GetAwaiter().GetResult();
            budget.CompleteJsonAsync(new List<DeepSeekMessageData>()).GetAwaiter().GetResult();
            Decide();
            Require(DailyRejects(Decide) && probe.Questions.Count == 1 && inner.Requests.Count == 1,
                "LLM和JEV共用派发预算，耗尽时不能发出额外JEV请求");
        }
        Console.WriteLine("JEV cognition review passed: bounded decisions, inherited identity, durable drafts, resume, rollback, stale sources, late arrivals and shared budget.");
    }

    // Explicit opt-in paid probe. Only synthetic fixture material is sent; never invoked by normal regression/CI.
    private static void RunJevReviewSmoke(string assemblyPath, string privateConnection)
    {
        using var config = JsonDocument.Parse(File.ReadAllText(privateConnection));
        var cfg = config.RootElement;
        using var client = new DecisionsClient(new LlmProviderRecord {
            baseUrl = cfg.GetProperty("base_url").GetString(), apiKey = cfg.GetProperty("openrouter_api_key").GetString(),
            relayKey = cfg.GetProperty("relay_key").GetString(), decisionMode = "relay", model = "typesafe/jev-1.14", timeout = 60 });
        using var f = new DailyFixture(Assembly.LoadFrom(Path.GetFullPath(assemblyPath)));
        f.Seed(2);
        var calls = 0; double cost = 0;
        Func<JsonElement, JsonElement, CancellationToken, Task<JsonElement>> decide = async (state, questions, token) => {
            if (++calls > 20) throw new InvalidOperationException("Synthetic JEV probe request budget exhausted.");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var result = await client.DecideAsync(state, questions, client.Model, token);
            if (result.TryGetProperty("usage", out var usage) && usage.TryGetProperty("cost", out var price) && price.TryGetDouble(out var n)) cost += n;
            Console.WriteLine(JsonSerializer.Serialize(new { call = calls, elapsedMs = watch.ElapsedMilliseconds,
                choices = result.GetProperty("answers").EnumerateObject().ToDictionary(a => a.Name, a => a.Value.GetProperty("choice").GetString()) }));
            return result;
        };
        f.Context.GetType().GetProperty("ReviewDecisionOverride").SetValue(f.Context, decide);
        f.Run(new SelectionProbeLlm { Reply = JevLlmReply });
        var node = f.Graph.GetCognitionNodes(20).Single();
        Require((node.IdentitySlot == "other" || node.IdentitySlot == "") && node.Domains == "user", "真实JEV识别合成用户早餐意愿并正确归类");
        Console.WriteLine(JsonSerializer.Serialize(new { success = true, scenario = "synthetic_new_cognition", calls, cost, nodes = 1,
            identity = node.IdentitySlot, domains = node.Domains, generatedDraft = "simulated", productionWrites = false }));
    }
}
