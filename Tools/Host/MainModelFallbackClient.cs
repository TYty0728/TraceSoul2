using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Logic;
using TraceSoul2.Manager;

namespace TraceSoul2.Host
{
    // A client belongs to one turn. Only inference is retried; the kernel and its effects are never replayed.
    public sealed class MainModelFallbackClient : ILlmClient, ILlmEndpoint, ILlmUsageReporter
    {
        private readonly IReadOnlyList<ILlmClient> clients;
        private readonly Action<string> log;
        private readonly List<string> failures = new List<string>();
        private int current;
        private ILlmClient Active => clients[Math.Min(current, clients.Count - 1)];
        public string ProviderId => Active.ProviderId;
        public string Model => Active.Model;
        public string BaseUrl => (Active as ILlmEndpoint)?.BaseUrl;
        public LlmUsageData LastUsage => current < clients.Count ? (Active as ILlmUsageReporter)?.LastUsage : null;

        public MainModelFallbackClient(IReadOnlyList<ILlmClient> clients, Action<string> log = null)
        {
            if (clients == null || clients.Count == 0 || clients.Count > 3 || clients.Any(x => x == null))
                throw new ArgumentException("主模型路由需包含 1～3 个有效通道。", nameof(clients));
            this.clients = clients;
            this.log = log;
        }

        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken token = default)
            => Active.ListModelsAsync(token);
        public Task<string> CompleteJsonAsync(List<DeepSeekMessageData> messages, CancellationToken token = default, string promptCacheKey = null)
            => CompleteAsync(messages, true, token, promptCacheKey);
        public Task<string> CompleteTextAsync(List<DeepSeekMessageData> messages, CancellationToken token = default, string promptCacheKey = null)
            => CompleteAsync(messages, false, token, promptCacheKey);

        private async Task<string> CompleteAsync(List<DeepSeekMessageData> messages, bool json, CancellationToken token, string cacheKey)
        {
            token.ThrowIfCancellationRequested();
            while (current < clients.Count)
            {
                var client = Active;
                try
                {
                    var result = json
                        ? await client.CompleteJsonAsync(messages, token, cacheKey)
                        : await client.CompleteTextAsync(messages, token, cacheKey);
                    token.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(result)) throw new InvalidOperationException("语言模型返回空 content。");
                    if (current > 0) log?.Invoke("主模型备用通道完成：" + client.ProviderId + " / " + client.Model);
                    return result;
                }
                catch (Exception) when (token.IsCancellationRequested) { token.ThrowIfCancellationRequested(); throw; }
                // Any failed inference advances the route; only caller cancellation stops it.
                catch (Exception error)
                {
                    failures.Add(client.ProviderId + "：" + FailureProtection.SafeReason(error));
                    current++;
                    if (current < clients.Count)
                        log?.Invoke("主模型切换：" + client.ProviderId + " → " + Active.ProviderId + " / " + Active.Model +
                            "｜" + FailureProtection.SafeReason(error));
                }
            }
            throw new MainModelFallbackException(string.Join("；", failures));
        }

    }

    public sealed class MainModelFallbackException : InvalidOperationException
    {
        public MainModelFallbackException(string summary) : base("主模型及已配置备用通道均失败：" + summary) { }
    }
}
