using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Logic;
using TraceSoul2.Manager;

namespace TraceSoul2.Host
{
    /// <summary>只包装时序日志，不记录 prompt、模型原文或密钥。</summary>
    internal sealed class TimedLlmClient : ILlmClient, ILlmEndpoint
    {
        private readonly ILlmClient inner;
        private readonly string traceId;
        private readonly Action<string> log;
        private int requestNumber;

        public string ProviderId { get { return inner.ProviderId; } }
        public string Model { get { return inner.Model; } }
        public string BaseUrl
        {
            get
            {
                var endpoint = inner as ILlmEndpoint;
                return endpoint == null ? string.Empty : endpoint.BaseUrl;
            }
        }

        public TimedLlmClient(ILlmClient inner, string traceId, Action<string> log)
        {
            this.inner = inner ?? throw new ArgumentNullException("inner");
            this.traceId = traceId ?? string.Empty;
            this.log = log;
        }

        public Task<string> CompleteJsonAsync(
            List<DeepSeekMessageData> messages,
            CancellationToken cancellationToken = default(CancellationToken),
            string promptCacheKey = null)
        {
            return CompleteAsync(messages, true, cancellationToken, promptCacheKey);
        }

        public Task<string> CompleteTextAsync(
            List<DeepSeekMessageData> messages,
            CancellationToken cancellationToken = default(CancellationToken),
            string promptCacheKey = null)
        {
            return CompleteAsync(messages, false, cancellationToken, promptCacheKey);
        }

        private async Task<string> CompleteAsync(
            List<DeepSeekMessageData> messages,
            bool json,
            CancellationToken cancellationToken,
            string promptCacheKey)
        {
            var number = Interlocked.Increment(ref requestNumber);
            var timer = Stopwatch.StartNew();
            Write("LLM#" + number + " 请求开始｜" + ProviderId + "/" + Model +
                  (json ? string.Empty : "｜text") +
                  "｜messages=" + (messages == null ? 0 : messages.Count));
            try
            {
                var result = json
                    ? await inner.CompleteJsonAsync(messages, cancellationToken, promptCacheKey)
                    : await inner.CompleteTextAsync(messages, cancellationToken, promptCacheKey);
                var usage = FormatUsage();
                Write("LLM#" + number + " 请求完成｜耗时 " + timer.ElapsedMilliseconds +
                      " ms｜chars=" + (result == null ? 0 : result.Length) +
                      (string.IsNullOrWhiteSpace(usage) ? string.Empty : "｜" + usage));
                return result;
            }
            catch (Exception exception)
            {
                Write("LLM#" + number + " 请求失败｜耗时 " + timer.ElapsedMilliseconds +
                      " ms｜" + exception.GetType().Name + ": " + exception.Message);
                throw;
            }
        }

        public Task<IReadOnlyList<string>> ListModelsAsync(
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return inner.ListModelsAsync(cancellationToken);
        }

        private string FormatUsage()
        {
            var reporter = inner as ILlmUsageReporter;
            return reporter == null ? string.Empty : LlmUsageLogic.FormatLog(reporter.LastUsage);
        }

        private void Write(string message)
        {
            log?.Invoke("[链路 " + (string.IsNullOrWhiteSpace(traceId) ? "--------" : traceId) + "] " + message);
        }
    }
}
