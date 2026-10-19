using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TraceSoul2.Host
{
    /// <summary>Native typed decisions, isolated from chat, dumps and automatic retries.</summary>
    public sealed class DecisionsClient : IDisposable
    {
        private readonly HttpClient http;
        private readonly LlmProviderRecord config;
        public string Model => config.model;
        public static void ValidateConfiguration(LlmProviderRecord value)
        {
            if (value.decisionMode is not (null or "" or "direct" or "relay"))
                throw new InvalidOperationException("决策连接模式必须是 direct 或 relay。");
            var address = value.baseUrl ?? "";
            if (address.Any(char.IsWhiteSpace) || !Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
                (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) ||
                !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                throw new InvalidOperationException("决策基址须为 HTTPS（本机测试可用 HTTP），不能包含账号、查询或片段。");
            foreach (var key in new[] { value.apiKey, value.relayKey })
                if ((key ?? "").Any(c => c <= 32 || c >= 127))
                    throw new InvalidOperationException("决策凭据含无效字符。");
            if (!string.IsNullOrWhiteSpace(value.proxy) &&
                (!Uri.TryCreate(value.proxy, UriKind.Absolute, out var proxy) ||
                 proxy.Scheme is not ("http" or "https" or "socks5") || !string.IsNullOrEmpty(proxy.UserInfo)))
                throw new InvalidOperationException("代理请填写不含账号的 http、https 或 socks5 地址。");
        }

        public DecisionsClient(LlmProviderRecord value, HttpMessageHandler handler = null)
        {
            ValidateConfiguration(value);
            config = value;
            if (value.decisionMode == "relay" && string.IsNullOrWhiteSpace(value.relayKey))
                throw new InvalidOperationException("专用中转尚未填写中转凭据。");
            if (handler == null)
            {
                var transport = new HttpClientHandler { AllowAutoRedirect = false };
                if (!string.IsNullOrWhiteSpace(value.proxy)) transport.Proxy = new WebProxy(value.proxy);
                handler = transport;
            }
            http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(Math.Clamp(value.timeout <= 0 ? 60 : value.timeout, 1, 300)) };
        }

        private string Url(bool models) => config.baseUrl.TrimEnd('/') +
            (config.decisionMode == "relay" ? (models ? "/models" : "/decisions") :
             (models ? "/v1/models?output_modalities=decisions" : "/alpha/decisions"));

        private async Task<JsonElement> RequestAsync(bool models, object payload, CancellationToken token)
        {
            using var request = new HttpRequestMessage(models ? HttpMethod.Get : HttpMethod.Post, Url(models));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (config.decisionMode == "relay") request.Headers.Add("X-TraceSoul-Relay-Key", config.relayKey);
            if (!models)
            {
                if (string.IsNullOrWhiteSpace(config.apiKey)) throw new InvalidOperationException("尚未填写 OpenRouter API Key。");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.apiKey);
                var body = JsonSerializer.SerializeToUtf8Bytes(payload);
                if (body.Length > 256 * 1024) throw new InvalidOperationException("决策请求超过 256 KiB。");
                request.Content = new ByteArrayContent(body);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            }
            try
            {
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException("决策服务返回 HTTP " + (int)response.StatusCode + "；未自动重试。请核对服务权限及配置。");
                using var stream = await response.Content.ReadAsStreamAsync(token);
                using var buffer = new MemoryStream();
                var chunk = new byte[8192];
                int count;
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                readTimeout.CancelAfter(http.Timeout);
                while ((count = await stream.ReadAsync(chunk.AsMemory(), readTimeout.Token)) > 0)
                {
                    if (buffer.Length + count > 1024 * 1024) throw new InvalidOperationException("决策响应超过 1 MiB。");
                    buffer.Write(chunk, 0, count);
                }
                using var doc = JsonDocument.Parse(buffer.ToArray());
                return doc.RootElement.Clone();
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { throw new InvalidOperationException("决策请求超时；未自动重试。"); }
            catch (HttpRequestException) { throw new InvalidOperationException("决策服务连接或 TLS 失败；未自动重试。"); }
            catch (IOException) { throw new InvalidOperationException("决策响应读取失败；未自动重试。"); }
            catch (JsonException) { throw new InvalidOperationException("决策服务未返回有效 JSON。"); }
        }

        public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken token)
        {
            var result = await RequestAsync(true, null, token);
            if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("决策模型列表格式不正确。");
            return data.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object &&
                x.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                .Select(x => x.GetProperty("id").GetString()).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
        }

        public async Task<JsonElement> DecideAsync(JsonElement state, JsonElement questions, string model, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(model)) throw new InvalidOperationException("请选择决策模型。");
            ValidateQuestions(questions);
            var result = await RequestAsync(false, new { model, state, questions }, token);
            ValidateAnswers(result, questions);
            return result;
        }

        private static bool Probability(JsonElement value) => value.ValueKind == JsonValueKind.Number &&
            value.TryGetDouble(out var n) && double.IsFinite(n) && n >= 0 && n <= 1;
        public static void ValidateQuestions(JsonElement questions)
        {
            if (questions.ValueKind != JsonValueKind.Object || questions.EnumerateObject().Count() is < 1 or > 20)
                throw new InvalidOperationException("决策需要 1～20 个命名问题。");
            foreach (var q in questions.EnumerateObject())
            {
                var v = q.Value;
                if (string.IsNullOrWhiteSpace(q.Name) || v.ValueKind != JsonValueKind.Object ||
                    !v.TryGetProperty("instructions", out var instruction) || instruction.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(instruction.GetString()) || !v.TryGetProperty("type", out var type))
                    throw new InvalidOperationException("决策问题缺少类型或说明。");
                var kind = type.ValueKind == JsonValueKind.String ? type.GetString() : "";
                v.TryGetProperty("criteria", out var criteria);
                var valid = kind switch
                {
                    "choice" => criteria.ValueKind == JsonValueKind.Object && criteria.EnumerateObject().Count() is >= 2 and <= 20 &&
                        criteria.EnumerateObject().All(x => !string.IsNullOrWhiteSpace(x.Name) && x.Value.ValueKind is JsonValueKind.String or JsonValueKind.Null),
                    "score" => criteria.ValueKind == JsonValueKind.Array && criteria.GetArrayLength() is >= 2 and <= 20 &&
                        criteria.EnumerateArray().All(x => x.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(x.GetString())),
                    "noul" => criteria.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ||
                        criteria.ValueKind == JsonValueKind.Object && criteria.EnumerateObject().Count() == 2 &&
                        criteria.TryGetProperty("true", out var yes) && yes.ValueKind == JsonValueKind.String &&
                        criteria.TryGetProperty("false", out var no) && no.ValueKind == JsonValueKind.String,
                    _ => false
                };
                if (!valid) throw new InvalidOperationException("决策问题格式无效；当前支持 choice、noul、score。");
            }
        }

        public static void ValidateAnswers(JsonElement result, JsonElement questions)
        {
            const string error = "决策响应与问题不匹配，不能作为有效决策。";
            if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("answers", out var answers) ||
                answers.ValueKind != JsonValueKind.Object || answers.EnumerateObject().Count() != questions.EnumerateObject().Count())
                throw new InvalidOperationException(error);
            foreach (var q in questions.EnumerateObject())
            {
                var kind = q.Value.GetProperty("type").GetString();
                if (!answers.TryGetProperty(q.Name, out var a) || a.ValueKind != JsonValueKind.Object ||
                    !a.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.String || t.GetString() != kind)
                    throw new InvalidOperationException(error);
                if (kind == "noul")
                {
                    if (!a.TryGetProperty("noul", out var n) || !Probability(n)) throw new InvalidOperationException(error);
                    continue;
                }
                var criteria = q.Value.GetProperty("criteria");
                var keys = kind == "choice" ? criteria.EnumerateObject().Select(x => x.Name).ToArray() :
                    Enumerable.Range(0, criteria.GetArrayLength()).Select(x => x.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                if (!a.TryGetProperty("confidence", out var confidence) || !Probability(confidence) ||
                    !a.TryGetProperty("probabilities", out var p) || p.ValueKind != JsonValueKind.Object ||
                    p.EnumerateObject().Count() != keys.Length || keys.Any(k => !p.TryGetProperty(k, out var n) || !Probability(n)) ||
                    Math.Abs(p.EnumerateObject().Sum(x => x.Value.GetDouble()) - 1) > 0.02)
                    throw new InvalidOperationException(error);
                if (kind == "choice" && (!a.TryGetProperty("choice", out var c) || c.ValueKind != JsonValueKind.String || !keys.Contains(c.GetString())))
                    throw new InvalidOperationException(error);
                if (kind == "score" && (!a.TryGetProperty("score", out var s) || s.ValueKind != JsonValueKind.Number ||
                    !s.TryGetDouble(out var scoreValue) || !double.IsFinite(scoreValue) || scoreValue < 0 || scoreValue > keys.Length - 1))
                    throw new InvalidOperationException(error);
            }
        }

        public async Task<object> TestAsync(string model, CancellationToken token)
        {
            var state = JsonSerializer.SerializeToElement(new { scene = "模拟场景：用户安静阅读，没有待办或新变化。", simulation = true });
            var questions = JsonSerializer.SerializeToElement(new { next_step = new { type = "choice",
                instructions = "选择下一步；只返回建议，不执行动作。", criteria = new { wait = "无事需要处理，等待", engage = "有明确待办或变化，交主模型" } } });
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var result = await DecideAsync(state, questions, model, token);
            var answer = result.GetProperty("answers").GetProperty("next_step");
            double? Metric(string name) => result.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object &&
                u.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d) ? d : null;
            return new { model, elapsedMs = clock.ElapsedMilliseconds, choice = answer.GetProperty("choice").GetString(),
                confidence = answer.GetProperty("confidence").GetDouble(),
                probabilities = new { wait = answer.GetProperty("probabilities").GetProperty("wait").GetDouble(), engage = answer.GetProperty("probabilities").GetProperty("engage").GetDouble() },
                usage = new { input_tokens = Metric("input_tokens"), output_tokens = Metric("output_tokens"), cost = Metric("cost") } };
        }
        public void Dispose() => http.Dispose();
    }
}
