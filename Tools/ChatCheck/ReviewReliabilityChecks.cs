using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Host;
using TraceSoul2.Logic;
using TraceSoul2.Manager;
using TraceSoul2.Util;

internal static partial class Program
{
    public sealed class ProseFixture { public string source; public List<string> parts; }
    private static async Task RunReviewReliabilityChecksAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "review-reliability-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "brain.db");
        var messages = new List<DeepSeekMessageData> { new("user", "original-source-v1") };
        string Validate(ProseFixture x) => x.source == "original-source-v1" && x.parts?.Count == 3 ? null : "来源或字段不匹配";
        IEnumerable<ReviewWorkLogic.TextPart> Parts(ProseFixture x) => x.parts.Select((_, i) =>
            new ReviewWorkLogic.TextPart("parts[" + i + "]", 10, () => x.parts[i], v => x.parts[i] = v));
        try
        {
            await CheckReviewProcessRecoveryAsync(dir);
            using (var store = new SqliteMemoryManager(path))
            {
                var llm = new AgentSequenceLlm(TraceJson.ToJson(new ProseFixture { source = "original-source-v1", parts = new() { "good", new string('x', 30), new string('y', 30) } }),
                    "{\"text\":\"fixed\"}", TraceJson.ToJson(new { text = new string('z', 30) }));
                var pending = false;
                try { await ReviewWorkLogic.CompleteAsync<ProseFixture>(store, "fixture", llm, messages, Validate, Parts); }
                catch (ReviewProsePendingException) { pending = true; }
                Require(pending && llm.Requests.Count == 3, "长字段逐项处理，失败项不丢弃合格字段");
                var idle = new AgentSequenceLlm();
                try { await ReviewWorkLogic.CompleteAsync<ProseFixture>(store, "fixture", idle, messages, Validate, Parts); }
                catch (ReviewProsePendingException) { }
                Require(idle.Requests.Count == 0, "退避期间不调用模型");
            }
            using (var store = new SqliteMemoryManager(path))
            {
                using (var db = new SQLite.SQLiteConnection(path))
                    db.Execute("UPDATE plugin_documents SET Json=json_set(Json, '$.NextAttemptUnixMs', 0) WHERE PluginId=?", ReviewWorkLogic.StoreId);
                var llm = new AgentSequenceLlm("{\"text\":\"last\"}");
                var done = await ReviewWorkLogic.CompleteAsync<ProseFixture>(store, "fixture", llm, messages, Validate, Parts);
                Require(llm.Requests.Count == 1 && done.parts.SequenceEqual(new[] { "good", "fixed", "last" }) && done.source == "original-source-v1",
                    "重开数据库只补剩余字段，结构和合格正文保持");
                var cached = new AgentSequenceLlm();
                await ReviewWorkLogic.CompleteAsync<ProseFixture>(store, "fixture", cached, messages, Validate, Parts);
                Require(cached.Requests.Count == 0, "完成后零重复请求");
                var invalid = new AgentSequenceLlm("{}", "{}"); var rejected = false;
                try { await ReviewWorkLogic.CompleteAsync<ProseFixture>(store, "bad-source", invalid, messages, Validate, Parts); }
                catch (ReviewProsePendingException) { throw new Exception("结构错误不可吞成待整理"); }
                catch (InvalidOperationException) { rejected = true; }
                Require(rejected && invalid.Requests.Count == 2, "来源错误最多一次纠正并严格失败");
                var chosen = new AgentSequenceLlm(Pick("i2", "i1", "i3"));
                var selected = await ReviewSelectionLogic.SelectAsync(chosen, new[] {
                    new ReviewSelectionLogic.Candidate { Id = "a", Description = "a", EvidenceChars = 9000 },
                    new ReviewSelectionLogic.Candidate { Id = "b", Description = "b", EvidenceChars = 8982 },
                    new ReviewSelectionLogic.Candidate { Id = "c", Description = "c", EvidenceChars = 500 } }, "budget", 3, 17818, fitBudget: true, store: store);
                Require(chosen.Requests.Count == 1 && selected.Select(x => x.Id).SequenceEqual(new[] { "b", "c" }) && selected.Sum(x => x.EvidenceChars) == 9482,
                    "17982超预算由程序装入完整项，保持模型优先顺序，不重问算术");
            }
            var gate = new ReviewMaintenanceGate(() => ReviewExecutionLease.Acquire(dir));
            Require(gate.TryWork(), "工作获得互斥");
            var reservation = gate.ReserveAsync(CancellationToken.None);
            Require(!reservation.IsCompleted && !gate.TryWork(), "维护预订立即拒绝新工作并等待当前工作结束");
            gate.EndWork();
            using (await reservation)
            {
                var locked = false;
                try { using var illegal = ReviewExecutionLease.Acquire(dir); } catch (InvalidOperationException) { locked = true; }
                Require(locked && !gate.TryWork(), "备份快路径与交接期间仍持有跨进程锁");
            }
            Require(gate.TryWork(), "维护失败/放弃释放工作锁"); gate.EndWork();
            using (var otherProcess = ReviewExecutionLease.Acquire(dir))
            {
                var rejected = false;
                try { using var lease = await gate.ReserveAsync(CancellationToken.None); } catch (InvalidOperationException) { rejected = true; }
                Require(rejected && gate.TryWork(), "独立迁移持锁时维护失败，预订正确释放"); gate.EndWork();
            }
            Require(gate.TryWork(), "取消测试获得工作锁");
            using (var cancel = new CancellationTokenSource())
            {
                var pending = gate.ReserveAsync(cancel.Token); cancel.Cancel();
                try { await pending; } catch (OperationCanceledException) { }
            }
            gate.EndWork(); Require(gate.TryWork(), "等待维护取消后可继续工作"); gate.EndWork();
            var states = new[] {
                new DailyReviewState { DayKey = "2020-08-01", Status = "deferred", RetryCount = 1, NextRetryUnixMs = 1 },
                new DailyReviewState { DayKey = "2020-08-02", Status = "waiting" },
                new DailyReviewState { DayKey = "2020-08-03", Status = "deferred", RetryCount = 3, NextRetryUnixMs = 1 } };
            var plan = DailyBuildPreflight.Plan("2020-08-04", "2020-08-04", Array.Empty<string>(), states, Array.Empty<DailyBuildAttempt>(), includePendingReviews: false);
            Require(plan.HistoricalFailures.Count == 0 && plan.Days.SequenceEqual(new[] { "2020-08-01", "2020-08-04" }),
                "只自动补做到期项，等待依赖和预算耗尽不循环调用、不触发全局暂停");
            states[0].Status = "done";
            plan = DailyBuildPreflight.Plan("2020-08-04", "2020-08-04", Array.Empty<string>(), states, Array.Empty<DailyBuildAttempt>(), includePendingReviews: false);
            Require(plan.Days.Contains("2020-08-02"), "前置完成后等待项自动解锁");
        }
        finally { Directory.Delete(dir, true); }
        Console.WriteLine("Review reliability passed: durable field recovery, strict sources, bounded fitting, backoff, dependencies and maintenance races.");
    }

    private static async Task CheckReviewProcessRecoveryAsync(string dir)
    {
        Process Start(string mode)
        {
            var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add(typeof(Program).Assembly.Location);
            info.ArgumentList.Add("--review-recovery-peer");
            info.ArgumentList.Add(dir);
            info.ArgumentList.Add(mode);
            return Process.Start(info);
        }
        using (var writer = Start("write"))
        {
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (!File.Exists(Path.Combine(dir, "peer-ready")) && !writer.HasExited && DateTime.UtcNow < deadline)
                    await Task.Delay(25);
                Require(File.Exists(Path.Combine(dir, "peer-ready")), "独立进程应先持久化草稿检查点");
            }
            finally
            {
                if (!writer.HasExited) writer.Kill(entireProcessTree: true);
                await writer.WaitForExitAsync();
            }
        }
        using var reader = Start("resume");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await reader.WaitForExitAsync(timeout.Token); }
        finally { if (!reader.HasExited) { reader.Kill(entireProcessTree: true); await reader.WaitForExitAsync(); } }
        Require(reader.ExitCode == 0, "杀死原进程后，新进程只修剩余字段，不重新生成已保存草稿");
    }

    private static async Task RunReviewRecoveryPeerAsync(string dir, string mode)
    {
        var path = Path.Combine(dir, "peer.db");
        using var store = new SqliteMemoryManager(path);
        var messages = new List<DeepSeekMessageData> { new("user", "original-source-v1") };
        string Validate(ProseFixture x) => x.source == "original-source-v1" && x.parts?.Count == 2 ? null : "来源或字段不匹配";
        IEnumerable<ReviewWorkLogic.TextPart> Parts(ProseFixture x) => x.parts.Select((_, i) =>
            new ReviewWorkLogic.TextPart("parts[" + i + "]", 10, () => x.parts[i], v => x.parts[i] = v));
        if (mode == "write")
        {
            var llm = new AgentSequenceLlm(TraceJson.ToJson(new ProseFixture { source = "original-source-v1", parts = new() { "keep", new string('x', 30) } }),
                TraceJson.ToJson(new { text = new string('y', 30) }));
            try { await ReviewWorkLogic.CompleteAsync<ProseFixture>(store, "peer", llm, messages, Validate, Parts); }
            catch (ReviewProsePendingException) { }
            Require(llm.Requests.Count == 2, "首次进程保存草稿与待补字段");
            File.WriteAllText(Path.Combine(dir, "peer-ready"), "ready");
            await Task.Delay(Timeout.Infinite);
        }
        else if (mode == "resume")
        {
            using (var db = new SQLite.SQLiteConnection(path))
                db.Execute("UPDATE plugin_documents SET Json=json_set(Json, '$.NextAttemptUnixMs', 0) WHERE PluginId=?", ReviewWorkLogic.StoreId);
            var llm = new AgentSequenceLlm("{\"text\":\"fixed\"}");
            var result = await ReviewWorkLogic.CompleteAsync<ProseFixture>(store, "peer", llm, messages, Validate, Parts);
            Require(llm.Requests.Count == 1 && result.parts.SequenceEqual(new[] { "keep", "fixed" }),
                "跨进程恢复保留合格字段且只调用剩余字段一次");
        }
        else throw new ArgumentException("Unknown peer mode");
    }
}
