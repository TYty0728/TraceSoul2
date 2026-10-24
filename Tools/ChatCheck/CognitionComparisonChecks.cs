using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Logic;
using TraceSoul2.Manager;
using TraceSoul2.Util;

internal static partial class Program
{
    private static async Task RunCognitionComparisonChecksAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), "cognition-compare-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            using var store = new SqliteMemoryManager(Path.Combine(folder, "brain.db"));
            var source = new MomentRecord { Id = "comparison-source", ConversationId = "tracesoul2", Role = "user", Content = "一起核对安排。" };
            store.SaveMoment(source);
            var nodes = new[] {
                new CognitionSliceRecord { Id = "relation-node", Summary = "我们会核对安排", IdentitySlot = "relation" },
                new CognitionSliceRecord { Id = "legacy-node", Summary = "原来会核对安排", IdentitySlot = "" },
                new CognitionSliceRecord { Id = "self-node", Summary = "我会先核对安排", IdentitySlot = "self" },
                new CognitionSliceRecord { Id = "other-node", Summary = "独立的其他认识", IdentitySlot = "other" } };
            BrainCognitionWriteData Write(string slot = "relation") => new() { operation = "create", summary = "我会共同检查新的计划",
                identity_slot = slot, domains = new() { slot == "self" ? "ass" : "relation" }, evidence_moment_ids = new() { source.Id } };
            CognitionConsolidationLogic.Decision Decision(string kind, string id = "", string before = "", string after = "", int index = 0)
                => new() { index = index, kind = kind, target_id = id, before = before, after = after, explanation = "根据对象和适用范围判断" };
            string Reply(params CognitionConsolidationLogic.Decision[] decisions) => TraceJson.ToJson(new CognitionConsolidationLogic.Review { decisions = decisions.ToList() });
            var write = Write();
            var writes = new List<BrainCognitionWriteData> { write };
            var original = TraceJson.ToJson(writes);
            string Error(params CognitionConsolidationLogic.Decision[] decisions) => CognitionConsolidationLogic.ValidationError(
                new() { decisions = decisions.ToList() }, writes, nodes);
            var mismatched = Decision("revise", nodes[1].Id, nodes[1].Summary, write.summary);
            Require(Error(mismatched).Contains("identity_slot"), "现场同类：空用途旧节点不能作为relation候选的修订目标，定位具体字段");
            var corrected = new AgentSequenceLlm(Reply(mismatched), Reply(Decision("new", after: write.summary)));
            var resolved = await CognitionConsolidationLogic.ResolveAsync(store, corrected, new[] { source }, nodes,
                writes, () => new BagOfCharsVectorEncoder(), null, CancellationToken.None);
            Require(corrected.Requests.Count == 2 && resolved.Writes.Single().operation == "create" &&
                TraceJson.ToJson(writes) == original && resolved.Candidates.Count == nodes.Length,
                "错误比较只纠正一次，不重做候选形成，也不修改输入提案或删除其他用途节点");
            using (var input = JsonDocument.Parse(corrected.Messages[0][1].content))
            {
                var shown = input.RootElement.GetProperty("existing").EnumerateArray().Select(x => x.GetProperty("id").GetString()).ToList();
                var allowed = input.RootElement.GetProperty("proposals")[0].GetProperty("existing_ids").EnumerateArray().Select(x => x.GetString()).ToList();
                Require(shown.SequenceEqual(new[] { nodes[0].Id }) && allowed.SequenceEqual(shown),
                    "单个relation候选只展示同用途旧节点及可选ID，空用途/self/other不进入该目标列表");
            }
            Require(corrected.Messages[1].Last().content.Contains("decisions[0].target_id") &&
                corrected.Messages[1].All(x => x.role != "assistant"), "纠正反馈包含字段路径，不携带失败正文反复照抄");

            var mixedWrites = new List<BrainCognitionWriteData> { Write(), new() { operation = "weaken", target_id = "relation-node" }, Write("self") };
            var wrongMixed = Reply(Decision("revise", nodes[2].Id, nodes[2].Summary, mixedWrites[0].summary), Decision("new", after: mixedWrites[2].summary, index: 2));
            var rightMixed = Reply(Decision("same", nodes[0].Id, nodes[0].Summary, nodes[0].Summary), Decision("new", after: mixedWrites[2].summary, index: 2));
            var mixed = new AgentSequenceLlm(wrongMixed, rightMixed);
            resolved = await CognitionConsolidationLogic.ResolveAsync(store, mixed, new[] { source }, nodes, mixedWrites,
                () => new BagOfCharsVectorEncoder(), null, CancellationToken.None);
            Require(mixed.Requests.Count == 2 && mixed.Messages[1].Last().content.Contains("identity_slot") &&
                resolved.Writes.Select(x => x.operation).SequenceEqual(new[] { "reinforce", "weaken", "create" }),
                "混合用途与非连续index逐项校验，跨用途引用纠正后保留合法比较及非提案操作");
            using (var input = JsonDocument.Parse(mixed.Messages[0][1].content))
                Require(input.RootElement.GetProperty("proposals").EnumerateArray().All(p =>
                    p.GetProperty("existing_ids").EnumerateArray().Select(x => x.GetString()).SequenceEqual(
                        new[] { p.GetProperty("index").GetInt32() == 0 ? nodes[0].Id : nodes[2].Id })), "多用途批次每个候选有独立目标列表");

            Require(Error(Decision("same", nodes[0].Id, nodes[0].Summary, nodes[0].Summary)) == null &&
                Error(Decision("revise", nodes[0].Id, nodes[0].Summary, write.summary)) == null,
                "合法同用途same/revise的逐字前后认识通过");
            foreach (var item in new[] {
                (Decision("same", "unknown-private-target", "private-before", "private-after"), ".target_id"),
                (Decision("same", nodes[0].Id, "private-before", nodes[0].Summary), ".before"),
                (Decision("same", nodes[0].Id, nodes[0].Summary, "private-after"), ".after"),
                (Decision("revise", nodes[0].Id, nodes[0].Summary, "private-after"), ".after"),
                (Decision("new", nodes[0].Id, "", write.summary), "target_id"),
                (Decision("new", after: "private-after"), ".after"),
                (Decision("insufficient", nodes[0].Id), "target_id"),
                (Decision("unknown-kind"), ".kind"),
                (Decision("new", after: write.summary, index: 99), ".index") })
            {
                var error = Error(item.Item1);
                Require(error?.Contains(item.Item2) == true && !error.Contains("private"), "非法比较具体定位且不回显未知ID/私人正文");
            }
            Require(Error() != null && Error(Decision("insufficient"), Decision("insufficient")) != null &&
                Error(new CognitionConsolidationLogic.Decision { index = 0, kind = "insufficient" }).Contains("explanation"),
                "遗漏、重复数量和空依据仍拒绝");
            var duplicate = new CognitionConsolidationLogic.Review { decisions = new() { Decision("insufficient"), Decision("insufficient") } };
            Require(CognitionConsolidationLogic.ValidationError(duplicate, new[] { Write(), Write() }, nodes).Contains("重复"), "相同数量下重复index仍拒绝");
            var rejected = new AgentSequenceLlm(Reply(mismatched), Reply(mismatched));
            try
            {
                await CognitionConsolidationLogic.ResolveAsync(store, rejected, new[] { source }, nodes, writes,
                    () => new BagOfCharsVectorEncoder(), null, CancellationToken.None);
                throw new Exception("Expected invalid comparison");
            }
            catch (InvalidOperationException error)
            {
                Require(rejected.Requests.Count == 2 && error.Message.Contains("首次错误") && TraceJson.ToJson(writes) == original &&
                    store.GetCognitionNodes(100).Count == 0, "两次非法比较仍停止且不提交任何认知，不把错误改成自动new");
            }
            var empty = new AgentSequenceLlm(Reply(Decision("insufficient")));
            resolved = await CognitionConsolidationLogic.ResolveAsync(store, empty, new[] { source }, Array.Empty<CognitionSliceRecord>(),
                writes, () => new BagOfCharsVectorEncoder(), null, CancellationToken.None);
            Require(empty.Requests.Count == 1 && resolved.Writes.Count == 0, "没有旧目标仍允许模型判断依据不足，不强制创建");
            var cancelled = new AgentSequenceLlm();
            try
            {
                await CognitionConsolidationLogic.ResolveAsync(store, cancelled, new[] { source }, nodes, writes,
                    () => new BagOfCharsVectorEncoder(), null, new CancellationToken(true));
                throw new Exception("Expected cancellation");
            }
            catch (OperationCanceledException) { Require(cancelled.Requests.Count == 0, "取消不进入比较纠正"); }
        }
        finally { Directory.Delete(folder, true); }
        Console.WriteLine("Cognition comparison checks passed: per-proposal purposes, bounded correction, exact text, no writes on rejection and cancellation.");
    }

    private static void RunCognitionComparisonReplay(string requestPath, string responsePath)
    {
        using var request = JsonDocument.Parse(File.ReadAllText(requestPath));
        var user = request.RootElement.GetProperty("messages").EnumerateArray().First(x => x.GetProperty("role").GetString() == "user").GetProperty("content").GetString();
        using var input = JsonDocument.Parse(user);
        var proposals = input.RootElement.GetProperty("proposals").EnumerateArray().ToList();
        var writes = Enumerable.Range(0, proposals.Max(p => p.GetProperty("index").GetInt32()) + 1)
            .Select(_ => new BrainCognitionWriteData { operation = "reinforce" }).ToList();
        foreach (var proposal in proposals)
            writes[proposal.GetProperty("index").GetInt32()] = TraceJson.FromJson<BrainCognitionWriteData>(proposal.GetProperty("proposal").GetRawText());
        var candidates = input.RootElement.GetProperty("existing").EnumerateArray().Select(x => new CognitionSliceRecord {
            Id = x.GetProperty("id").GetString(), Summary = x.GetProperty("summary").GetString(), IdentitySlot = x.GetProperty("identity_slot").GetString() }).ToList();
        var raw = File.ReadAllText(responsePath);raw = raw.Substring(raw.IndexOf('\n') + 1);
        var review = DeepSeekStructuredOutputLogic.Read<CognitionConsolidationLogic.Review>(raw);
        var error = CognitionConsolidationLogic.ValidationError(review, writes, candidates);
        Require(error?.Contains("identity_slot") == true, "原始现场响应应定位为用途不匹配");
        Console.WriteLine("Read-only comparison replay: " + error);
    }
}
