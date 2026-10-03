using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Manager;
using TraceSoul2.Logic;
using TraceSoul2.Util;
using TraceSoul2.Plugins;

internal static partial class Program
{
    private static void RunDayCardReviewChecks(Assembly assembly)
    {
        var contract = assembly.GetType("TraceSoul2.Migrate.DayCardReviewContract", true);
        var outputType = assembly.GetType("TraceSoul2.Migrate.ReplayPrompts+DayCardReviewOutputData", true);
        var validate = contract.GetMethod("ValidationError");
        var json = new JsonSerializerOptions { IncludeFields = true };
        var cards = IdentityCardSlotValues.All.Select(slot => new IdentityCardRecord
            { Slot = slot, Body = "", Pinned = slot == "personality" }).ToList();
        var nodes = new List<CognitionSliceRecord> {
            new() { Id = "r1", Status = "active", IdentitySlot = "relation", Summary = "第一条关系理解" },
            new() { Id = "r2", Status = "active", IdentitySlot = "relation", Summary = "第二条关系理解" },
            new() { Id = "h1", Status = "active", IdentitySlot = "expression_habit", Summary = "表达理解" },
            new() { Id = "p1", Status = "active", IdentitySlot = "personality", Summary = "固定内容依据" } };
        object Parse(string raw) => JsonSerializer.Deserialize(raw, outputType, json);
        string Error(string raw) => (string)validate.Invoke(null, new object[] { Parse(raw), cards, nodes });
        object Card(string slot, string body, params string[] refs) => new { slot, body, cognition_ids = refs };
        string Output(params object[] updates) => JsonSerializer.Serialize(new { summary = "离线测试", cards = updates });
        var duplicate = Output(Card("relation", "第一部分", "r1"), Card("relation", "第二部分", "r2"),
            Card("expression_habit", "表达变化", "h1"));
        var merged = Output(Card("relation", "综合理解保留两部分", "r1", "r2"), Card("expression_habit", "表达变化", "h1"));
        var error = Error(duplicate);
        Require(error.Contains("$.cards[1].slot") && error.Contains("$.cards[0].slot") && error.Contains("重复") &&
            error.Contains("综合") && !error.Contains("第一部分") && !error.Contains("缺少有效认知"),
            "重复卡错误准确定位同名项和综合要求，不谎报依据缺失、不泄露正文");
        Require(Error(merged) == null && Error("{\"cards\":[]}") == null, "同卡多依据合法，明确不更新合法");
        Require(Error("{}")?.Contains("$.cards") == true && Error("{\"cards\":null}") != null,
            "省略 cards 不能冒充无变化");
        Require(Error(Output(Card("personality", "不应覆盖", "p1"))).Contains("本人固定"), "仍拒绝覆盖固定内容");
        Require(Error(Output(Card("relation", new string('字', 445), "r1"))) == null &&
            Error(Output(Card("relation", new string('字', 600), "r1"))) == null &&
            Error(Output(Card("relation", new string('字', 601), "r1"))).Contains(".body 超过600"),
            "300是目标，完整摘要允许余量，异常长度仍准确定位");
        Require(Error(Output(Card("relation", "测试", "h1"))).Contains(".cognition_ids[0]"), "跨 slot 依据拒绝");
        Require(Error(Output(Card("relation", "测试", "unknown-private-id"))).Contains(".cognition_ids[0]"), "未知依据拒绝且不回显值");
        var contextText = (string)contract.GetMethod("EvidenceContext").Invoke(null, new object[] { cards, nodes });
        Require(contextText.Contains("同组的多条认知") && contextText.Contains("r1") && contextText.Contains("r2") &&
            !contextText.Contains("固定内容依据"), "按可更新卡片组织依据，固定卡不作为输出目标");

        // Exercise the actual private DayBuilder entry with temporary dependencies, never production config.
        var dir = Path.Combine(Path.GetTempPath(), "day-card-contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        IDisposable context = null;
        try
        {
            var contextType = assembly.GetType("TraceSoul2.Migrate.MigrationContext", true);
            var storeType = assembly.GetType("TraceSoul2.Manager.SqliteMemoryManager", true);
            var dbPath = Path.Combine(dir, "brain.db");
            context = (IDisposable)Activator.CreateInstance(contextType);
            var actualStore = Activator.CreateInstance(storeType, dbPath);
            contextType.GetProperty("Store").SetValue(context, actualStore);
            var migration = Activator.CreateInstance(assembly.GetType("TraceSoul2.Migrate.MigrationDb", true),
                Path.Combine(dir, "migration.db"), dbPath);
            contextType.GetProperty("Migration").SetValue(context, migration);
            var store = (IMemoryStore)actualStore;
            store.SavePairIdentity("本人", "同伴", "称呼");
            using (var db = new SQLite.SQLiteConnection(dbPath))
                foreach (var node in nodes) db.Insert(node);
            var run = assembly.GetType("TraceSoul2.Migrate.DayBuilder", true).GetMethod("RunDayReviewAsync", BindingFlags.NonPublic | BindingFlags.Static);
            var llm = new AgentSequenceLlm(duplicate, merged);
            var args = new object[] { context, store.LoadPairIdentity(), llm, "2026-09-30", cards, "",
                new List<EventIndexRecord>(), new List<EventEntryRecord>() };
            var task = (Task)run.Invoke(null, args);
            task.GetAwaiter().GetResult();
            var result = task.GetType().GetProperty("Result").GetValue(task);
            Require(((System.Collections.IList)outputType.GetField("cards").GetValue(result)).Count == 2 &&
                llm.Requests.Count == 2 && llm.Messages[1].Last().content.Contains("$.cards[1].slot") &&
                llm.Messages[1].Last().content.Contains("合并所用 cognition_ids"),
                "真实日复盘入口按具体错误纠正一次，两张同名卡综合后正常完成");
            var outside = new AgentSequenceLlm(Output(Card("self", "范围外的自我改写", "r1"),
                Card("other", "范围外的他者改写", "r2"), Card("personality", "范围外的种子改写", "p1"),
                Card("relation", "有依据的关系变化", "r1"), Card("expression_habit", "有依据的表达变化", "h1")));
            args[2] = outside;
            var restrictedTask = (Task)run.Invoke(null, args);
            restrictedTask.GetAwaiter().GetResult();
            var restricted = restrictedTask.GetType().GetProperty("Result").GetValue(restrictedTask);
            using (var resultJson = JsonDocument.Parse(JsonSerializer.Serialize(restricted, outputType, json)))
                Require(resultJson.RootElement.GetProperty("cards").GetArrayLength() == 2 && outside.Requests.Count == 1 &&
                    resultJson.RootElement.GetProperty("cards")[0].GetProperty("slot").GetString() == "relation" &&
                    resultJson.RootElement.GetProperty("cards")[0].GetProperty("cognition_ids")[0].GetString() == "r1",
                    "实际入口由程序保留范围外与固定卡，仅校验可写目标；有效正文及依据不变且不额外纠正");
            var wrongGroup = new AgentSequenceLlm(Output(Card("relation", "错误跨组引用", "h1")), merged);
            args[2] = wrongGroup;
            ((Task)run.Invoke(null, args)).GetAwaiter().GetResult();
            Require(wrongGroup.Requests.Count == 2 && wrongGroup.Messages[1].Last().content.Contains("cognition_ids[0]"),
                "可写卡跨组引用仍进入精确纠正，程序不改写引用或强行通过");
            var failing = new AgentSequenceLlm(duplicate, duplicate);
            args[2] = failing;
            var rejected = false;
            try { ((Task)run.Invoke(null, args)).GetAwaiter().GetResult(); }
            catch (InvalidOperationException ex) { rejected = ex.Message.Contains("首次错误") && ex.Message.Contains("纠正后错误") && ex.Message.Contains("重复"); }
            Require(rejected && failing.Requests.Count == 2, "连续重复仍保留准确错误并停止，不放宽约束、不无限重试");

            foreach (var length in new[] { 300, 363, 365, 445, 600 })
            {
                var body = new string('长', length - 7) + "重要条件在末尾";
                var validOutput = Output(Card("relation", body, "r1", "r2"));
                var accepted = new AgentSequenceLlm(validOutput);
                args[2] = accepted;
                var acceptedTask = (Task)run.Invoke(null, args);
                acceptedTask.GetAwaiter().GetResult();
                using var acceptedJson = JsonDocument.Parse(JsonSerializer.Serialize(acceptedTask.GetType().GetProperty("Result").GetValue(acceptedTask), outputType, json));
                Require(accepted.Requests.Count == 1 && acceptedJson.RootElement.GetProperty("cards")[0].GetProperty("body").GetString() == body,
                    "完整摘要在目标余量内通过真实日复盘入口，无额外精炼调用或尾部损失");
                var saved = store.SaveIdentityCard("length-check", "relation", body, "");
                Require(saved.Body == body && store.LoadIdentityCards("length-check").Single(x => x.Slot == "relation").Body == body,
                    "底层保存和重新读取保留完整摘要");
            }
            var previous = store.LoadIdentityCards("length-check").Single(x => x.Slot == "relation");
            rejected = false;
            try { store.SaveIdentityCard("length-check", "relation", new string('长', 601), ""); }
            catch (InvalidOperationException) { rejected = true; }
            Require(rejected && store.LoadIdentityCards("length-check").Single(x => x.Slot == "relation").Body == previous.Body,
                "异常长度保存失败时原卡不变，不能截断后落库");
            var normalized = IdentityCardLogic.Normalize(new IdentityReviewOutputData { cards = new() {
                new() { slot = "relation", changed = true, body = new string('长', 440) + "完整末尾。" }
            } }, Array.Empty<IdentityCardRecord>(), store.LoadPairIdentity());
            Require(normalized.cards[0].body.Length == 445 && normalized.cards[0].body.EndsWith("完整末尾。"),
                "旧身份规范化同样保留略超目标的完整正文");
            var renameBody = "本人" + new string('长', 591) + "重要条件在末尾";
            store.LoadIdentityCards("rename-length");
            store.SaveIdentityCard("rename-length", "self", renameBody, "");
            store.SavePairIdentity("新的完整本人称呼", "同伴", "称呼");
            Require(store.LoadIdentityCards("rename-length").Single(x => x.Slot == "self").Body ==
                "新的完整本人称呼" + new string('长', 591) + "重要条件在末尾",
                "更换为更长称呼不截断已保存摘要或末尾条件");
            store.SavePairIdentity("本人", "同伴", "称呼");
            var longOutput = Output(Card("relation", new string('长', 745), "r1", "r2"), Card("expression_habit", "保持这张原文", "h1"));
            var tooLong = JsonSerializer.Serialize(new { cards = new[] { new { slot = "relation", body = new string('长', 665) } } });
            var shortBody = "凝练后仍然完整的关系理解。";
            var fitted = JsonSerializer.Serialize(new { cards = new[] { new { slot = "relation", body = shortBody } } });
            var fitting = new AgentSequenceLlm(longOutput, tooLong, fitted);
            args[2] = fitting;
            var fitTask = (Task)run.Invoke(null, args);
            fitTask.GetAwaiter().GetResult();
            using var fitJson = JsonDocument.Parse(JsonSerializer.Serialize(fitTask.GetType().GetProperty("Result").GetValue(fitTask), outputType, json));
            var fitCards = fitJson.RootElement.GetProperty("cards");
            Require(fitting.Requests.Count == 3 && fitting.Requests[1].Length < fitting.Requests[0].Length &&
                !fitting.Requests[1].Contains("保持这张原文") && fitting.Messages[2].Last().content.Contains("当前665字") &&
                fitCards[0].GetProperty("body").GetString() == shortBody && fitCards[0].GetProperty("cognition_ids").GetArrayLength() == 2 &&
                fitCards[1].GetProperty("body").GetString() == "保持这张原文",
                "异常长摘要独立精炼，重试给准确长度，其他卡和依据原样保留");
            var unchanged = Parse(longOutput);
            var fitMethod = contract.GetMethod("FitBodiesAsync");
            var failure = new AgentSequenceLlm(tooLong, tooLong);
            var preservedTask = (Task)fitMethod.Invoke(null, new object[] { failure, unchanged, cards, nodes, System.Threading.CancellationToken.None });
            preservedTask.GetAwaiter().GetResult();
            var preserved = (List<string>)preservedTask.GetType().GetProperty("Result").GetValue(preservedTask);
            using var remaining = JsonDocument.Parse(JsonSerializer.Serialize(unchanged, outputType, json));
            Require(failure.Requests.Count == 2 && preserved.SequenceEqual(new[] { "relation" }) &&
                remaining.RootElement.GetProperty("cards").GetArrayLength() == 1 &&
                remaining.RootElement.GetProperty("cards")[0].GetProperty("body").GetString() == "保持这张原文",
                "精炼失败只延后超长卡，其他有效卡继续，原卡和正文不被截断");
            var fittingFailure = new AgentSequenceLlm(Output(Card("relation", new string('长', 1201), "r1", "r2"), Card("expression_habit", "保持这张原文", "h1")),
                JsonSerializer.Serialize(new { cards = new[] { new { slot = "relation", body = new string('长', 724) } } }),
                JsonSerializer.Serialize(new { cards = new[] { new { slot = "relation", body = new string('长', 722) } } }));
            args[2] = fittingFailure;
            var continued = (Task)run.Invoke(null, args);
            continued.GetAwaiter().GetResult();
            using var continuedJson = JsonDocument.Parse(JsonSerializer.Serialize(continued.GetType().GetProperty("Result").GetValue(continued), outputType, json));
            Require(fittingFailure.Requests.Count == 3 && continuedJson.RootElement.GetProperty("cards").GetArrayLength() == 1 &&
                continuedJson.RootElement.GetProperty("summary").GetString().Contains("保留原卡："),
                "真实Migration入口在1201→724→722字持续超长时继续返回有效结果并记录延后原因");
            var unavailable = Parse(longOutput);
            rejected = false;
            try { ((Task)fitMethod.Invoke(null, new object[] { new AgentSequenceLlm(), unavailable, cards, nodes, System.Threading.CancellationToken.None })).GetAwaiter().GetResult(); }
            catch (InvalidOperationException) { rejected = true; }
            Require(rejected && JsonSerializer.Serialize(unavailable, outputType, json) == JsonSerializer.Serialize(Parse(longOutput), outputType, json),
                "模型调用本身失败仍传播错误，不能冒充摘要精炼的局部失败");
        }
        finally { context?.Dispose(); Directory.Delete(dir, true); }
        Console.WriteLine("Day card review checks passed: slot grouping, precise repair, multiple evidence, explicit empty, pinned/length/source guards and actual Migration entry.");
    }

    private static void RunOct3ReviewReplay(string assemblyPath, string directory)
    {
        string Response(string pattern)
        {
            var text = File.ReadAllText(Directory.GetFiles(directory, pattern).Single());
            return text.Substring(text.IndexOf('{'), text.LastIndexOf('}') - text.IndexOf('{') + 1);
        }
        var assembly = Assembly.LoadFrom(Path.GetFullPath(assemblyPath));
        var contract = assembly.GetType("TraceSoul2.Migrate.DayCardReviewContract", true);
        var outputType = assembly.GetType("TraceSoul2.Migrate.ReplayPrompts+DayCardReviewOutputData", true);
        var options = new JsonSerializerOptions { IncludeFields = true, PropertyNameCaseInsensitive = true };
        using var request = JsonDocument.Parse(File.ReadAllText(Directory.GetFiles(directory, "20261003-122623*-request.json").Single()));
        var prompt = request.RootElement.GetProperty("messages")[0].GetProperty("content").GetString();
        var start = prompt.IndexOf("\n[", prompt.IndexOf("【成长摘要与可参考的认识，按 slot 分组】", StringComparison.Ordinal), StringComparison.Ordinal);
        var end = prompt.IndexOf("【保留原文的本人设定】", start, StringComparison.Ordinal);
        using var groups = JsonDocument.Parse(prompt.Substring(start, end - start).Trim());
        var evidence = groups.RootElement.EnumerateArray().SelectMany(g => JsonSerializer.Deserialize<List<CognitionSliceRecord>>(g.GetProperty("cognitions").GetRawText(), options)).ToList();
        var current = new List<IdentityCardRecord> { new() { Slot = "personality", Pinned = true } };
        var output = JsonSerializer.Deserialize(Response("20261003-122623*-response.txt"), outputType, options);
        using var before = JsonDocument.Parse(JsonSerializer.Serialize(output, outputType, options));
        var fitting = new AgentSequenceLlm(Response("20261003-*-076-response.txt"), Response("20261003-*-077-response.txt"));
        var task = (Task)contract.GetMethod("FitBodiesAsync").Invoke(null, new object[] { fitting, output, current, evidence, System.Threading.CancellationToken.None });
        task.GetAwaiter().GetResult();
        Require(((List<string>)task.GetType().GetProperty("Result").GetValue(task)).SequenceEqual(new[] { "relation" }) && fitting.Requests.Count == 2,
            "真实1201字候选及724、722字精炼响应只延后关系卡，无真实模型调用");
        using var after = JsonDocument.Parse(JsonSerializer.Serialize(output, outputType, options));
        Require(after.RootElement.GetProperty("cards").GetArrayLength() == 1 &&
            after.RootElement.GetProperty("cards")[0].GetRawText() == before.RootElement.GetProperty("cards")[0].GetRawText() &&
            contract.GetMethod("ValidationError").Invoke(null, new object[] { output, current, evidence }) == null,
            "真实有效表达卡的正文及引用完整通过原校验");
        foreach (var field in before.RootElement.EnumerateObject().Where(x => x.Name != "cards"))
            Require(after.RootElement.GetProperty(field.Name).GetRawText() == field.Value.GetRawText(), "精炼回退不修改其余真实内心与摘要字段");
        using var store = new SqliteMemoryManager(":memory:");
        var dayStart = MemoryDayLogic.CurrentStart(DateTimeOffset.Now);
        for (var i = 0; i < 4; i++)
        {
            store.SaveMoment(new MomentRecord { Id = "replay-" + i, ConversationId = "replay", Role = "user", Content = "合成来源", CreatedUnixMs = dayStart.AddHours(7).AddMinutes(i).ToUnixTimeMilliseconds() });
            store.AppendDayTrajectory("replay", "replay-" + i, "合成事件" + i);
        }
        var services = new TracePluginServices(store, new HierarchicalVectorRouterLogic(new FakeEncoder())) { Llm = new AgentSequenceLlm(Response("20261003-122220*-response.txt")) };
        var turn = new TraceTurnContext("replay", new MomentRecord { ConversationId = "replay" }, new(), 0, true, services,
            environment: new EnvironmentSnapshotData { ContextConversationId = "replay", Visibility = "private" });
        var commit = DayTrajectoryOverviewLogic.Prepare(turn).AnalyzeAsync(default).GetAwaiter().GetResult();
        Require(commit != null, "真实精简响应的共同来源通过校验");
        commit(default).GetAwaiter().GetResult();
        Require(DayTrajectoryOverviewLogic.Read(store, "replay", dayStart.ToString("yyyy-MM-dd")).Count == 5,
            "真实五个短事件均保留，重复来源不退回原文");
        Console.WriteLine("Oct 3 read-only replay passed: failed card fitting preserved valid outputs; shared-source overview accepted. No private text printed or production data written.");
    }

    private static void RunDayCardScopeDumpCheck(string assemblyPath, string directory)
    {
        var assembly = Assembly.LoadFrom(Path.GetFullPath(assemblyPath));
        var contract = assembly.GetType("TraceSoul2.Migrate.DayCardReviewContract", true);
        var outputType = assembly.GetType("TraceSoul2.Migrate.ReplayPrompts+DayCardReviewOutputData", true);
        var options = new JsonSerializerOptions { IncludeFields = true, PropertyNameCaseInsensitive = true };
        var count = 0;
        foreach (var file in Directory.GetFiles(directory, "20261003-04*-response.txt").OrderBy(x => x))
        {
            var raw = File.ReadAllText(file);
            raw = raw.Substring(raw.IndexOf('{'), raw.LastIndexOf('}') - raw.IndexOf('{') + 1);
            using var original = JsonDocument.Parse(raw);
            if (!original.RootElement.TryGetProperty("cards", out var originalCards)) continue;
            using var request = JsonDocument.Parse(File.ReadAllText(file.Replace("-response.txt", "-request.json")));
            var prompt = request.RootElement.GetProperty("messages")[0].GetProperty("content").GetString();
            const string marker = "【成长摘要与可参考的认识，按 slot 分组】";
            var start = prompt.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0) continue;
            start = prompt.IndexOf("\n[", start, StringComparison.Ordinal);
            const string endMarker = "【保留原文的本人设定】";
            var end = prompt.IndexOf(endMarker, start, StringComparison.Ordinal);
            using var groups = JsonDocument.Parse(prompt.Substring(start, end - start).Trim());
            var evidence = groups.RootElement.EnumerateArray().SelectMany(g =>
                JsonSerializer.Deserialize<List<CognitionSliceRecord>>(g.GetProperty("cognitions").GetRawText(), options)).ToList();
            var pinned = prompt.Substring(end + endMarker.Length).TrimStart().Split('\n')[0].Split(',')
                .Select(x => new IdentityCardRecord { Slot = x.Trim(), Pinned = true }).ToList();
            var output = JsonSerializer.Deserialize(raw, outputType, options);
            var error = (string)contract.GetMethod("ValidationError").Invoke(null, new object[] { output, pinned, evidence });
            Require(error?.Contains("cards[0].cognition_ids[0]") == true, "真实响应复现范围外卡使用关系依据的原始错误");
            using var beforeProjection = JsonDocument.Parse(JsonSerializer.Serialize(output, outputType, options));
            var preserved = (List<string>)contract.GetMethod("PreserveUnavailableCards").Invoke(null, new object[] { output, pinned, evidence });
            Require(preserved.OrderBy(x => x).SequenceEqual(new[] { "other", "self" }) &&
                contract.GetMethod("ValidationError").Invoke(null, new object[] { output, pinned, evidence }) == null,
                "程序保留self/other后，真实relation和expression_habit通过原引用及长度校验");
            using var projected = JsonDocument.Parse(JsonSerializer.Serialize(output, outputType, options));
            foreach (var card in projected.RootElement.GetProperty("cards").EnumerateArray())
            {
                var source = originalCards.EnumerateArray().Single(x => x.GetProperty("slot").GetString() == card.GetProperty("slot").GetString());
                Require(source.GetProperty("body").GetString() == card.GetProperty("body").GetString() &&
                    source.GetProperty("cognition_ids").EnumerateArray().Select(x => x.GetString()).SequenceEqual(
                        card.GetProperty("cognition_ids").EnumerateArray().Select(x => x.GetString())), "有效摘要正文与引用完整不变");
            }
            foreach (var field in original.RootElement.EnumerateObject().Where(x => x.Name.StartsWith("inner_")))
                Require(JsonSerializer.Serialize(beforeProjection.RootElement.GetProperty(field.Name)) == JsonSerializer.Serialize(projected.RootElement.GetProperty(field.Name)),
                    "本次真实内心输出保持不变");
            count++;
        }
        Require(count == 2, "读取两份最新真实失败日终响应");
        Console.WriteLine("Day card scope dump checks passed: both real failures reproduced; only eligible targets retained with intact bodies/references/inner state, no model calls or production writes.");
    }

