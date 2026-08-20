using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Logic;
using TraceSoul2.Manager;

namespace TraceSoul2.Host
{
    /// <summary>
    /// Google Gemini 原生 generateContent 口。请求形状抄 AstrBot <c>gemini_source.py</c>：
    /// system_instruction + contents(user/model) + generationConfig + safetySettings。
    /// 陪伴场景默认 BLOCK_NONE；撞到 RECITATION 时按 AstrBot 把温度 +0.2 重试。
    /// </summary>
    public sealed class GeminiClientManager : ILlmClient
    {
        private static readonly HttpClient Http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(300)
        };

        private readonly DeepSeekConfigData config;

        private static readonly string LlmDumpDir =
            Environment.GetEnvironmentVariable("TRACESOUL2_LLM_DUMP_DIR");
        private static int llmDumpCounter;

        public string ProviderId
        {
            get { return string.IsNullOrWhiteSpace(config.ProviderId) ? "google_gemini" : config.ProviderId; }
        }

        public string Model { get { return config.Model; } }

        public GeminiClientManager(DeepSeekConfigData config)
        {
            this.config = config ?? throw new ArgumentNullException("config");
            if (string.IsNullOrWhiteSpace(this.config.BaseUrl))
                this.config.BaseUrl = "https://generativelanguage.googleapis.com/";
            if (string.IsNullOrWhiteSpace(this.config.Model))
                this.config.Model = "gemini-2.5-flash";
            this.config.Temperature = Math.Max(0f, Math.Min(2f, this.config.Temperature));
            this.config.TopP = Math.Max(0.01f, Math.Min(1f, this.config.TopP));
            this.config.MaxTokens = Math.Max(128, Math.Min(384000, this.config.MaxTokens));
            this.config.EmptyContentRetries = Math.Max(0, Math.Min(3, this.config.EmptyContentRetries));
        }

        public async Task<IReadOnlyList<string>> ListModelsAsync(
            CancellationToken cancellationToken = default(CancellationToken))
        {
            RequireKey();
            var url = GeminiRoot(config.BaseUrl) + "/models?key=" + Uri.EscapeDataString(config.ApiKey);
            using (var response = await Http.GetAsync(url, cancellationToken))
            {
                var body = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException(
                        "获取 Gemini 模型列表失败 HTTP " + (int)response.StatusCode + "。");
                return ParseModelIds(body);
            }
        }

        public async Task<string> CompleteJsonAsync(
            List<DeepSeekMessageData> messages,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            RequireKey();
            var diagnostics = new List<string>();
            var attempts = 1 + config.EmptyContentRetries;
            var temperature = config.Temperature;
            var recitationTries = 0;
            var current = messages;
            for (var attempt = 0; attempt < attempts; )
            {
                var result = await SendOnceAsync(current, temperature, cancellationToken);
                if (string.Equals(result.FinishReason, "RECITATION", StringComparison.OrdinalIgnoreCase))
                {
                    recitationTries++;
                    if (temperature >= 2f || recitationTries > 8)
                        throw new InvalidOperationException("Gemini recitation 在提高温度后仍未解除。");
                    temperature = Math.Min(2f, temperature + 0.2f);
                    diagnostics.Add("recitation，温度提到 " + temperature.ToString("0.0"));
                    continue;
                }
                var incomplete = DeepSeekStructuredOutputLogic.LooksIncompleteJson(result.Content);
                if (!incomplete && !string.IsNullOrWhiteSpace(result.Content)) return result.Content;
                diagnostics.Add("第" + (attempt + 1) + "次：" + result.Diagnostic);
                if (attempt >= attempts - 1)
                {
                    if (!string.IsNullOrWhiteSpace(result.Content)) return result.Content;
                    break;
                }
                current = incomplete || DeepSeekStructuredOutputLogic.LooksLikeTruncatedFinish(result.FinishReason)
                    ? AppendRetry(messages, "刚才的 JSON 被截断了，不完整。这不是新任务。现在立即输出一个更紧凑、完整、合法的 JSON 对象；第一个字符必须是 {，最后一个字符必须是 }，闭合全部字符串与数组，不要解释。")
                    : AppendRetry(messages, "刚才没有产生可读取的 JSON。这不是新任务。现在立即输出一个紧凑、完整、合法的 JSON 对象；第一个字符必须是 {，最后一个字符必须是 }，不要解释。");
                attempt++;
            }
            throw new InvalidOperationException(
                "Gemini 连续返回空 content。" + string.Join("；", diagnostics));
        }

