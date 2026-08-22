using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Logic;
using TraceSoul2.Prompts;
using TraceSoul2.Util;

namespace TraceSoul2.Manager
{
    /// <summary>
    /// OpenAI 兼容 ChatCompletions 客户端。DeepSeek 是默认口，不是唯一口。
    /// API Key 只进入 Authorization header，不写日志，也不写数据库。
    /// </summary>
    public sealed class DeepSeekClientManager : ILlmClient
    {
        private static readonly HttpClient Http = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        private readonly DeepSeekConfigData config;

        /// <summary>按需 LLM 原文导出：设置 TRACESOUL2_LLM_DUMP_DIR 后，每次调用把请求/响应原文落盘（不含 API Key）。</summary>
        private static readonly string LlmDumpDir =
            Environment.GetEnvironmentVariable("TRACESOUL2_LLM_DUMP_DIR");
        private static int llmDumpCounter;

        public string ProviderId
        {
            get { return string.IsNullOrWhiteSpace(config.ProviderId) ? "default" : config.ProviderId; }
        }

        public string Model { get { return config.Model; } }

        public DeepSeekClientManager(DeepSeekConfigData config)
        {
            this.config = config ?? throw new ArgumentNullException("config");
            NormalizeConfig(this.config);
        }

        public async Task<IReadOnlyList<string>> ListModelsAsync(
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(config.ApiKey))
                throw new InvalidOperationException("语言模型 API Key 尚未填写。");
            var endpoint = config.BaseUrl.TrimEnd('/') + "/models";
            using (var request = new HttpRequestMessage(HttpMethod.Get, endpoint))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
                using (var response = await SendWithTimeoutAsync(request, cancellationToken))
                {
                    var body = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                        throw new InvalidOperationException(
                            "获取模型列表失败 HTTP " + (int)response.StatusCode + "。");
            var parsed = TraceJson.FromJson<OpenAiModelListData>(body);
                    return ParseModelList(parsed);
                }
            }
        }

        public static IReadOnlyList<string> ParseModelList(OpenAiModelListData parsed)
        {
            var ids = new List<string>();
            if (parsed == null || parsed.data == null) return ids;
            foreach (var item in parsed.data)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.id)) continue;
                var id = item.id.Trim();
                if (!ids.Contains(id)) ids.Add(id);
            }
            ids.Sort(StringComparer.Ordinal);
            return ids;
        }

        public Task<string> CompleteJsonAsync(
            List<DeepSeekMessageData> messages,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return CompleteCoreAsync(messages, true, cancellationToken);
        }

        public Task<string> CompleteTextAsync(
            List<DeepSeekMessageData> messages,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return CompleteCoreAsync(messages, false, cancellationToken);
        }

        private async Task<string> CompleteCoreAsync(
            List<DeepSeekMessageData> messages,
            bool json,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(config.ApiKey))
                throw new InvalidOperationException("语言模型 API Key 尚未填写。");

            // 官方文档提示 JSON Output 偶尔可能返回空 content。第二次请求追加纠偏指令，
            // 避免把完全相同的请求盲目重放后再次命中相同结果。
            var diagnostics = new List<string>();
            var attempts = 1 + config.EmptyContentRetries;
            var current = messages;
            for (var attempt = 0; attempt < attempts; attempt++)
            {
                var result = await SendOnceAsync(current, json, cancellationToken);
                var incomplete = json && DeepSeekStructuredOutputLogic.LooksIncompleteJson(result.Content);
                if (!incomplete && !string.IsNullOrWhiteSpace(result.Content)) return result.Content;
                diagnostics.Add("第" + (attempt + 1) + "次：" + result.Diagnostic);
                if (attempt >= attempts - 1)
                {
                    if (!string.IsNullOrWhiteSpace(result.Content)) return result.Content;
                    break;
                }
                var truncated = incomplete ||
                                DeepSeekStructuredOutputLogic.LooksLikeTruncatedFinish(result.FinishReason);
                current = json
                    ? (truncated ? BuildTruncationRetryMessages(messages) : BuildEmptyContentRetryMessages(messages))
                    : BuildTextRetryMessages(messages, truncated);
            }

            throw new InvalidOperationException(
                "语言模型连续 " + attempts + " 次返回了空 content。" +
                string.Join("；", diagnostics));
        }

        private async Task<CompletionAttempt> SendOnceAsync(
            List<DeepSeekMessageData> messages,
            bool json,
            CancellationToken cancellationToken)
        {
            var temperature = ResolveTemperature();
            try
            {
                return await PostOnceAsync(messages, temperature, json, cancellationToken);
            }
            catch (InvalidOperationException exception)
            {
                if (Math.Abs(temperature - 1f) > 0.001f &&
                    LooksLikeUnitTemperatureOnly(exception.Message))
                    return await PostOnceAsync(messages, 1f, json, cancellationToken);
                throw;
            }
        }

        private async Task<CompletionAttempt> PostOnceAsync(
            List<DeepSeekMessageData> messages,
            float temperature,
            bool json,
            CancellationToken cancellationToken)
        {
            string bodyJson;
            if (UsesDeepSeekExtensions())
            {
                if (json)
                {
                    bodyJson = TraceJson.ToJson(new DeepSeekChatRequestData
                    {
                        model = ResolveModel(),
                        messages = messages,
                        response_format = new DeepSeekResponseFormatData(),
                        thinking = new DeepSeekThinkingData
                        {
                            type = config.ThinkingEnabled ? "enabled" : "disabled"
                        },
                        reasoning_effort = config.ThinkingEnabled ? config.ReasoningEffort : "none",
                        temperature = temperature,
                        top_p = config.TopP,
                        max_tokens = config.MaxTokens
                    });
                }
                else
                {
                    bodyJson = TraceJson.ToJson(new GlmChatRequestData
                    {
                        model = ResolveModel(),
                        messages = messages,
                        thinking = new DeepSeekThinkingData
                        {
                            type = config.ThinkingEnabled ? "enabled" : "disabled"
                        },
                        reasoning_effort = config.ThinkingEnabled ? config.ReasoningEffort : "none",
                        temperature = temperature,
                        top_p = config.TopP,
                        max_tokens = config.MaxTokens
                    });
                }
            }
            else if (UsesGlmExtensions())
            {
                // GLM-5.x 默认开启 Thinking。OpenCode Go 也是 OpenAI-compatible 转发，
                // 必须把该扩展字段真正发出去，不能只在 WebUI 中保存开关。
                bodyJson = TraceJson.ToJson(new GlmChatRequestData
                {
                    model = ResolveModel(),
                    messages = messages,
                    thinking = new DeepSeekThinkingData
                    {
                        type = config.ThinkingEnabled ? "enabled" : "disabled"
                    },
                    reasoning_effort = config.ThinkingEnabled ? config.ReasoningEffort : "none",
                    temperature = temperature,
                    top_p = config.TopP,
                    max_tokens = config.MaxTokens
                });
            }
            else
            {
                bodyJson = TraceJson.ToJson(new OpenAiChatRequestData
                {
                    model = ResolveModel(),
                    messages = messages,
                    temperature = temperature,
                    top_p = config.TopP,
                    max_tokens = config.MaxTokens
                });
            }
            var endpoint = config.BaseUrl.TrimEnd('/') + "/chat/completions";

            using (var request = new HttpRequestMessage(HttpMethod.Post, endpoint))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
                request.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");

                using (var response = await SendWithTimeoutAsync(request, cancellationToken))
                {
                    var body = await response.Content.ReadAsStringAsync();
                    DumpCall(bodyJson, body);
                    DeepSeekChatResponseData parsed = null;
                    try
                    {
                    parsed = TraceJson.FromJson<DeepSeekChatResponseData>(body);
                    }
                    catch (Exception exception)
                    {
                        throw new InvalidOperationException(
                            "语言模型返回了无法解析的响应，HTTP " +
                            (int)response.StatusCode + "，body长度=" + body.Length + "。",
                            exception);
                    }
                    if (!response.IsSuccessStatusCode)
                    {
                        var detail = parsed != null && parsed.error != null
                            ? parsed.error.message
                            : ExtractErrorMessage(body);
                        throw new InvalidOperationException(
                            "语言模型 API " + (int)response.StatusCode + ": " + detail);
                    }

                    if (parsed == null || parsed.choices == null || parsed.choices.Count == 0 ||
                        parsed.choices[0].message == null)
                        throw new InvalidOperationException(
                            "语言模型返回结构不完整，HTTP " + (int)response.StatusCode +
                            "，request_id=" + GetRequestId(response) +
                            "，body长度=" + body.Length + "。");

                    var choice = parsed.choices[0];
                    return new CompletionAttempt(
                        choice.message.content,
                        choice.finish_reason,
                        choice.message.reasoning_content,
                        GetRequestId(response));
                }
            }
        }

        private static void DumpCall(string requestJson, string responseBody)
        {
            var dir = LlmDumpDir;
            if (string.IsNullOrWhiteSpace(dir)) return;
            try
            {
                System.IO.Directory.CreateDirectory(dir);
                var seq = System.Threading.Interlocked.Increment(ref llmDumpCounter);
                var name = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + seq.ToString("D3");
                var enc = new UTF8Encoding(true); // 带 BOM，记事本可直接打开
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(dir, name + "-prompt.txt"),
                    ParseRequestText(requestJson), enc);
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(dir, name + "-response.txt"),
                    ParseResponseText(responseBody), enc);
                var usage = ParseUsageText(responseBody);
                if (!string.IsNullOrWhiteSpace(usage))
                    System.IO.File.WriteAllText(
                        System.IO.Path.Combine(dir, name + "-usage.txt"), usage, enc);
            }
            catch
            {
                /* 导出失败不影响主流程 */
            }
        }

        private static string ParseRequestText(string requestJson)
        {
            try
            {
            var parsed = TraceJson.FromJson<DeepSeekChatRequestData>(requestJson);
                var builder = new StringBuilder();
                builder.Append("model=").AppendLine(parsed == null ? "?" : parsed.model);
                foreach (var m in (parsed == null || parsed.messages == null)
                         ? new List<DeepSeekMessageData>() : parsed.messages)
                {
                    builder.Append("【").Append(m.role ?? "?").Append("】").AppendLine();
                    builder.AppendLine(m.content ?? string.Empty);
                    builder.AppendLine();
                }
                return builder.ToString();
            }
            catch
            {
                return requestJson ?? string.Empty;
            }
        }

        private static string ParseResponseText(string responseBody)
        {
            try
            {
            var parsed = TraceJson.FromJson<DeepSeekChatResponseData>(responseBody);
                if (parsed != null && parsed.choices != null && parsed.choices.Count > 0 &&
                    parsed.choices[0].message != null)
                    return parsed.choices[0].message.content ?? string.Empty;
            }
            catch
            {
                /* 解析失败退回原文 */
            }
            return responseBody ?? string.Empty;
        }

        private static string ParseUsageText(string responseBody)
        {
            try
            {
            var parsed = TraceJson.FromJson<DeepSeekChatResponseData>(responseBody);
                var usage = parsed == null ? null : parsed.usage;
                if (usage == null || usage.total_tokens <= 0) return string.Empty;
                var input = Math.Max(0, usage.prompt_cache_hit_tokens) +
                            Math.Max(0, usage.prompt_cache_miss_tokens);
                var rate = input == 0 ? 0d : usage.prompt_cache_hit_tokens * 100d / input;
                var builder = new StringBuilder();
                builder.Append("prompt_tokens=").AppendLine(usage.prompt_tokens.ToString());
                builder.Append("prompt_cache_hit_tokens=").AppendLine(usage.prompt_cache_hit_tokens.ToString());
                builder.Append("prompt_cache_miss_tokens=").AppendLine(usage.prompt_cache_miss_tokens.ToString());
                builder.Append("prompt_cache_hit_rate=").Append(rate.ToString("0.0")).AppendLine("%");
                builder.Append("completion_tokens=").AppendLine(usage.completion_tokens.ToString());
                builder.Append("total_tokens=").AppendLine(usage.total_tokens.ToString());
                return builder.ToString();
            }
            catch
            {
                return string.Empty;
            }
        }

        private static List<DeepSeekMessageData> BuildTruncationRetryMessages(
            IEnumerable<DeepSeekMessageData> source)
        {
            var result = new List<DeepSeekMessageData>(source ?? new DeepSeekMessageData[0]);
            result.Add(new DeepSeekMessageData(
                "user",
                CorePrompts.Retry.JsonTruncated));
            return result;
        }

        private static List<DeepSeekMessageData> BuildEmptyContentRetryMessages(
            IEnumerable<DeepSeekMessageData> source)
        {
            var result = new List<DeepSeekMessageData>(source ?? new DeepSeekMessageData[0]);
            result.Add(new DeepSeekMessageData(
                "user",
                CorePrompts.Retry.JsonEmpty));
            return result;
        }

        private static List<DeepSeekMessageData> BuildTextRetryMessages(
            IEnumerable<DeepSeekMessageData> source,
            bool truncated)
        {
            var result = new List<DeepSeekMessageData>(source ?? new DeepSeekMessageData[0]);
            result.Add(new DeepSeekMessageData(
                "user",
                truncated
                    ? CorePrompts.Retry.TextTruncated
                    : CorePrompts.Retry.TextEmpty));
            return result;
        }

        /// <summary>
        /// AstrBot 两层结构会把「供应商/模型」写进同一个 model 字段，例如 openai/按次官逆B-gemini-...。
        /// 中转站通道名通常没有供应商前缀；请求时剥掉与当前供应商 ID 相同的前缀。
        /// </summary>
        private string ResolveModel()
        {
            var model = (config.Model ?? string.Empty).Trim();
            var prefix = (config.ProviderId ?? string.Empty).Trim() + "/";
            if (prefix.Length > 1 &&
                model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                model = model.Substring(prefix.Length);
            if (IsOpenCodeZen())
                model = NormalizeOpenCodeModel(model);
            return model;
        }

        private bool IsOpenCodeZen()
        {
            var url = config.BaseUrl ?? string.Empty;
            return url.IndexOf("opencode.ai/zen", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   string.Equals(config.ProviderId, "opencode-go", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// OpenCode 界面是「GLM-5.2」「Kimi K3(2x usage)」，Chat Completions 只要 glm-5.2 / kimi-k3。
        /// </summary>
        private static string NormalizeOpenCodeModel(string model)
        {
            model = (model ?? string.Empty).Trim();
            var paren = model.IndexOf('(');
            if (paren >= 0) model = model.Substring(0, paren).Trim();
            model = model.ToLowerInvariant();
            while (model.IndexOf("  ", StringComparison.Ordinal) >= 0)
                model = model.Replace("  ", " ");
            return model.Replace(' ', '-');
        }

        private float ResolveTemperature()
        {
            if (RequiresUnitTemperature(ResolveModel())) return 1f;
            return config.Temperature;
        }

        private static bool RequiresUnitTemperature(string model)
        {
            model = (model ?? string.Empty).Trim().ToLowerInvariant();
            return model == "kimi-k3" ||
                   model.StartsWith("kimi-k3", StringComparison.Ordinal) ||
                   model.IndexOf("kimi k3", StringComparison.Ordinal) >= 0;
        }

        private static bool LooksLikeUnitTemperatureOnly(string message)
        {
            return !string.IsNullOrEmpty(message) &&
                   message.IndexOf("only 1 is allowed", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string ExtractErrorMessage(string body)
        {
            body = body ?? string.Empty;
            const string key = "\"message\":\"";
            var start = body.IndexOf(key, StringComparison.Ordinal);
            if (start < 0) return body.Length <= 800 ? body : body.Substring(0, 800);
            start += key.Length;
            var end = body.IndexOf('"', start);
            if (end <= start) return body.Length <= 800 ? body : body.Substring(0, 800);
            return body.Substring(start, end - start);
        }

        private bool UsesDeepSeekExtensions()
        {
            var type = (config.Type ?? string.Empty).Trim().ToLowerInvariant();
            if (type.IndexOf("deepseek", StringComparison.Ordinal) >= 0) return true;
            var url = (config.BaseUrl ?? string.Empty).Trim();
            return url.IndexOf("deepseek.com", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private bool UsesGlmExtensions()
        {
            var model = ResolveModel();
            return model.StartsWith("glm-", StringComparison.OrdinalIgnoreCase);
        }

        private async Task<HttpResponseMessage> SendWithTimeoutAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(config.TimeoutSeconds));
                try
                {
                    return await Http.SendAsync(request, timeout.Token);
                }
                catch (OperationCanceledException exception)
                    when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        "语言模型请求超过 " + config.TimeoutSeconds + " 秒：" +
                        ProviderId + "/" + Model + "。", exception);
                }
            }
        }

        private static void NormalizeConfig(DeepSeekConfigData value)
        {
            value.BaseUrl = string.IsNullOrWhiteSpace(value.BaseUrl)
                ? "https://api.deepseek.com" : value.BaseUrl.Trim();
            value.Model = string.IsNullOrWhiteSpace(value.Model)
                ? "deepseek-v4-flash" : value.Model.Trim();
            value.Temperature = Math.Max(0f, Math.Min(2f, value.Temperature));
            value.TopP = Math.Max(0.01f, Math.Min(1f, value.TopP));
            value.MaxTokens = Math.Max(128, Math.Min(384000, value.MaxTokens));
            value.TimeoutSeconds = Math.Max(5, Math.Min(600, value.TimeoutSeconds));
            value.EmptyContentRetries = Math.Max(0, Math.Min(3, value.EmptyContentRetries));
            value.ReasoningEffort = NormalizeReasoningEffort(value.ReasoningEffort);
        }

        private static string NormalizeReasoningEffort(string value)
        {
            value = (value ?? string.Empty).Trim().ToLowerInvariant();
            return value == "low" || value == "max" ? value : "high";
        }

        private static string GetRequestId(HttpResponseMessage response)
        {
            IEnumerable<string> values;
            if (response != null &&
                (response.Headers.TryGetValues("x-request-id", out values) ||
                 response.Headers.TryGetValues("request-id", out values)))
                return string.Join(",", values);
            return "unknown";
        }

        private sealed class CompletionAttempt
        {
            public string Content { get; private set; }
            public string FinishReason { get; private set; }
            public string Diagnostic { get; private set; }

            public CompletionAttempt(
                string content,
                string finishReason,
                string reasoningContent,
                string requestId)
            {
                Content = content;
                FinishReason = finishReason ?? string.Empty;
                Diagnostic = "finish_reason=" +
                             (string.IsNullOrWhiteSpace(finishReason) ? "unknown" : finishReason) +
                             "，reasoning长度=" + (reasoningContent ?? string.Empty).Length +
                             "，request_id=" + requestId;
            }
        }
    }
}
