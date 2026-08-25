using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    public sealed class DeepSeekClientManager : ILlmClient, ILlmUsageReporter, ILlmEndpoint
    {
        private static readonly HttpClient Http = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        private readonly DeepSeekConfigData config;
        private LlmUsageData lastUsage;

        /// <summary>按需 LLM 原文导出：设置 TRACESOUL2_LLM_DUMP_DIR 后，每次调用把请求/响应原文落盘（不含 API Key）。</summary>
        private static readonly string LlmDumpDir =
            Environment.GetEnvironmentVariable("TRACESOUL2_LLM_DUMP_DIR");
        private static int llmDumpCounter;
        private static readonly JsonSerializerOptions OmitNullJson = new JsonSerializerOptions
        {
            IncludeFields = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public string ProviderId
        {
            get { return string.IsNullOrWhiteSpace(config.ProviderId) ? "default" : config.ProviderId; }
        }

        public string Model { get { return config.Model; } }

        public string BaseUrl { get { return config.BaseUrl; } }

        public LlmUsageData LastUsage { get { return lastUsage; } }

        /// <summary>只读快照，供 Kimi 策略包识别官网渠道。</summary>
        public DeepSeekConfigData ConfigSnapshot { get { return config; } }

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

        /// <summary>
        /// 按供应商拼 Chat Completions 请求体。Kimi 官网固定 temperature/top_p，K3 走 reasoning_effort。
        /// </summary>
        public static string BuildChatRequestJson(
            DeepSeekConfigData config,
            List<DeepSeekMessageData> messages,
            float temperature,
            bool json,
            bool useJsonResponseFormat,
            string promptCacheKey = null)
        {
            if (config == null) throw new ArgumentNullException("config");
            var model = ResolveRequestModel(config);
            if (UsesKimiOfficialApi(config))
                return JsonSerializer.Serialize(
                    BuildKimiChatRequest(config, model, messages, json, useJsonResponseFormat, promptCacheKey),
                    OmitNullJson);
            if (UsesDeepSeekExtensions(config))
            {
                if (json)
                {
                    return TraceJson.ToJson(new DeepSeekChatRequestData
                    {
                        model = model,
                        messages = messages,
                        response_format = useJsonResponseFormat ? new DeepSeekResponseFormatData() : null,
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
                return TraceJson.ToJson(new GlmChatRequestData
                {
                    model = model,
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
            if (UsesGlmExtensions(model))
            {
                return TraceJson.ToJson(new GlmChatRequestData
                {
                    model = model,
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
            return JsonSerializer.Serialize(
                new OpenAiChatRequestData
                {
                    model = model,
                    messages = messages,
                    response_format = json && useJsonResponseFormat
                        ? new DeepSeekResponseFormatData()
                        : null,
                    temperature = temperature,
                    top_p = config.TopP,
                    max_tokens = config.MaxTokens
                },
                OmitNullJson);
        }

        public static bool UsesKimiOfficialApi(DeepSeekConfigData config)
        {
            return OfficialLlmChannelLogic.Resolve(config) == OfficialLlmChannel.Kimi;
        }

        private static KimiChatRequestData BuildKimiChatRequest(
            DeepSeekConfigData config,
            string model,
            List<DeepSeekMessageData> messages,
            bool json,
            bool useJsonResponseFormat,
            string promptCacheKey)
        {
            var request = new KimiChatRequestData
            {
                model = model,
                messages = messages,
                response_format = json && useJsonResponseFormat ? new DeepSeekResponseFormatData() : null,
                max_completion_tokens = config.MaxTokens > 0 ? config.MaxTokens : (int?)null,
                prompt_cache_key = NormalizePromptCacheKey(promptCacheKey)
            };
            if (IsKimiK3Model(model))
            {
                // K3 始终思考，没有 thinking 开关；关思考槽时降到 low，禁止传 none。
                request.reasoning_effort = config.ThinkingEnabled
                    ? NormalizeReasoningEffort(config.ReasoningEffort)
                    : "low";
            }
            else if (IsKimiK2ThinkingToggleModel(model))
            {
                request.thinking = new DeepSeekThinkingData
                {
                    type = config.ThinkingEnabled ? "enabled" : "disabled"
                };
            }
            return request;
        }

        private static bool IsKimiK3Model(string model)
        {
            model = (model ?? string.Empty).Trim().ToLowerInvariant();
            return model == "kimi-k3" ||
                   model.StartsWith("kimi-k3-", StringComparison.Ordinal) ||
                   model.StartsWith("kimi-k3/", StringComparison.Ordinal);
        }

        private static bool IsKimiK2ThinkingToggleModel(string model)
        {
            model = (model ?? string.Empty).Trim().ToLowerInvariant();
            if (model.IndexOf("kimi-k2.7-code", StringComparison.Ordinal) >= 0) return false;
            return model.IndexOf("kimi-k2.6", StringComparison.Ordinal) >= 0 ||
                   model.IndexOf("kimi-k2.5", StringComparison.Ordinal) >= 0;
        }

        public Task<string> CompleteJsonAsync(
            List<DeepSeekMessageData> messages,
            CancellationToken cancellationToken = default(CancellationToken),
            string promptCacheKey = null)
        {
            return CompleteCoreAsync(messages, true, cancellationToken, promptCacheKey);
        }

        public Task<string> CompleteTextAsync(
            List<DeepSeekMessageData> messages,
            CancellationToken cancellationToken = default(CancellationToken),
            string promptCacheKey = null)
        {
            return CompleteCoreAsync(messages, false, cancellationToken, promptCacheKey);
        }

        private async Task<string> CompleteCoreAsync(
            List<DeepSeekMessageData> messages,
            bool json,
            CancellationToken cancellationToken,
            string promptCacheKey)
        {
            if (string.IsNullOrWhiteSpace(config.ApiKey))
                throw new InvalidOperationException("语言模型 API Key 尚未填写。");

            // 官方文档提示 JSON Output 偶尔可能返回空 content。第二次请求追加纠偏指令，
            // 避免把完全相同的请求盲目重放后再次命中相同结果。
            // OpenCode 等中转站还会在 json_object 上直接 500 / Internal server error，
            // 那种失败原先会整轮炸掉，这里用同一次额度再打一枪，并摘掉 response_format。
            var diagnostics = new List<string>();
            var attempts = 1 + config.EmptyContentRetries;
            var current = messages;
            var useJsonResponseFormat = json;
            for (var attempt = 0; attempt < attempts; attempt++)
            {
                CompletionAttempt result;
                try
                {
                    result = await SendOnceAsync(current, json, useJsonResponseFormat, cancellationToken, promptCacheKey);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (
                    attempt < attempts - 1 && IsRetryableProviderException(exception))
                {
                    diagnostics.Add("第" + (attempt + 1) + "次上游失败：" + exception.Message);
                    if (json && useJsonResponseFormat)
                        useJsonResponseFormat = false;
                    current = messages;
                    continue;
                }
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
                // 少数 OpenAI 兼容中转在 response_format=json_object 下会返回
                // finish=stop 但 content 为空。下一次保留 JSON 提示，但去掉协议级
                // response_format，让模型仍可输出 JSON，同时避开该通道的空响应路径。
                if (json && !truncated) useJsonResponseFormat = false;
            }

            throw new InvalidOperationException(
                "语言模型连续 " + attempts + " 次返回了空 content。" +
                string.Join("；", diagnostics));
        }

        private async Task<CompletionAttempt> SendOnceAsync(
            List<DeepSeekMessageData> messages,
            bool json,
            bool useJsonResponseFormat,
            CancellationToken cancellationToken,
            string promptCacheKey)
        {
            var temperature = ResolveTemperature();
            try
            {
                return await PostOnceAsync(
                    messages, temperature, json, useJsonResponseFormat, cancellationToken, promptCacheKey);
            }
            catch (InvalidOperationException exception)
            {
                if (Math.Abs(temperature - 1f) > 0.001f &&
                    LooksLikeUnitTemperatureOnly(exception.Message))
                    return await PostOnceAsync(
                        messages, 1f, json, useJsonResponseFormat, cancellationToken, promptCacheKey);
                throw;
            }
        }

        private async Task<CompletionAttempt> PostOnceAsync(
            List<DeepSeekMessageData> messages,
            float temperature,
            bool json,
            bool useJsonResponseFormat,
            CancellationToken cancellationToken,
            string promptCacheKey)
        {
            var bodyJson = BuildChatRequestJson(
                config, messages, temperature, json, useJsonResponseFormat, promptCacheKey);
            var endpoint = config.BaseUrl.TrimEnd('/') + "/chat/completions";

            using (var request = new HttpRequestMessage(HttpMethod.Post, endpoint))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
                request.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");

                using (var response = await SendWithTimeoutAsync(request, cancellationToken))
                {
                    var body = await response.Content.ReadAsStringAsync();
                    lastUsage = LlmUsageLogic.Parse(body);
                    var httpStatus = (int)response.StatusCode;
                    DumpCall(bodyJson, body, httpStatus);
                    DeepSeekChatResponseData parsed = null;
                    try
                    {
                    parsed = TraceJson.FromJson<DeepSeekChatResponseData>(body);
                    }
                    catch (Exception exception)
                    {
                        throw new InvalidOperationException(
                            "语言模型返回了无法解析的响应，HTTP " +
                            httpStatus + "，body长度=" + body.Length + "。",
                            exception);
                    }
                    if (!response.IsSuccessStatusCode)
                    {
                        var detail = parsed != null && parsed.error != null
                            ? parsed.error.message
                            : ExtractErrorMessage(body);
                        throw new InvalidOperationException(
                            "语言模型 API " + httpStatus + ": " + detail);
                    }

                    if (IsErrorOnlyChatResponse(parsed, body))
                    {
                        var detail = parsed != null && parsed.error != null &&
                                     !string.IsNullOrWhiteSpace(parsed.error.message)
                            ? parsed.error.message
                            : ExtractErrorMessage(body);
                        throw new InvalidOperationException(
                            "语言模型上游失败 HTTP " + httpStatus + ": " + detail);
                    }

                    if (parsed == null || parsed.choices == null || parsed.choices.Count == 0 ||
                        parsed.choices[0].message == null)
                        throw new InvalidOperationException(
                            "语言模型返回结构不完整，HTTP " + httpStatus +
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

        private static void DumpCall(string requestJson, string responseBody, int httpStatus)
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
                    "http=" + httpStatus + Environment.NewLine + ParseResponseText(responseBody), enc);
                var usage = LlmUsageLogic.FormatDump(LlmUsageLogic.Parse(responseBody));
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
                if ((requestJson ?? string.Empty).IndexOf("max_completion_tokens", StringComparison.Ordinal) >= 0 ||
                    (requestJson ?? string.Empty).IndexOf("prompt_cache_key", StringComparison.Ordinal) >= 0)
                    return FormatKimiRequestText(TraceJson.FromJson<KimiChatRequestData>(requestJson));
                var parsed = TraceJson.FromJson<DeepSeekChatRequestData>(requestJson);
                var builder = new StringBuilder();
                builder.Append("model=").AppendLine(parsed == null ? "?" : parsed.model);
                if (parsed != null && parsed.response_format != null &&
                    !string.IsNullOrWhiteSpace(parsed.response_format.type))
                    builder.Append("response_format=").AppendLine(parsed.response_format.type);
                if (parsed != null && !string.IsNullOrWhiteSpace(parsed.reasoning_effort))
                    builder.Append("reasoning_effort=").AppendLine(parsed.reasoning_effort);
                AppendMessagesDump(builder, parsed == null ? null : parsed.messages);
                return builder.ToString();
            }
            catch
            {
                return requestJson ?? string.Empty;
            }
        }

        private static string FormatKimiRequestText(KimiChatRequestData parsed)
        {
            var builder = new StringBuilder();
            builder.Append("model=").AppendLine(parsed == null ? "?" : parsed.model);
            if (parsed != null && parsed.response_format != null &&
                !string.IsNullOrWhiteSpace(parsed.response_format.type))
                builder.Append("response_format=").AppendLine(parsed.response_format.type);
            if (parsed != null && !string.IsNullOrWhiteSpace(parsed.reasoning_effort))
                builder.Append("reasoning_effort=").AppendLine(parsed.reasoning_effort);
            if (parsed != null && !string.IsNullOrWhiteSpace(parsed.prompt_cache_key))
                builder.Append("prompt_cache_key=").AppendLine(parsed.prompt_cache_key);
            AppendMessagesDump(builder, parsed == null ? null : parsed.messages);
            return builder.ToString();
        }

        private static void AppendMessagesDump(StringBuilder builder, List<DeepSeekMessageData> messages)
        {
            foreach (var m in messages ?? new List<DeepSeekMessageData>())
            {
                builder.Append("【").Append(m.role ?? "?").Append("】").AppendLine();
                if (!string.IsNullOrWhiteSpace(m.reasoning_content))
                {
                    builder.Append("[reasoning]").AppendLine();
                    builder.AppendLine(m.reasoning_content);
                }
                builder.AppendLine(m.content ?? string.Empty);
                builder.AppendLine();
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
            return ResolveRequestModel(config);
        }

        public static string ResolveRequestModel(DeepSeekConfigData config)
        {
            var model = (config.Model ?? string.Empty).Trim();
            var prefix = (config.ProviderId ?? string.Empty).Trim() + "/";
            if (prefix.Length > 1 &&
                model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                model = model.Substring(prefix.Length);
            if (IsOpenCodeZen(config))
                model = NormalizeOpenCodeModel(model);
            return model;
        }

        private static bool IsOpenCodeZen(DeepSeekConfigData config)
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

        /// <summary>
        /// OpenCode 等中转站会用 HTTP 200 包一层 <c>{"type":"error"}</c>，没有 choices。
        /// </summary>
        public static bool IsErrorOnlyChatResponse(DeepSeekChatResponseData parsed, string body)
        {
            if (parsed != null && parsed.error != null &&
                (parsed.choices == null || parsed.choices.Count == 0))
                return true;
            return LooksLikeErrorOnlyBody(body);
        }

        public static bool IsRetryableProviderFailure(int httpStatus, string body)
        {
            if (httpStatus == 401 || httpStatus == 403 || httpStatus == 404)
                return false;
            if (httpStatus == 429 || httpStatus >= 500)
                return true;
            return LooksLikeErrorOnlyBody(body) || LooksLikeTransientUpstream(body);
        }

        public static bool IsRetryableProviderException(Exception exception)
        {
            if (exception == null) return false;
            if (exception is TimeoutException || exception is HttpRequestException)
                return true;
            var message = exception.Message ?? string.Empty;
            if (message.IndexOf("API Key", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("尚未填写", StringComparison.Ordinal) >= 0)
                return false;
            return message.IndexOf("上游失败", StringComparison.Ordinal) >= 0 ||
                   message.IndexOf("结构不完整", StringComparison.Ordinal) >= 0 ||
                   message.IndexOf("Internal server error", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("Upstream request failed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("Floating point NaN", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("API 429", StringComparison.Ordinal) >= 0 ||
                   message.IndexOf("API 5", StringComparison.Ordinal) >= 0 ||
                   (message.IndexOf("超过", StringComparison.Ordinal) >= 0 &&
                    message.IndexOf("秒", StringComparison.Ordinal) >= 0);
        }

        private static bool LooksLikeErrorOnlyBody(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return false;
            try
            {
                using (var doc = JsonDocument.Parse(body))
                {
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) return false;
                    var type = root.TryGetProperty("type", out var typeEl) &&
                               typeEl.ValueKind == JsonValueKind.String
                        ? typeEl.GetString()
                        : string.Empty;
                    var hasChoices = root.TryGetProperty("choices", out var choices) &&
                                     choices.ValueKind == JsonValueKind.Array &&
                                     choices.GetArrayLength() > 0;
                    var hasError = root.TryGetProperty("error", out _);
                    if (hasChoices) return false;
                    return hasError || string.Equals(type, "error", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (JsonException)
            {
                return LooksLikeTransientUpstream(body);
            }
        }

        private static bool LooksLikeTransientUpstream(string body)
        {
            var text = body ?? string.Empty;
            return text.IndexOf("Internal server error", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   text.IndexOf("Upstream request failed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   text.IndexOf("Floating point NaN", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool UsesDeepSeekExtensions(DeepSeekConfigData config)
        {
            var type = (config.Type ?? string.Empty).Trim().ToLowerInvariant();
            if (type.IndexOf("deepseek", StringComparison.Ordinal) >= 0) return true;
            var url = (config.BaseUrl ?? string.Empty).Trim();
            return url.IndexOf("deepseek.com", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool UsesGlmExtensions(string model)
        {
            return (model ?? string.Empty).StartsWith("glm-", StringComparison.OrdinalIgnoreCase);
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

        private static string NormalizePromptCacheKey(string value)
        {
            value = (value ?? string.Empty).Trim();
            return value.Length == 0 ? null : value;
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
