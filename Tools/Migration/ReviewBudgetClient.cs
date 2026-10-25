using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Logic;
using TraceSoul2.Manager;

namespace TraceSoul2.Migrate
{
    internal sealed class ReviewBudgetClient : ILlmClient, ILlmEndpoint
    {
        private readonly ILlmClient inner;
        private readonly System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
        private static readonly TimeSpan Duration = TimeSpan.FromMinutes(20);
        private int calls;
        private readonly Action<int, string> report;
        public ReviewBudgetClient(ILlmClient inner, Action<int, string> report) { this.inner = inner; this.report = report; }
        public string ProviderId => inner.ProviderId;
        public string Model => inner.Model;
        public string BaseUrl => (inner as ILlmEndpoint)?.BaseUrl;
        private void Reserve(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (elapsed.Elapsed >= Duration || Interlocked.Increment(ref calls) > 256)
                throw new ReviewProsePendingException("本轮已达20分钟或256次逻辑请求预算，保存进度等待补做。");
        }
        public Task<string> CompleteJsonAsync(List<DeepSeekMessageData> messages, CancellationToken cancellationToken = default, string promptCacheKey = null)
            => CompleteBoundedAsync(messages, t => inner.CompleteJsonAsync(messages, t, promptCacheKey), cancellationToken);
        private async Task<string> CompleteBoundedAsync(List<DeepSeekMessageData> messages,
            Func<CancellationToken, Task<string>> request, CancellationToken cancellationToken)
        {
            Reserve(cancellationToken);
            var title = System.Text.RegularExpressions.Regex.Match(messages.Count > 0 ? messages[0].content ?? "" : "", @"【([^】]{1,40})】");
            if (title.Success) report?.Invoke(calls, title.Groups[1].Value);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var remaining = Duration - elapsed.Elapsed;
            timeout.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1));
            try { return await request(timeout.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
            { throw new ReviewProsePendingException("本轮已达20分钟请求预算，已保存检查点等待补做。"); }
        }
        public Task<string> CompleteTextAsync(List<DeepSeekMessageData> messages, CancellationToken cancellationToken = default, string promptCacheKey = null)
            => CompleteBoundedAsync(messages, t => inner.CompleteTextAsync(messages, t, promptCacheKey), cancellationToken);
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) => inner.ListModelsAsync(cancellationToken);
    }
}
