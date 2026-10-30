using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Logic;
using TraceSoul2.Manager;

namespace TraceSoul2.Migrate
{
    public sealed class ReviewBudgetYieldException : InvalidOperationException
    {
        public ReviewBudgetYieldException() : base("本轮派发预算已用完，已发请求完成保存后继续未完成步骤。") { }
    }

    internal sealed class ReviewBudgetClient : ILlmClient, ILlmEndpoint
    {
        private readonly ILlmClient inner;
        private readonly int callLimit;
        private int calls;
        private readonly Action<int, string> report;
        public ReviewBudgetClient(ILlmClient inner, Action<int, string> report)
            : this(inner, report, 256) { }
        public ReviewBudgetClient(ILlmClient inner, Action<int, string> report, int callLimit)
        { this.inner = inner; this.report = report; this.callLimit = callLimit; }
        public string ProviderId => inner.ProviderId;
        public string Model => inner.Model;
        public string BaseUrl => (inner as ILlmEndpoint)?.BaseUrl;
        private void Reserve(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref calls) > callLimit)
                throw new ReviewBudgetYieldException();
        }
        public Task<string> CompleteJsonAsync(List<DeepSeekMessageData> messages, CancellationToken cancellationToken = default, string promptCacheKey = null)
            => CompleteBoundedAsync(messages, t => inner.CompleteJsonAsync(messages, t, promptCacheKey), cancellationToken);
        private async Task<string> CompleteBoundedAsync(List<DeepSeekMessageData> messages,
            Func<CancellationToken, Task<string>> request, CancellationToken cancellationToken)
        {
            Reserve(cancellationToken);
            var title = System.Text.RegularExpressions.Regex.Match(messages.Count > 0 ? messages[0].content ?? "" : "", @"【([^】]{1,40})】");
            if (title.Success) report?.Invoke(calls, title.Groups[1].Value);
            // Bound request count, not total wall time. Slow successful requests keep
            // their results; transport has its own deadline and bounded retry policy.
            return await request(cancellationToken);
        }
        public Task<string> CompleteTextAsync(List<DeepSeekMessageData> messages, CancellationToken cancellationToken = default, string promptCacheKey = null)
            => CompleteBoundedAsync(messages, t => inner.CompleteTextAsync(messages, t, promptCacheKey), cancellationToken);
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) => inner.ListModelsAsync(cancellationToken);
    }
}
