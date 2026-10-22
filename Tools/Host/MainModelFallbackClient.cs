using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
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
                catch (Exception error) when (CanFallback(error))
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

        internal static bool CanFallback(Exception error)
        {
            if (error is TimeoutException || error is HttpRequestException || error is JsonException) return true;
            if (error is OperationCanceledException) return false;
            var message = error.Message ?? "";
            return DeepSeekClientManager.IsRetryableProviderException(error) ||
                Regex.IsMatch(message, @"(?:API|HTTP)\s*(401|402|403|404|408|425|429|5\d\d)\b", RegexOptions.IgnoreCase) ||
                message.Contains("空 content", StringComparison.Ordinal) ||
                message.Contains("无法解析的响应", StringComparison.Ordinal);
        }
    }

    public sealed class MainModelFallbackException : InvalidOperationException
    {
        public MainModelFallbackException(string summary) : base("主模型及已配置备用通道均失败：" + summary) { }
    }
}
