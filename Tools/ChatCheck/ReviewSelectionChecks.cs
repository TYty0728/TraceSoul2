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
        await RunSelectionBudgetRecoveryChecksAsync();
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

        var dir = Path.Combine(Path.GetTempPath(), "review-selection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "brain.db");
            const string day = "2020-08-20";
            var begin = MemoryDayLogic.StartOf(day).ToUnixTimeMilliseconds();
            using var store = new SqliteMemoryManager(path);
            void Seed(string root, string scope, int count, string visibility = "private")
            {
                for (var i = 0; i < count; i++)
                {
                    var id = scope + "-" + i;
                    if (store.GetEvidenceMoments(new[] { id }).Count == 0)
                        store.SaveMoment(new MomentRecord { Id = id, ConversationId = scope, Role = "user",
                            Content = "原始经历" + i, MemoryVisibility = visibility, CreatedUnixMs = begin + i });
                    store.AppendRuntimeSlice(new RuntimeSliceRecord { RootConversationId = root, ContextConversationId = scope,
                        MomentId = id, SnapshotJson = TraceJson.ToJson(new { today = "原始事件标记" + i, inner = "变化" + i }) });
                }
            }
            Seed("nomination-small-over", "nomination-small-over", 13);
            var modestDirectory = TraceJson.ToJson(new { events = new[] { new { summary = new string('长', 122), source_ids = new[] { "s1" } } } });
            var modest = new AgentSequenceLlm(modestDirectory, "{\"summary\":\"完整回望。\"}");
            Require(await RuntimeSliceLogic.ReviewDayAsync(store, modest, "nomination-small-over", day) == 13 && modest.Requests.Count == 2,
                "单项122字但整批目录在预算内，无须额外重写或失败暂停");
            var longDraft = "不要回传的超长草稿" + new string('长', 713); // 722字，超出整批目录预算。
            string Nominations(int count, params int[] longItems) => TraceJson.ToJson(new {
                events = Enumerable.Range(1, count).Select(i => new {
                    summary = longItems.Contains(i) ? longDraft : "已合格事件目录" + i,
                    source_ids = new[] { "s" + i } }).ToList() });
            string Rewrite(params string[] ids) => TraceJson.ToJson(new {
                summaries = ids.Select(id => new { id, summary = "重写后的必要变化" + id }).ToList() });

            Seed("nomination-repair", "nomination-repair", 24);
            var nominationRepair = new AgentSequenceLlm(Nominations(8, 6), Nominations(6, 6), Rewrite("e6"),
                "{\"summary\":\"少数重要事件的当天回望。\"}");
            Require(longDraft.Length == 722 &&
                await RuntimeSliceLogic.ReviewDayAsync(store, nominationRepair, "nomination-repair", day) == 24 &&
                nominationRepair.Requests.Count == 4 && nominationRepair.Requests[1].Contains("1～6项") &&
                !nominationRepair.Requests[1].Contains(longDraft) &&
                nominationRepair.Requests[2].Contains("【事件提名短句重写】") &&
                nominationRepair.Requests[2].Contains("nomination-repair-5｜") &&
                !nominationRepair.Requests[2].Contains("nomination-repair-0｜") &&
                !nominationRepair.Requests[2].Contains(longDraft) &&
                !nominationRepair.Requests[2].Contains("已合格事件目录1") &&
                nominationRepair.Requests[3].Contains("已合格事件目录1") &&
                nominationRepair.Requests[3].Contains("重写后的必要变化e6") &&
                !nominationRepair.Requests[3].Contains(longDraft) &&
                store.GetRuntimeDaySummaries("nomination-repair", day).Count == 1,
                "8件纠正为6件后只重写超出整批预算的目录，合格描述与来源保留，完整原件不截断");

            Seed("nomination-resume", "nomination-resume", 13);
            var tooLong = TraceJson.ToJson(new { summaries = new[] { new { id = "e1", summary = longDraft } } });
            var shortFailure = new AgentSequenceLlm(Nominations(2, 1), tooLong);
            var failed = false;
            try { await RuntimeSliceLogic.ReviewDayAsync(store, shortFailure, "nomination-resume", day); }
            catch (InvalidOperationException error) { failed = error.Message.Contains("720字") && error.Message.Contains("检查点"); }
            Require(failed && shortFailure.Requests.Count == 2 &&
                store.GetRuntimeSlices("nomination-resume", day, true).Count == 13 &&
                store.GetRuntimeDayReviews("nomination-resume", day).Count == 0,
                "短句重写仍超120字立即停止，不追加循环请求或将不合格提名标成完成");
            using (var reopened = new SqliteMemoryManager(path))
            {
                var resumeShort = new AgentSequenceLlm(Rewrite("e1"), "{\"summary\":\"恢复后的一篇回望。\"}");
                Require(await RuntimeSliceLogic.ReviewDayAsync(reopened, resumeShort, "nomination-resume", day) == 13 &&
                    resumeShort.Requests.Count == 2 && resumeShort.Requests[0].Contains("【事件提名短句重写】") &&
                    resumeShort.Requests[1].Contains("已合格事件目录2"),
                    "重启复用结构合格的提名检查点，只补写超长短句及成稿，不重新选择整批事件");
            }

            Seed("nomination-multiple", "nomination-multiple", 13);
            var multipleShort = new AgentSequenceLlm(Nominations(3, 1, 3), Rewrite("e3", "e1"),
                "{\"summary\":\"多条短句一并修好。\"}");
            Require(await RuntimeSliceLogic.ReviewDayAsync(store, multipleShort, "nomination-multiple", day) == 13 &&
                multipleShort.Requests.Count == 3 && multipleShort.Requests[1].Contains("nomination-multiple-0｜") &&
                multipleShort.Requests[1].Contains("nomination-multiple-2｜") &&
                !multipleShort.Requests[1].Contains("nomination-multiple-1｜") &&
                multipleShort.Requests[2].Contains("已合格事件目录2"),
                "多条超长短句合为一次请求，乱序返回按稳定编号更新，未超长条目保持原样");

            var badRewrites = new[] { "{}", "{", Rewrite("e2"), Rewrite("e1", "e1"),
                "{\"summaries\":[{\"id\":\"e1\",\"summary\":\"\"}]}", tooLong };
            for (var i = 0; i < badRewrites.Length; i++)
            {
                var scope = "nomination-invalid-" + i;
                Seed(scope, scope, 13);
                var badRewrite = new AgentSequenceLlm(Nominations(1, 1), badRewrites[i]);
                failed = false;
                try { await RuntimeSliceLogic.ReviewDayAsync(store, badRewrite, scope, day); }
                catch (InvalidOperationException) { failed = true; }
                Require(failed && badRewrite.Requests.Count == 2 && store.GetRuntimeSlices(scope, day, true).Count == 13 &&
                    store.GetRuntimeDayReviews(scope, day).Count == 0,
                    "短句重写缺项、语法失败、未知或重复编号、空正文及持续超长均不提交且只调用一次");
            }

            Seed("nomination-cancel", "nomination-cancel", 13);
            using (var cancellation = new CancellationTokenSource())
            {
                var cancelRewrite = new SelectionProbeLlm { Reply = messages => {
                    if (!messages[0].content.Contains("【事件提名短句重写】")) return Nominations(1, 1);
                    cancellation.Cancel();
                    return Rewrite("e1");
                } };
                var wasCanceled = false;
                try { await RuntimeSliceLogic.ReviewDayAsync(store, cancelRewrite, "nomination-cancel", day, cancellation.Token); }
                catch (OperationCanceledException) { wasCanceled = true; }
                Require(wasCanceled && cancelRewrite.Requests.Count == 2 &&
                    store.GetRuntimeSlices("nomination-cancel", day, true).Count == 13 &&
                    store.GetRuntimeDayReviews("nomination-cancel", day).Count == 0,
                    "短句重写后取消继续向上传播，不保存为合格提名或发布成稿");
            }
            Seed("resume", "resume", 81);
            var broken = new AgentSequenceLlm(Nominate("s1", "s2"), Nominate("s1"), "{}", "{}");
            failed = false;
            try { await RuntimeSliceLogic.ReviewDayAsync(store, broken, "resume", day); }
            catch (InvalidOperationException) { failed = true; }
            Require(failed && broken.Requests.Count == 4 && store.GetRuntimeSlices("resume", day, true).Count == 81 &&
                store.GetRuntimeDayReviews("resume", day).Count == 0,
                "成稿失败保留所有原切片为待处理，合格批次提名已存档，不把未入围当作删除");
            using (var reopened = new SqliteMemoryManager(path))
            {
                var finish = new AgentSequenceLlm(TraceJson.ToJson(new { summary = new string(' ', 1001) + "这天有几处重要转折。" }));
                Require(await RuntimeSliceLogic.ReviewDayAsync(reopened, finish, "resume", day) == 81 && finish.Requests.Count == 1 &&
                    finish.Requests[0].Contains("resume-0｜") && finish.Requests[0].Contains("resume-80｜") &&
                    !finish.Requests[0].Contains("resume-30｜") &&
                    TraceJson.FromJson<List<string>>(reopened.GetRuntimeDaySummaries("resume", day).Single().SliceIdsJson).Count == 81 &&
                    reopened.GetRuntimeSlices("resume", day).Count == 81,
                    "重启复用提名，只对入围完整依据成稿；未入围仍保留可查证，整日处理来源完整");
                var noCalls = new AgentSequenceLlm();
                await RuntimeSliceLogic.ReviewDayAsync(reopened, noCalls, "resume", day);
                Require(noCalls.Requests.Count == 0, "已有有效成稿不重复调用");
            }
            Seed("resume", "resume", 82);
            var late = new AgentSequenceLlm(Nominate("s2"), "{\"summary\":\"新结果改变了原来的认识。\"}");
            Require(await RuntimeSliceLogic.ReviewDayAsync(store, late, "resume", day) == 1 && late.Requests.Count == 2 &&
                late.Requests[0].Contains("resume-81｜") && !late.Requests[0].Contains("resume-0｜") &&
                store.GetRuntimeDaySummaries("resume", day).Single().Summary == "新结果改变了原来的认识。",
                "迟到来源只使所属提名批次失效，重新全日成稿，不拼回旧长段");

            Seed("privacy", "privacy", 13);
            Seed("privacy", "privacy:environment:public", 13, "public");
            var privacy = new AgentSequenceLlm(Nominate("s1"), "{\"summary\":\"私密回望。\"}",
                Nominate("s1"), "{\"summary\":\"公开回望。\"}");
            Require(await RuntimeSliceLogic.ReviewDayAsync(store, privacy, "privacy", day) == 26 &&
                !privacy.Requests[2].Contains("runtime:privacy-0｜") && !privacy.Requests[3].Contains("私密回望") &&
                store.GetRuntimeDaySummaries("privacy", day).Count == 2,
                "提名、候选与成稿都按环境和可见性隔离");

            Seed("unknown", "unknown", 13);
            var wrongSource = new AgentSequenceLlm(Nominate("foreign"), Nominate("foreign"));
            try { await RuntimeSliceLogic.ReviewDayAsync(store, wrongSource, "unknown", day); } catch (InvalidOperationException) { }
            Require(wrongSource.Requests.Count == 2 && store.GetRuntimeSlices("unknown", day, true).Count == 13 &&
                store.GetRuntimeDayReviews("unknown", day).Count == 0, "跨批次或未知来源不能提交提名与完成标记");

            Seed("crowded", "crowded", 170);
            var crowded = new SelectionProbeLlm { Reply = messages => {
                if (messages[0].content.Contains("【当天回望事件提名】"))
                    return Nominate("s1", "s2", "s3", "s4", "s5", "s6");
                if (messages[0].content.Contains("【复盘候选入围】"))
                {
                    using var json = JsonDocument.Parse(messages[1].content);
                    return Pick(json.RootElement[json.RootElement.GetArrayLength() - 1].GetProperty("id").GetString());
                }
                return "{\"summary\":\"全局筛选后的少数重要变化。\"}";
            } };
            Require(await RuntimeSliceLogic.ReviewDayAsync(store, crowded, "crowded", day) == 170 &&
                crowded.Requests.Count == 5 && crowded.Requests[3].Contains("【复盘候选入围】") &&
                crowded.Requests[4].Contains("crowded-165｜") && !crowded.Requests[4].Contains("crowded-0｜") &&
                crowded.Requests.All(x => x.Length < 18000) && store.GetRuntimeSlices("crowded", day).Count == 170,
                "170条切片先分批提名18件，再全局选取并只加载入围原件成稿，不先写三篇长文");

            Seed("race", "race", 13);
            var race = new SelectionProbeLlm { Reply = messages => {
                if (messages[0].content.Contains("事件提名")) return Nominate("s1");
                Seed("race", "race", 14);
                return "{\"summary\":\"旧快照的回望。\"}";
            } };
            failed = false;
            try { await RuntimeSliceLogic.ReviewDayAsync(store, race, "race", day); } catch (InvalidOperationException) { failed = true; }
            Require(failed && store.GetRuntimeDaySummaries("race", day).Count == 0 && store.GetRuntimeSlices("race", day, true).Count == 1,
                "成稿期间新来源使提交失败，旧快照不能被当成完整当天回望");
            var recover = new AgentSequenceLlm(Nominate("s14"), "{\"summary\":\"补入新结果的完整回望。\"}");
            await RuntimeSliceLogic.ReviewDayAsync(store, recover, "race", day);
            Require(store.GetRuntimeDaySummaries("race", day).Single().Summary == "补入新结果的完整回望。",
                "并发失败后可恢复，重新提名包含迟到来源");
            var canceled = new AgentSequenceLlm();
            try { await RuntimeSliceLogic.ReviewDayAsync(store, canceled, "unknown", day, new CancellationToken(true)); }
            catch (OperationCanceledException) { }
            Require(canceled.Requests.Count == 0 && store.GetRuntimeSlices("unknown", day, true).Count == 13,
                "取消不调用模型或标记切片完成");
        }
        finally { Directory.Delete(dir, true); }
        Console.WriteLine("Review selection checks passed: bounded selection, provenance, privacy, restart, late data and concurrency.");
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

    private static async Task RunSelectionBudgetRecoveryChecksAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "selection-budget-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "brain.db");
            const string root = "budget-recovery", day = "2020-08-20";
            var begin = MemoryDayLogic.StartOf(day).ToUnixTimeMilliseconds();
            var snapshots = new Dictionary<string, string>();
            using (var store = new SqliteMemoryManager(path))
            {
                for (var i = 0; i < 40; i++)
                {
                    var id = "budget-moment-" + i.ToString("D2");
                    store.SaveMoment(new MomentRecord { Id = id, ConversationId = root, Role = "user",
                        Content = "原始经历" + i, MemoryVisibility = "private", CreatedUnixMs = begin + i });
                    store.AppendRuntimeSlice(new RuntimeSliceRecord { RootConversationId = root, ContextConversationId = root,
                        MomentId = id, SnapshotJson = TraceJson.ToJson(new { today = "完整原件" + i, inner = new string('材', 2100) }) });
                }
                foreach (var slice in store.GetRuntimeSlices(root, day)) snapshots.Add(slice.Id, slice.SnapshotJson);
                var failed = new SelectionProbeLlm { Reply = messages => {
                    if (messages[0].content.Contains("【当天回望事件提名】"))
                    {
                        var aliases = messages[1].content.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                            .Select(line => line.Substring(0, line.IndexOf('｜'))).ToArray();
                        return TraceJson.ToJson(new { events = new[] { new { summary = "一件事的完整转折和结果", source_ids = aliases } } });
                    }
                    Require(messages[0].content.Contains("【复盘候选入围】"), "超预算失败不应进入回望成稿");
                    return Pick("i1", "i2");
                } };
                var stopped = false;
                try { await RuntimeSliceLogic.ReviewDayAsync(store, failed, root, day); }
                catch (InvalidOperationException error) { stopped = error.Message.Contains("超额"); }
                Require(stopped && failed.Requests.Count(x => x.Contains("【复盘候选入围】")) == 2 &&
                    store.GetRuntimeSlices(root, day, true).Count == 40 && store.GetRuntimeDayReviews(root, day).Count == 0 &&
                    store.GetRuntimeDaySummaries(root, day).Count == 0,
                    "入围两次超预算后停止，全部原切片待处理，不发布回望或冒充完成");
            }
            using (var reopened = new SqliteMemoryManager(path))
            {
                var resume = new SelectionProbeLlm { Reply = messages => {
                    if (messages[0].content.Contains("【复盘候选入围】")) return Pick("i1");
                    Require(messages[0].content.Contains("【当天回望入围成稿】") &&
                        messages[1].content.Contains("budget-moment-00") &&
                        messages[1].content.Contains(new string('材', 2100)) && !messages[1].content.Contains("budget-moment-39"),
                        "恢复只重选编号并从入围的完整原切片成稿，不重复提名或截断依据");
                    return "{\"summary\":\"恢复后按关键转折写成的完整回望。\"}";
                } };
                Require(await RuntimeSliceLogic.ReviewDayAsync(reopened, resume, root, day) == 40 &&
                    resume.Requests.Count == 2 && reopened.GetRuntimeDaySummaries(root, day).Count == 1 &&
                    reopened.GetEvidenceMoments(Enumerable.Range(0, 40).Select(i => "budget-moment-" + i.ToString("D2"))).Count == 40 &&
                    reopened.GetRuntimeSlices(root, day).All(x => snapshots[x.Id] == x.SnapshotJson),
                    "跨实例恢复复用全部提名，两次请求完成入围/成稿；未入围原件与所有来源保持");
            }
        }
        finally { Directory.Delete(dir, true); }
        Console.WriteLine("Selection budget recovery checks passed: failed selection preserves nominations and complete original sources across restart.");
    }

    private static void RunReviewSelectionMigrationChecks(Assembly assembly)
    {
        var dir = Path.Combine(Path.GetTempPath(), "selection-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        IDisposable context = null;
        try
        {
            var contextType = assembly.GetType("TraceSoul2.Migrate.MigrationContext", true);
            var storeType = assembly.GetType("TraceSoul2.Manager.SqliteMemoryManager", true);
            var path = Path.Combine(dir, "brain.db");
            context = (IDisposable)Activator.CreateInstance(contextType);
            var storeObject = Activator.CreateInstance(storeType, path);
            var store = (IMemoryStore)storeObject;
            contextType.GetProperty("Store").SetValue(context, storeObject);
            contextType.GetProperty("Migration").SetValue(context, Activator.CreateInstance(
                assembly.GetType("TraceSoul2.Migrate.MigrationDb", true), Path.Combine(dir, "migration.db"), path));
            store.SavePairIdentity("本人", "同伴", "称呼");
            using (var db = new SQLite.SQLiteConnection(path))
            {
                foreach (var node in Enumerable.Range(0, 50).Select(i => new CognitionSliceRecord {
                    Id = "chosen-node-" + i.ToString("D2"), Status = "active", IdentitySlot = "relation",
                    Summary = i == 49 ? "最后的新反证标记" : "旧重复理解" + i, Scope = "适用条件完整保留",
                    Exceptions = "例外也完整保留", CreatedUnixMs = i, MemoryVisibility = "private", ContextConversationId = "tracesoul2" })) db.Insert(node);
                db.Insert(new CognitionSliceRecord { Id = "public-node", Status = "active", IdentitySlot = "relation", Summary = "公开无权候选标记", MemoryVisibility = "public" });
                db.Insert(new CognitionSliceRecord { Id = "pinned-node", Status = "active", IdentitySlot = "self", Summary = "固定卡候选标记" });
            }
            var model = new SelectionProbeLlm { Reply = messages => {
                if (messages[0].content.Contains("【复盘候选入围】"))
                {
                    using var json = JsonDocument.Parse(messages[1].content);
                    return Pick(json.RootElement[json.RootElement.GetArrayLength() - 1].GetProperty("id").GetString());
                }
                return TraceJson.ToJson(new { cards = new[] { new { slot = "relation", body = "结合新的反证修订理解。",
                    cognition_ids = new[] { "chosen-node-49" } } } });
            } };
            var cards = IdentityCardSlotValues.All.Select(slot => new IdentityCardRecord { Slot = slot,
                Body = "原有稳定认识", Pinned = slot == "personality" || slot == "self" }).ToList();
            var run = assembly.GetType("TraceSoul2.Migrate.DayBuilder", true)
                .GetMethod("RunDayReviewAsync", BindingFlags.NonPublic | BindingFlags.Static);
            ((Task)run.Invoke(null, new object[] { context, store.LoadPairIdentity(), model, "2020-08-20", cards, "",
                new List<EventIndexRecord> { new() { Id = "event", EventSummary = "不应进入身份长日记标记" } },
                new List<EventEntryRecord> { new() { Id = "detail", IndexId = "event", Summary = "冗余事件", Detail = new string('长', 30000) } } })).GetAwaiter().GetResult();
            var final = model.Requests.Last();
            Require(model.Requests.Count > 1 && final.Contains("chosen-node-49") && final.Contains("最后的新反证标记") &&
                final.Contains("适用条件完整保留") && final.Contains("例外也完整保留") &&
                !final.Contains("chosen-node-00") && !final.Contains("不应进入身份长日记标记") && final.Length < 15000 &&
                model.Requests.All(x => !x.Contains("公开无权候选标记") && !x.Contains("固定卡候选标记")),
                "实际身份复盘先按slot筛选全部可见候选，40条以后的反证可入围，完整条件保留，全天冗余细节退出成稿输入");
            Require(((ICognitionGraphStore)store).GetCognitionNodes(100).Count == 52,
                "身份入围只改变本次摘要输入，不删除未入围认知或固定设定");
            var noGrowth = new SelectionProbeLlm { Reply = messages =>
                messages[0].content.Contains("【复盘候选入围】") ? Pick() : "{\"cards\":[]}" };
            var noGrowthTask = (Task)run.Invoke(null, new object[] { context, store.LoadPairIdentity(), noGrowth, "2020-08-20", cards, "",
                new List<EventIndexRecord>(), new List<EventEntryRecord>() });
            noGrowthTask.GetAwaiter().GetResult();
            var noGrowthOutput = noGrowthTask.GetType().GetProperty("Result").GetValue(noGrowthTask);
            Require(((System.Collections.IList)noGrowthOutput.GetType().GetField("cards").GetValue(noGrowthOutput)).Count == 0 &&
                !noGrowth.Requests.Last().Contains("chosen-node-") &&
                ((ICognitionGraphStore)store).GetCognitionNodes(100).Count == 52,
                "实际身份复盘允许各批全不入围、整轮不更新，候选仍完整留存且不强行凑一张卡");
        }
        finally { context?.Dispose(); Directory.Delete(dir, true); }
        Console.WriteLine("Review selection Migration checks passed: actual day review, late-ranked cognition, slot and visibility.");
    }
}
