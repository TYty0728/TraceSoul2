using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Logic;
using TraceSoul2.Manager;
using TraceSoul2.Util;

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
            Write("模型请求 #" + number + " 开始", detail: "模型：" + ProviderId + "/" + Model +
                  "｜格式：" + (json ? "结构化" : "文本") + "｜messages=" + (messages?.Count ?? 0));
            try
            {
                var result = json
                    ? await inner.CompleteJsonAsync(messages, cancellationToken, promptCacheKey)
                    : await inner.CompleteTextAsync(messages, cancellationToken, promptCacheKey);
                var usage = FormatUsage();
                Write("模型请求 #" + number + " 完成", timer.ElapsedMilliseconds,
                    "模型：" + ProviderId + "/" + Model + "｜返回字符：" + (result?.Length ?? 0) +
                    (string.IsNullOrWhiteSpace(usage) ? string.Empty : "｜Token用量：" + usage));
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Write("模型请求 #" + number + " 已取消", timer.ElapsedMilliseconds, "模型：" + ProviderId + "/" + Model);
                throw;
            }
            catch (Exception exception)
            {
                Write("模型请求 #" + number + " 失败", timer.ElapsedMilliseconds,
                    "模型：" + ProviderId + "/" + Model + "｜原因：" + exception.GetType().Name + "：" + exception.Message, "错误");
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

        private void Write(string stage, long? elapsedMs = null, string detail = null, string level = null)
        {
            log?.Invoke(TraceLogFormat.Format(traceId, stage, elapsedMs, detail, level));
        }
    }
}
