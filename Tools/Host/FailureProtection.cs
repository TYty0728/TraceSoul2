using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using SQLite;
using TraceSoul2.Data;
using TraceSoul2.Manager;
using TraceSoul2.Logic;

namespace TraceSoul2.Host
{
    // Host 与复盘子进程共享；暂停不会因新建客户端或重启而消失。
    public sealed class FailureProtection
    {
        private readonly string path;
        public FailureProtection(string directory)
        {
            Directory.CreateDirectory(directory);
            path = Path.Combine(directory, "failure-protection.sqlite3");
            using var db = Open();
            db.CreateTable<FailureStop>();
            // 旧版把对话/供应商错误当作停聊开关；升级后保留报告，但不再阻塞聊天。
            db.Execute("UPDATE failure_stops SET NonBlocking=1 WHERE Key='dialogue' OR Key LIKE 'provider:%'");
        }

        private SQLiteConnection Open()
        {
            var db = new SQLiteConnection(path);
            db.BusyTimeout = TimeSpan.FromSeconds(5);
            return db;
        }

        public List<FailureStop> List()
        {
            using var db = Open();
            return db.Table<FailureStop>().OrderBy(x => x.CreatedUnixMs).ToList();
        }

        public bool IsPaused(string key)
        {
            using var db = Open();
            var fault = db.Find<FailureStop>(key);
            return fault != null && !fault.NonBlocking;
        }

        public void ThrowIfPaused(string key)
        {
            if (IsPaused(key)) throw new FailurePausedException();
        }

        public void ObserveTurnFailures(ChatTurnResultData result)
        {
            foreach (var failure in result?.ContributionResults ?? Array.Empty<TraceCapabilityResultData>())
            {
                if (failure == null || failure.Status != "failed") continue;
                if (failure.CapabilityId == "qq.sticker.send")
                    Report("warning:qq.sticker.send", "表情包", "本次表情未发送成功，已跳过。", "warning");
                else
                    Report("dialogue", "对话或附加能力", new InvalidOperationException("能力执行失败"));
            }
        }

        public void Report(string key, string label, Exception error)
            => Report(key, label, SafeReason(error), "error");

        private void Report(string key, string label, string reason, string severity)
        {
            using var db = Open();
            // 同一未处理报告只通知一次；后续新消息照常处理，失败轮不自动重放。
            db.Execute("INSERT OR IGNORE INTO failure_stops (Key,Label,Reason,CreatedUnixMs,NotificationState,NonBlocking,Severity) VALUES (?,?,?,?,0,1,?)",
                key, label, reason, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), severity);
        }

        public void Pause(string key, string label, Exception error)
        {
            if (error is FailurePausedException) return;
            using var db = Open();
            // INSERT OR IGNORE 保留第一次故障和通知状态，避免轮询重复通知。
            db.Execute("INSERT OR IGNORE INTO failure_stops (Key,Label,Reason,CreatedUnixMs,NotificationState) VALUES (?,?,?,?,0)",
                key, label, SafeReason(error), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }

        public void Resume(string key)
        {
            using var db = Open();
            db.Execute("DELETE FROM failure_stops WHERE Key=? AND NotificationState>=0", key);
        }

        public void CompleteAttempt(string key)
        {
            using var db = Open();
            db.Execute("DELETE FROM failure_stops WHERE Key=? AND NotificationState=-1", key);
        }

        public void BeginAttempt(string key, string label)
        {
            using var db = Open();
            if (db.Execute("INSERT OR IGNORE INTO failure_stops (Key,Label,Reason,CreatedUnixMs,NotificationState) VALUES (?,?,?,?, -1)",
                    key, label, "正在执行；异常退出后需手动恢复", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) == 0)
                throw new FailurePausedException();
        }

        public void FailAttempt(string key, Exception error)
        {
            using var db = Open();
            db.Execute("UPDATE failure_stops SET Reason=?, NotificationState=0 WHERE Key=? AND NotificationState=-1",
                SafeReason(error), key);
        }

        public void RecoverInterruptedAttempts()
        {
            using var db = Open();
            db.Execute("UPDATE failure_stops SET Reason=?, NotificationState=0 WHERE NotificationState=-1",
                "上次任务未正常结束，已停止自动重跑");
        }

        // 发送前持久化占位：即使 QQ 已收到但回执丢失，也不自动重发。
        public bool ClaimNotification(string key, long createdUnixMs)
        {
            using var db = Open();
            return db.Execute("UPDATE failure_stops SET NotificationState=1 WHERE Key=? AND CreatedUnixMs=? AND NotificationState=0",
                key, createdUnixMs) == 1;
        }

        public static string SafeReason(Exception error)
        {
            var message = error?.Message ?? "";
            var status = Regex.Match(message, @"(?:API|HTTP)\s*([45]\d\d)", RegexOptions.IgnoreCase);
            if (status.Success)
                return status.Groups[1].Value switch
                {
                    "402" => "模型账户余额不足（HTTP 402）",
                    "401" or "403" => "模型鉴权或权限失败（HTTP " + status.Groups[1].Value + "）",
                    "429" => "模型额度或频率受限（HTTP 429）",
                    _ => "模型服务请求失败（HTTP " + status.Groups[1].Value + "）"
                };
            if (error is TimeoutException || error is OperationCanceledException)
                return "请求超时或任务被中断";
            if (error is System.Net.Http.HttpRequestException) return "模型服务网络连接失败";
            if (message.Contains("API Key", StringComparison.OrdinalIgnoreCase)) return "模型 API Key 未配置或不可用";
            if (message.Contains("JSON", StringComparison.OrdinalIgnoreCase) || message.Contains("结构化输出", StringComparison.Ordinal) || message.Contains("缺少", StringComparison.Ordinal))
                return "模型输出格式或必填字段校验失败";
            // 不把上游正文、Key、URL、提示词或私聊内容转发给 QQ。
            return "任务执行或模型输出校验失败，详情请查看后台日志";
        }
    }

