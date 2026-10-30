using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Manager;
using TraceSoul2.Logic;

namespace TraceSoul2.Host
{
    // One instance per review attempt. Successful intermediate requests do not
    // replenish the retry allowance; exhaustion reaches daily failure protection.
    public sealed class ReviewTransportClient : ILlmClient, ILlmEndpoint, ILlmUsageReporter
    {
        private readonly ILlmClient inner;
        private readonly Func<TimeSpan> elapsed;
        private readonly Func<TimeSpan, CancellationToken, Task> delay;
        private readonly Action<string> log;
        private readonly object retryGate = new();
        private TimeSpan? firstFailure;
        private int retries;
        private bool exhausted;
        private static readonly TimeSpan RetryWindow = TimeSpan.FromMinutes(30);
        public ReviewTransportClient(ILlmClient inner)
            : this(inner, Clock(), Task.Delay, Console.WriteLine) { }
        public ReviewTransportClient(ILlmClient inner, Func<TimeSpan> elapsed,
            Func<TimeSpan, CancellationToken, Task> delay, Action<string> log)
        { this.inner = inner; this.elapsed = elapsed; this.delay = delay; this.log = log; }
        private static Func<TimeSpan> Clock()
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            return () => watch.Elapsed;
        }
        public string ProviderId => inner.ProviderId;
        public string Model => inner.Model;
        public string BaseUrl => (inner as ILlmEndpoint)?.BaseUrl;
        public LlmUsageData LastUsage => (inner as ILlmUsageReporter)?.LastUsage;
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken token = default) => inner.ListModelsAsync(token);
        public Task<string> CompleteJsonAsync(List<DeepSeekMessageData> messages, CancellationToken token = default, string promptCacheKey = null)
            => RunAsync(t => inner.CompleteJsonAsync(messages, t, promptCacheKey), token);
        public Task<string> CompleteTextAsync(List<DeepSeekMessageData> messages, CancellationToken token = default, string promptCacheKey = null)
            => RunAsync(t => inner.CompleteTextAsync(messages, t, promptCacheKey), token);

        public static bool IsTransportFailure(Exception error)
        {
            if (error is LlmContentRejectedException) return false;
            if (error is TimeoutException) return true;
            if (error is HttpRequestException http)
                return !http.StatusCode.HasValue || RetryableStatus((int)http.StatusCode.Value);
            // Provider clients represent non-success HTTP responses as exceptions.
            // Do not retry broken credentials, local installation errors or JSON/field errors.
            var message = error?.Message ?? "";
            if (Regex.IsMatch(message, @"codex_(failed_check_login_or_quota|executable_not_found|launch_failed|invalid_json|empty_output|completion_unconfirmed)|bridge_internal_error|API Key", RegexOptions.IgnoreCase))
                return false;
            var status = Regex.Match(message, @"(?:API|HTTP)\s*(\d{3})\b", RegexOptions.IgnoreCase);
            return error is InvalidOperationException && status.Success && RetryableStatus(int.Parse(status.Groups[1].Value));
        }
        private static bool RetryableStatus(int status) => status == 408 || status == 429 ||
            status == 500 || status == 502 || status == 503 || status == 504;
        private InvalidOperationException Stop(Exception cause = null)
        {
            exhausted = true;
            return new InvalidOperationException("复盘网络保护已停止请求：整轮最多补试2次，首次故障后30分钟内允许补试；检查点保留，请检查连接后手动恢复。", cause);
        }
        private async Task<string> RunAsync(Func<CancellationToken, Task<string>> request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            lock (retryGate) { if (exhausted) throw Stop(); }
            while (true)
            {
                try { return await request(token); }
                catch (Exception error) when (!token.IsCancellationRequested && IsTransportFailure(error))
                {
                    TimeSpan wait;
                    int retryNumber;
                    lock (retryGate)
                    {
                        firstFailure ??= elapsed();
                        wait = TimeSpan.FromMinutes(retries == 0 ? 5 : 10);
                        if (exhausted || retries >= 2 || elapsed() - firstFailure.Value + wait >= RetryWindow) throw Stop(error);
                        retryNumber = ++retries;
                    }
                    log?.Invoke("复盘网络请求未完成，冷却" + wait.TotalMinutes + "分钟后补试（整轮" + retryNumber + "/2）；已完成步骤保留。");
                    await delay(wait, token);
                    token.ThrowIfCancellationRequested();
                    // Machine sleep or a slow scheduler must not reopen an expired window.
                    lock (retryGate)
                    {
                        if (exhausted || elapsed() - firstFailure.Value >= RetryWindow) throw Stop(error);
                    }
                    // Never cancel an in-flight response merely because the retry window ends.
                }
            }
        }
    }
}
