using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Logic;
using TraceSoul2.Manager;
using TraceSoul2.Util;

internal static partial class Program
{
    private sealed class SelectionProbeLlm : ILlmClient
    {
        public string ProviderId => "selection-check";
        public string Model => "offline";
        public readonly List<string> Requests = new();
        public Func<List<DeepSeekMessageData>, string> Reply;
        public Task<string> CompleteJsonAsync(List<DeepSeekMessageData> messages, CancellationToken cancellationToken = default, string promptCacheKey = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(string.Join("\n", messages.Select(x => x.content)));
            return Task.FromResult(Reply(messages));
        }
        public Task<string> CompleteTextAsync(List<DeepSeekMessageData> messages, CancellationToken cancellationToken = default, string promptCacheKey = null)
            => throw new InvalidOperationException("复盘只用结构化调用。");
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(new[] { Model });
    }

    private static string Pick(params string[] ids) => TraceJson.ToJson(new { selected_ids = ids });
    private static string Nominate(params string[] ids) => TraceJson.ToJson(new {
        events = ids.Select(id => new { summary = "重要变化" + id, source_ids = new[] { id } }).ToArray() });

    private static async Task RunReviewSelectionChecksAsync()
    {
        await RunSelectionEvidenceBudgetChecksAsync();
        var candidates = Enumerable.Range(0, 140).Select(i => new ReviewSelectionLogic.Candidate {
            Id = "real-" + i, Description = "候选" + i, EvidenceChars = 100 }).ToList();
        var seen = new HashSet<string>();
        var rank = new SelectionProbeLlm { Reply = messages => {
            using var doc = JsonDocument.Parse(messages[1].content);
            foreach (var item in doc.RootElement.EnumerateArray()) seen.Add(item.GetProperty("description").GetString());
            return Pick(doc.RootElement[doc.RootElement.GetArrayLength() - 1].GetProperty("id").GetString());
        } };
        var chosen = await ReviewSelectionLogic.SelectAsync(rank, candidates, "测试全局候选", 3, 1000);
        Require(seen.Count == 140 && chosen.Count <= 3 && chosen.Any(x => x.Id == "real-139") &&
            rank.Requests.All(x => x.Length < 9000) && rank.Requests.Count < 15,
            "大量目录分批提名再全局入围，晚出现的重要候选不被Take截掉，输入有界且递归收敛");
        var invalid = new AgentSequenceLlm(Pick("not-an-id"), Pick("i1"));
        var repaired = await ReviewSelectionLogic.SelectAsync(invalid, candidates.Take(8).ToList(), "校验", 2, 1000);
        Require(repaired.Single().Id == "real-0" && invalid.Requests.Count == 2 &&
            invalid.Requests[1].Contains("只能复制本次目录"), "入围必须是本批真实编号，纠正后才加载依据");
        var empty = new AgentSequenceLlm(Pick());
        Require((await ReviewSelectionLogic.SelectAsync(empty, candidates.Take(8).ToList(), "身份候选", 2, 1000,
            allowEmpty: true)).Count == 0 && empty.Requests.Count == 1,
            "身份候选没有值得沉淀的内容时可以明确全不选，不强制纠正为空的合法判断");
        var emptyBatches = new SelectionProbeLlm { Reply = _ => Pick() };
        Require((await ReviewSelectionLogic.SelectAsync(emptyBatches, candidates.Take(25).ToList(), "身份候选", 2, 1000,
            allowEmpty: true)).Count == 0 && emptyBatches.Requests.Count == 2,
            "分批身份选择的单项尾批也可不入围，不能因为只剩一项就强行保留");
        var missing = new AgentSequenceLlm("{}", Pick());
        Require((await ReviewSelectionLogic.SelectAsync(missing, candidates.Take(8).ToList(), "身份候选", 2, 1000,
            allowEmpty: true)).Count == 0 && missing.Requests.Count == 2,
            "允许空入围仍拒绝缺失字段，不能将格式失败冒充全不选");
        var required = new AgentSequenceLlm(Pick(), Pick("i1"));
        Require((await ReviewSelectionLogic.SelectAsync(required, candidates.Take(8).ToList(), "当天回望", 2, 1000))
            .Single().Id == "real-0" && required.Requests.Count == 2,
            "身份的空入围许可不改变当天回望要求保留有效来源的契约");
        foreach (var bad in new[] { Pick("i1", "i1"), Pick("i1", "i2"), "{}" })
        {
            var model = new AgentSequenceLlm(bad, bad);
            var failed = false;
            try { await ReviewSelectionLogic.SelectAsync(model, candidates.Take(8).ToList(), "预算", 2, 100); }
            catch (InvalidOperationException) { failed = true; }
            Require(failed && model.Requests.Count == 2, "重复、超依据预算或缺失入围结果最多纠正一次，不能静默裁切");
        }

        Console.WriteLine("Review selection checks passed: bounded candidates and evidence budget.");
    }