        private async Task<GeminiAttempt> SendOnceAsync(
            List<DeepSeekMessageData> messages,
            float temperature,
            CancellationToken cancellationToken)
        {
            var payload = BuildPayload(messages, temperature);
            var model = StripModelsPrefix(config.Model);
            var url = GeminiRoot(config.BaseUrl) + "/models/" + Uri.EscapeDataString(model) +
                      ":generateContent?key=" + Uri.EscapeDataString(config.ApiKey);
            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                using (var response = await Http.SendAsync(request, cancellationToken))
                {
                    var body = await response.Content.ReadAsStringAsync();
                    DumpCall(payload, body);
                    if (!response.IsSuccessStatusCode)
                        throw new InvalidOperationException(
                            "Gemini API " + (int)response.StatusCode + ": " + TrimError(body));
                    return ParseAttempt(body);
                }
            }
        }

        private string BuildPayload(List<DeepSeekMessageData> messages, float temperature)
        {
            var systems = new List<string>();
            var contents = new List<object>();
            foreach (var item in messages ?? new List<DeepSeekMessageData>())
            {
                var role = (item.role ?? string.Empty).Trim().ToLowerInvariant();
                var text = string.IsNullOrWhiteSpace(item.content) ? " " : item.content;
                if (role == "system")
                {
                    systems.Add(text);
                    continue;
                }
                contents.Add(new
                {
                    role = role == "assistant" ? "model" : "user",
                    parts = new[] { new { text } }
                });
            }
            if (contents.Count == 0)
                contents.Add(new { role = "user", parts = new[] { new { text = " " } } });

            var generation = new Dictionary<string, object>
            {
                { "temperature", temperature },
                { "topP", config.TopP },
                { "maxOutputTokens", config.MaxTokens },
                { "responseMimeType", "application/json" }
            };
            if (config.ThinkingEnabled)
                generation["thinkingConfig"] = new { thinkingBudget = 1024 };

            var body = new Dictionary<string, object>
            {
                { "contents", contents },
                { "generationConfig", generation },
                {
                    "safetySettings", new[]
                    {
                        Safety("HARM_CATEGORY_HARASSMENT"),
                        Safety("HARM_CATEGORY_HATE_SPEECH"),
                        Safety("HARM_CATEGORY_SEXUALLY_EXPLICIT"),
                        Safety("HARM_CATEGORY_DANGEROUS_CONTENT")
                    }
                }
            };
            if (systems.Count > 0)
                body["systemInstruction"] = new { parts = new[] { new { text = string.Join("\n\n", systems) } } };

            return JsonSerializer.Serialize(body);
        }

        private static List<DeepSeekMessageData> AppendRetry(
            IEnumerable<DeepSeekMessageData> source, string instruction)
        {
            var result = new List<DeepSeekMessageData>(source ?? new DeepSeekMessageData[0]);
            result.Add(new DeepSeekMessageData("user", instruction));
            return result;
        }

        private static object Safety(string category)
        {
            return new { category, threshold = "BLOCK_NONE" };
        }

        private static GeminiAttempt ParseAttempt(string body)
        {
            using (var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body))
            {
                var root = doc.RootElement;
                if (!root.TryGetProperty("candidates", out var candidates) ||
                    candidates.ValueKind != JsonValueKind.Array ||
                    candidates.GetArrayLength() == 0)
                    return new GeminiAttempt("", "unknown", "candidates 为空");

                var first = candidates[0];
                var finish = first.TryGetProperty("finishReason", out var fr)
                    ? (fr.GetString() ?? "unknown") : "unknown";
                var text = new StringBuilder();
                if (first.TryGetProperty("content", out var content) &&
                    content.TryGetProperty("parts", out var parts) &&
                    parts.ValueKind == JsonValueKind.Array)
                {
                    foreach (var part in parts.EnumerateArray())
                    {
                        if (part.TryGetProperty("text", out var t))
                            text.Append(t.GetString());
                    }
                }
                return new GeminiAttempt(text.ToString(), finish, "finish_reason=" + finish);
            }
        }

        private static List<string> ParseModelIds(string body)
        {
            var ids = new List<string>();
            using (var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body))
            {
                if (!doc.RootElement.TryGetProperty("models", out var models) ||
                    models.ValueKind != JsonValueKind.Array)
                    return ids;
                foreach (var item in models.EnumerateArray())
                {
                    if (!item.TryGetProperty("name", out var nameEl)) continue;
                    var name = StripModelsPrefix(nameEl.GetString());
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var canGenerate = true;
                    if (item.TryGetProperty("supportedGenerationMethods", out var methods) &&
                        methods.ValueKind == JsonValueKind.Array)
                    {
                        canGenerate = false;
                        foreach (var method in methods.EnumerateArray())
                        {
                            if (string.Equals(method.GetString(), "generateContent", StringComparison.Ordinal))
                            {
                                canGenerate = true;
                                break;
                            }
                        }
                    }
                    if (canGenerate && !ids.Contains(name)) ids.Add(name);
                }
            }
            ids.Sort(StringComparer.Ordinal);
            return ids;
        }

        private static string GeminiRoot(string baseUrl)
        {
            var url = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
            if (url.EndsWith("/v1beta", StringComparison.OrdinalIgnoreCase)) return url;
            return url + "/v1beta";
        }

        private static string StripModelsPrefix(string name)
        {
            name = (name ?? string.Empty).Trim();
            const string prefix = "models/";
            return name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? name.Substring(prefix.Length) : name;
        }

        private void RequireKey()
        {
            if (string.IsNullOrWhiteSpace(config.ApiKey))
                throw new InvalidOperationException("Gemini API Key 尚未填写。");
        }

        private static string TrimError(string body)
        {
            body = body ?? string.Empty;
            return body.Length <= 800 ? body : body.Substring(0, 800);
        }

        private static void DumpCall(string requestJson, string responseBody)
        {
            var dir = LlmDumpDir;
            if (string.IsNullOrWhiteSpace(dir)) return;
            try
            {
                System.IO.Directory.CreateDirectory(dir);
                var seq = Interlocked.Increment(ref llmDumpCounter);
                var name = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + seq.ToString("D3");
                var enc = new UTF8Encoding(true);
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(dir, name + "-prompt.txt"), requestJson ?? string.Empty, enc);
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(dir, name + "-response.txt"),
                    ParseAttempt(responseBody).Content ?? responseBody ?? string.Empty, enc);
            }
            catch
            {
                /* 导出失败不影响主流程 */
            }
        }

        private sealed class GeminiAttempt
        {
            public string Content { get; private set; }
            public string FinishReason { get; private set; }
            public string Diagnostic { get; private set; }

            public GeminiAttempt(string content, string finishReason, string diagnostic)
            {
                Content = content ?? string.Empty;
                FinishReason = finishReason ?? string.Empty;
                Diagnostic = diagnostic ?? string.Empty;
            }
        }
    }
}