    [Table("failure_stops")]
    public sealed class FailureStop
    {
        [PrimaryKey] public string Key { get; set; }
        public string Label { get; set; }
        public string Reason { get; set; }
        public long CreatedUnixMs { get; set; }
        public int NotificationState { get; set; }
        public bool NonBlocking { get; set; }
        public string Severity { get; set; }
    }

    public sealed class FailurePausedException : InvalidOperationException
    {
        public FailurePausedException() : base("错误保护已暂停此任务；修复配置后，请到 WebUI「记忆 → 错误保护」手动恢复。") { }
    }

    public static class FailureNotifications
    {
        public static async Task SendPendingAsync(FailureProtection protection, bool connected, string sessionJson,
            Func<string, Dictionary<string, object>, CancellationToken, Task<string>> send, Action<string> log,
            CancellationToken token)
        {
            if (!connected || string.IsNullOrWhiteSpace(sessionJson)) return;
            var pending = protection.List().Where(x => x.NotificationState == 0).ToList();
            if (pending.Count == 0) return;
            string type;
            long id;
            try
            {
                using var session = JsonDocument.Parse(sessionJson);
                type = session.RootElement.GetProperty("session_type").GetString();
                if (!long.TryParse(session.RootElement.GetProperty("session_id").GetString(), out id) || id <= 0) return;
                if (type != "private" && type != "group") return;
            }
            catch { return; }
            token.ThrowIfCancellationRequested();
            var claimed = pending.Where(x => protection.ClaimNotification(x.Key, x.CreatedUnixMs)).ToList();
            if (claimed.Count == 0) return;
            var warningOnly = claimed.All(x => x.Severity == "warning");
            var text = (warningOnly ? "【系统 WARNING】\n" : "【系统 ERROR】\n") +
                string.Join("\n", claimed.Select(x =>
                    (x.Severity == "warning" ? "WARNING" : "ERROR") + " · " + x.Label + "：" + x.Reason +
                    (x.NonBlocking ? "" : "（此任务已暂停）"))) +
                "\n对话不会因此暂停，失败对话不会自动重发。";
            if (claimed.Any(x => !x.NonBlocking))
                text += "\n已暂停的后台任务需处理后到 WebUI「记忆 → 错误保护」手动恢复。";
            try
            {
                await send(type == "group" ? "send_group_msg" : "send_private_msg",
                    new Dictionary<string, object>
                    {
                        [type == "group" ? "group_id" : "user_id"] = id,
                        ["message"] = new[] { new { type = "text", data = new { text } } }
                    }, token).WaitAsync(TimeSpan.FromSeconds(15), token);
                log("错误报告：已发送 QQ 系统通知。");
            }
            catch (Exception) { log("错误报告：QQ 通知未确认送达，不自动重发；报告可在 WebUI 查看。"); }
        }
    }

    public sealed class ProtectedLlmClient : ILlmClient, ILlmEndpoint, ILlmUsageReporter
    {
        private readonly ILlmClient inner;
        private readonly FailureProtection protection;
        private string Key => "provider:" + inner.ProviderId;
        public ProtectedLlmClient(ILlmClient inner, FailureProtection protection)
        { this.inner = inner; this.protection = protection; }
        public string ProviderId => inner.ProviderId;
        public string Model => inner.Model;
        public string BaseUrl => (inner as ILlmEndpoint)?.BaseUrl;
        public LlmUsageData LastUsage => (inner as ILlmUsageReporter)?.LastUsage;
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken token = default)
            => inner.ListModelsAsync(token);
        public Task<string> CompleteJsonAsync(List<DeepSeekMessageData> messages, CancellationToken token = default, string promptCacheKey = null)
            => RunAsync(() => inner.CompleteJsonAsync(messages, token, promptCacheKey), token);
        public Task<string> CompleteTextAsync(List<DeepSeekMessageData> messages, CancellationToken token = default, string promptCacheKey = null)
            => RunAsync(() => inner.CompleteTextAsync(messages, token, promptCacheKey), token);
        private async Task<string> RunAsync(Func<Task<string>> call, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            try { return await call(); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                protection.Report(Key, "语言模型通道", error);
                throw;
            }
        }
    }

    public static class ProtectedParallelWork
    {
        public static async Task RunAsync<T>(IEnumerable<T> items, int concurrency,
            Func<T, CancellationToken, Task> run, Action<Exception> failed)
        {
            using var cancellation = new CancellationTokenSource();
            using var gate = new SemaphoreSlim(concurrency);
            Exception firstError = null;
            var tasks = items.Select(async item =>
            {
                await gate.WaitAsync(cancellation.Token);
                try
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    await run(item, cancellation.Token);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    if (Interlocked.CompareExchange(ref firstError, error, null) == null)
                    {
                        cancellation.Cancel();
                        failed(error);
                    }
                    throw;
                }
                finally { gate.Release(); }
            }).ToArray();
            try { await Task.WhenAll(tasks); }
            catch when (firstError != null) { ExceptionDispatchInfo.Capture(firstError).Throw(); }
        }
    }
}