    private static async Task RunSelectionEvidenceBudgetChecksAsync()
    {
        // 现场仅保留成本和短编号；合成描述不包含私人原文。
        var costs = new[] { 3354, 1454, 1561, 5095, 2694, 1503, 1938, 1904, 1475, 1238, 2035, 2823,
            1481, 2128, 1653, 2530, 1595, 3922, 1031, 1202, 1811, 2535, 1391, 2551 };
        var candidates = costs.Select((cost, i) => new ReviewSelectionLogic.Candidate {
            Id = "budget-source-" + i, Description = "独立事件" + i, EvidenceChars = cost }).ToList();
        var first = Pick("i1", "i4", "i5", "i12", "i13", "i22");
        var second = Pick("i1", "i4", "i12", "i13", "i16", "i22");
        var corrected = Pick("i1", "i4", "i12", "i13", "i22");
        var model = new AgentSequenceLlm(first, corrected);
        var selected = await ReviewSelectionLogic.SelectAsync(model, candidates, "回望预算回归", 6, 16000);
        Require(selected.Count == 5 && selected.Sum(x => x.EvidenceChars) == 15288 && model.Requests.Count == 2 &&
            model.Requests[1].Contains(first) && model.Requests[1].Contains("合计17982字") &&
            model.Requests[1].Contains("上限16000字") && model.Requests[1].Contains("超额1982字") &&
            model.Requests[1].Contains("i4=5095") && model.Requests[1].Contains("只修改selected_ids") &&
            !model.Requests[1].Contains("新正文必须更短") && !model.Requests[1].Contains("budget-source-"),
            "依据预算纠正保留短编号输出，反馈精确合计/超额及成本，不误用正文精简或暴露原始ID");
        var repeated = new AgentSequenceLlm(first, second);
        string failure = null;
        try { await ReviewSelectionLogic.SelectAsync(repeated, candidates, "持续超预算", 6, 16000); }
        catch (InvalidOperationException error) { failure = error.Message; }
        Require(failure != null && failure.Contains("合计17982字") && failure.Contains("合计17818字") &&
            failure.Contains("超额1818字") && repeated.Requests.Count == 2 &&
            costs.SequenceEqual(candidates.Select(x => x.EvidenceChars)),
            "现场两次超预算原样回放仍停止，两次具体错误保留，不裁切候选或增加第三次请求");
        var boundary = new List<ReviewSelectionLogic.Candidate> {
            new() { Id = "a", Description = "事件一", EvidenceChars = 8000 },
            new() { Id = "b", Description = "事件二", EvidenceChars = 8000 },
            new() { Id = "c", Description = "事件三", EvidenceChars = 1 } };
        var exact = new AgentSequenceLlm(Pick("i1", "i2"));
        Require((await ReviewSelectionLogic.SelectAsync(exact, boundary, "恰好预算", 6, 16000)).Count == 2 &&
            exact.Requests.Count == 1, "完整依据恰好16000字一次通过，少选不因未满数量而纠正");
        var large = new[] { new ReviewSelectionLogic.Candidate { Id = "large-a", Description = "大依据一", EvidenceChars = int.MaxValue },
            new ReviewSelectionLogic.Candidate { Id = "large-b", Description = "大依据二", EvidenceChars = int.MaxValue } };
        var overflow = new AgentSequenceLlm(Pick("i1", "i2"), Pick("i1"));
        Require((await ReviewSelectionLogic.SelectAsync(overflow, large, "大预算", 2, int.MaxValue)).Count == 1 &&
            overflow.Requests.Count == 2 && overflow.Requests[1].Contains("合计4294967294字"),
            "依据合计使用长整数，不能溢出后错误通过预算校验");
        Console.WriteLine("Selection evidence budget checks passed: exact feedback, bounded repair, recorded costs and strict limits.");
    }

}
