using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Manager;

namespace TraceSoul2.Host
{
    /// <summary>只包装时序日志，不记录 prompt、模型原文或密钥。</summary>
    internal sealed class TimedLlmClient : ILlmClient
    {
        private readonly ILlmClient inner;
        private readonly string traceId;
        private readonly Action<string> log;
        private int requestNumber;

        public string ProviderId { get { return inner.ProviderId; } }
        public string Model { get { return inner.Model; } }

        public TimedLlmClient(ILlmClient inner, string traceId, Action<string> log)
        {
            this.inner = inner ?? throw new ArgumentNullException("inner");
            this.traceId = traceId ?? string.Empty;
            this.log = log;
        }

        public Task<string> CompleteJsonAsync(
            List<DeepSeekMessageData> messages,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return CompleteAsync(messages, true, cancellationToken);
        }

        public Task<string> CompleteTextAsync(
            List<DeepSeekMessageData> messages,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return CompleteAsync(messages, false, cancellationToken);
        }

        private async Task<string> CompleteAsync(
            List<DeepSeekMessageData> messages,
            bool json,
            CancellationToken cancellationToken)
        {
            var number = Interlocked.Increment(ref requestNumber);
            var timer = Stopwatch.StartNew();
            Write("LLM#" + number + " 请求开始｜" + ProviderId + "/" + Model +
                  (json ? string.Empty : "｜text") +
                  "｜messages=" + (messages == null ? 0 : messages.Count));
            try
            {
                var result = json
                    ? await inner.CompleteJsonAsync(messages, cancellationToken)
                    : await inner.CompleteTextAsync(messages, cancellationToken);
                Write("LLM#" + number + " 请求完成｜耗时 " + timer.ElapsedMilliseconds +
                      " ms｜chars=" + (result == null ? 0 : result.Length));
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

        private void Write(string message)
        {
            log?.Invoke("[链路 " + (string.IsNullOrWhiteSpace(traceId) ? "--------" : traceId) + "] " + message);
        }
    }
}