    private static void RunDayCardLengthDumpCheck(string assemblyPath, string directory)
    {
        var assembly = Assembly.LoadFrom(Path.GetFullPath(assemblyPath));
        var contract = assembly.GetType("TraceSoul2.Migrate.DayCardReviewContract", true);
        var outputType = assembly.GetType("TraceSoul2.Migrate.ReplayPrompts+DayCardReviewOutputData", true);
        var options = new JsonSerializerOptions { IncludeFields = true, PropertyNameCaseInsensitive = true };
        var count = 0;
        foreach (var file in Directory.GetFiles(directory, "20261002*-response.txt").OrderBy(x => x))
        {
            var raw = File.ReadAllText(file);
            if (raw.StartsWith("http=")) raw = raw.Substring(raw.IndexOf('\n') + 1);
            using var original = JsonDocument.Parse(raw);
            if (!original.RootElement.TryGetProperty("cards", out var outputCards)) continue;
            using var request = JsonDocument.Parse(File.ReadAllText(file.Replace("-response.txt", "-request.json")));
            var prompt = request.RootElement.GetProperty("messages")[0].GetProperty("content").GetString();
            var marker = prompt.Contains("【成长摘要与可参考的认识，按 slot 分组】")
                ? "【成长摘要与可参考的认识，按 slot 分组】" : "【本次可更新的身份卡与依据，按 slot 分组】";
            var groupStart = prompt.IndexOf(marker, StringComparison.Ordinal);
            if (groupStart < 0) continue; // 独立精炼响应没有认知依据，不冒充完整日终输出。
            var jsonStart = prompt.IndexOf("\n[", groupStart, StringComparison.Ordinal);
            var pinnedMarker = marker.StartsWith("【成长") ? "【保留原文的本人设定】" : "【本人固定，不能覆盖的卡】";
            var groupEnd = prompt.IndexOf(pinnedMarker, jsonStart, StringComparison.Ordinal);
            using var groups = JsonDocument.Parse(prompt.Substring(jsonStart, groupEnd - jsonStart).Trim());
            var evidence = groups.RootElement.EnumerateArray().SelectMany(g =>
                JsonSerializer.Deserialize<List<CognitionSliceRecord>>(g.GetProperty("cognitions").GetRawText(), options)).ToList();
            var slots = prompt.Substring(groupEnd + pinnedMarker.Length).TrimStart().Split('\n')[0]
                .Split(',').Select(x => new IdentityCardRecord { Slot = x.Trim(), Pinned = true }).ToList();
            var output = JsonSerializer.Deserialize(raw, outputType, options);
            Require(contract.GetMethod("ValidationError").Invoke(null, new object[] { output, slots, evidence }) == null,
                "真实完整摘要通过长度、结构、固定卡及依据校验");
            var before = JsonSerializer.Serialize(output, outputType, options);
            var llm = new AgentSequenceLlm();
            ((Task)contract.GetMethod("FitBodiesAsync").Invoke(null,
                new object[] { llm, output, slots, evidence, System.Threading.CancellationToken.None })).GetAwaiter().GetResult();
            Require(JsonSerializer.Serialize(output, outputType, options) == before && llm.Requests.Count == 0,
                "真实略超目标的摘要完整保留，无额外调用，其他卡、引用和内心原样保留");
            count++;
        }
        Require(count > 0, "至少核对一份带完整认知依据的真实日终响应");
        Console.WriteLine("Day card length dump checks passed: actual review outputs accepted intact without fitting calls; no private text printed or runtime database modified.");
    }

