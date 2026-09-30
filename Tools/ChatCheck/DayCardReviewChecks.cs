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
        Require(Error(Output(Card("relation", new string('字', 301), "r1"))).Contains(".body 超过300"), "超长摘要准确定位");
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
            var failing = new AgentSequenceLlm(duplicate, duplicate);
            args[2] = failing;
            var rejected = false;
            try { ((Task)run.Invoke(null, args)).GetAwaiter().GetResult(); }
            catch (InvalidOperationException ex) { rejected = ex.Message.Contains("首次错误") && ex.Message.Contains("纠正后错误") && ex.Message.Contains("重复"); }
            Require(rejected && failing.Requests.Count == 2, "连续重复仍保留准确错误并停止，不放宽约束、不无限重试");
        }
        finally { context?.Dispose(); Directory.Delete(dir, true); }
        Console.WriteLine("Day card review checks passed: slot grouping, precise repair, multiple evidence, explicit empty, pinned/length/source guards and actual Migration entry.");
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