    private static void RunDayCardDumpCheck(string assemblyPath, string directory)
    {
        var assembly = Assembly.LoadFrom(Path.GetFullPath(assemblyPath));
        var contract = assembly.GetType("TraceSoul2.Migrate.DayCardReviewContract", true);
        var outputType = assembly.GetType("TraceSoul2.Migrate.ReplayPrompts+DayCardReviewOutputData", true);
        var options = new JsonSerializerOptions { IncludeFields = true, PropertyNameCaseInsensitive = true };
        var checkedCount = 0;
        foreach (var requestPath in Directory.GetFiles(directory, "20261001-04*-request.json").OrderBy(x => x))
        {
            using var request = JsonDocument.Parse(File.ReadAllText(requestPath));
            var prompt = request.RootElement.GetProperty("messages")[0].GetProperty("content").GetString();
            const string marker = "【可用于摘要的认知与来源】\n";
            var start = prompt.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0) continue;
            var evidenceText = prompt.Substring(start + marker.Length).Split("\n【", 2)[0];
            var evidence = JsonSerializer.Deserialize<List<CognitionSliceRecord>>(evidenceText, options);
            const string pinnedMarker = "【本人固定，不能覆盖的卡】\n";
            var pinned = prompt.Substring(prompt.IndexOf(pinnedMarker, StringComparison.Ordinal) + pinnedMarker.Length).Split('\n')[0]
                .Split(',').Select(slot => new IdentityCardRecord { Slot = slot, Pinned = true }).ToList();
            var raw = (string)typeof(DeepSeekStructuredOutputLogic).GetMethod("StripCodeFence", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { File.ReadAllText(requestPath.Replace("-request.json", "-response.txt")) });
            var output = JsonSerializer.Deserialize(raw, outputType, options);
            var error = (string)contract.GetMethod("ValidationError").Invoke(null, new[] { output, pinned, evidence });
            Require(error?.Contains("$.cards[1].slot") == true && error.Contains("重复"), "真实失败响应必须识别为重复卡");
            checkedCount++;
        }
        Require(checkedCount == 2, "只读核对两份真实失败的日终卡片输出");
        Console.WriteLine("Day card dump check passed: both original outputs rejected with exact duplicate-slot diagnosis; no source text printed or database modified.");
    }
}
