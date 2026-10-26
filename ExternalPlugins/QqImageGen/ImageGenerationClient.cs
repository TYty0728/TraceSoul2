using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Plugins;

namespace TraceSoul2.ExternalPlugins
{
    internal sealed class ImageGenerationSettings
    {
        public string BaseUrl;
        public string Model;
        public List<string> ApiKeys = new List<string>();
        public string Proxy;
        public int TimeoutSeconds = 600;
    }

    internal sealed class GeneratedImageData
    {
        public byte[] Bytes;
        public string MimeType;
        public string Source;
    }

    internal sealed class ImageGenerationResult
    {
        public List<GeneratedImageData> Images = new List<GeneratedImageData>();
        public string Error = string.Empty;
        public string Protocol = string.Empty;
        public bool Success => Images.Count > 0;
    }

    internal sealed class ImageGenerationClient : IDisposable
    {
        private readonly ImageGenerationSettings settings;
        private readonly TracePluginServices services;
        private readonly string traceId;
        private readonly HttpClient http;

        public ImageGenerationClient(ImageGenerationSettings settings, TracePluginServices services, string traceId,
            HttpMessageHandler messageHandler = null)
        {
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
            this.services = services;
            this.traceId = traceId;
            if (messageHandler != null)
            {
                http = new HttpClient(messageHandler) { Timeout = Timeout.InfiniteTimeSpan };
                return;
            }
            var handler = new HttpClientHandler();
            if (!string.IsNullOrWhiteSpace(settings.Proxy))
            {
                handler.Proxy = new WebProxy(settings.Proxy.Trim());
                handler.UseProxy = true;
            }
            http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        }

        public void Dispose() => http.Dispose();

        public async Task<ImageGenerationResult> GenerateAsync(
            string prompt,
            IReadOnlyList<ReferenceImageData> references,
            string aspectRatio,
            CancellationToken cancellationToken)
        {
            var keys = settings.ApiKeys.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
            if (keys.Count == 0) return Failed("未配置 API Key。");
            if (string.IsNullOrWhiteSpace(settings.BaseUrl) || string.IsNullOrWhiteSpace(settings.Model))
                return Failed("缺少 base_url 或 model。");

            services?.LogTiming(traceId, "TA的相机 上游生成开始", detail:
                "model=" + settings.Model + "｜interface=images" +
                "｜refs=" + (references?.Count ?? 0) + "｜aspect=" + (aspectRatio ?? string.Empty));
            var timer = Stopwatch.StartNew();
            var result = await GenerateOpenAiStandardAsync(prompt, references ?? new List<ReferenceImageData>(),
                aspectRatio, keys[0], cancellationToken);
            services?.LogTiming(traceId, result.Success ? "生图上游完成" : "生图上游未完成",
                timer.ElapsedMilliseconds,
                "protocol=" + result.Protocol +
                (result.Success ? "｜images=" + result.Images.Count : "｜" + Truncate(result.Error, 400)));
            if (result.Success) return result;
            if (UpstreamUnavailable(result.Error))
                services?.LogTiming(traceId, "TA的相机 上游未返回，已停止", detail: Truncate(result.Error, 400));
            return Failed(string.IsNullOrWhiteSpace(result.Error) ? "上游未返回。" : result.Error, result.Protocol);
        }

        internal static bool UpstreamUnavailable(string error)
        {
            if (string.IsNullOrWhiteSpace(error)) return false;
            if (error.IndexOf("请求超时", StringComparison.Ordinal) >= 0) return true;
            if (error.IndexOf("HttpRequestException", StringComparison.Ordinal) >= 0) return true;
            if (error.IndexOf("SocketException", StringComparison.Ordinal) >= 0) return true;
            if (error.IndexOf("TimeoutException", StringComparison.Ordinal) >= 0) return true;
            if (error.IndexOf("TaskCanceledException", StringComparison.Ordinal) >= 0) return true;
            foreach (var code in new[] { "408", "429", "500", "502", "503", "504", "524" })
                if (error.IndexOf("HTTP " + code, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        private bool IsGptImage() =>
            (settings.Model ?? string.Empty).IndexOf("gpt-image", StringComparison.OrdinalIgnoreCase) >= 0 ||
            (settings.Model ?? string.Empty).IndexOf("chatgpt-image", StringComparison.OrdinalIgnoreCase) >= 0;

        private string ResolveStandardSize(string aspectRatio)
        {
            if (!IsGptImage()) return "1024x1024";
            var ratio = (aspectRatio ?? string.Empty).Split(':');
            if (ratio.Length != 2 || !int.TryParse(ratio[0], out var width) ||
                !int.TryParse(ratio[1], out var height) || width <= 0 || height <= 0)
                return "auto";
            if (width == height) return "1024x1024";
            if ((settings.Model ?? string.Empty).IndexOf("gpt-image-2", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // A 1536px long edge stays within GPT Image 2/2.5 pixel limits for ratios up to 3:1.
                var scale = (double)Math.Max(width, height) / Math.Min(width, height);
                if (scale > 3) return "auto";
                var shortEdge = (int)Math.Round(1536 / scale / 16) * 16;
                return width > height ? "1536x" + shortEdge : shortEdge + "x1536";
            }
            return width > height ? "1536x1024" : "1024x1536";
        }

        private async Task<ImageGenerationResult> GenerateOpenAiStandardAsync(
            string prompt,
            IReadOnlyList<ReferenceImageData> references,
            string aspectRatio,
            string apiKey,
            CancellationToken cancellationToken)
        {
            if (references.Count > 0)
                return await GenerateOpenAiEditAsync(prompt, references, aspectRatio, apiKey, cancellationToken);
            var payload = new Dictionary<string, object>
            {
                { "model", settings.Model }, { "prompt", prompt }, { "n", 1 },
                { "size", ResolveStandardSize(aspectRatio) }
            };
            // GPT Image always returns b64_json and does not accept response_format.
            if (!IsGptImage()) payload["response_format"] = "b64_json";
            var response = await SendJsonAsync(HttpMethod.Post, NormalizeOpenAiBase() + "/images/generations",
                apiKey, payload, cancellationToken);
            if (!response.Success) return Failed(response.Error, "openai-images");
            var roots = ParseRoots(response.Body, out var parseError);
            if (roots.Count == 0) return Failed(parseError, "openai-images");
            var extracted = await ExtractImagesAsync(roots, apiKey, "openai-images", cancellationToken);
            return extracted.Success ? extracted : Failed("响应中没有 b64_json/url。" + extracted.Error, "openai-images");
        }

        private async Task<ImageGenerationResult> GenerateOpenAiEditAsync(
            string prompt,
            IReadOnlyList<ReferenceImageData> references,
            string aspectRatio,
            string apiKey,
            CancellationToken cancellationToken)
        {
            try
            {
                using (var content = new MultipartFormDataContent())
                {
                    content.Add(new StringContent(settings.Model), "model");
                    content.Add(new StringContent(prompt), "prompt");
                    content.Add(new StringContent(ResolveStandardSize(aspectRatio)), "size");
                    content.Add(new StringContent("1"), "n");
                    if (!IsGptImage()) content.Add(new StringContent("b64_json"), "response_format");
                    for (var i = 0; i < references.Count; i++)
                    {
                        var part = new ByteArrayContent(references[i].Bytes);
                        part.Headers.ContentType = new MediaTypeHeaderValue(references[i].MimeType ?? "image/png");
                        content.Add(part, references.Count == 1 ? "image" : "image[]", references[i].FileName ?? ("ref" + i + ".png"));
                    }
                    var response = await SendContentAsync(HttpMethod.Post, NormalizeOpenAiBase() + "/images/edits",
                        apiKey, content, cancellationToken);
                    if (!response.Success) return Failed(response.Error, "openai-edits");
                    var roots = ParseRoots(response.Body, out var parseError);
                    if (roots.Count == 0) return Failed(parseError, "openai-edits");
                    var extracted = await ExtractImagesAsync(roots, apiKey, "openai-edits", cancellationToken);
                    return extracted.Success ? extracted : Failed("响应中没有 b64_json/url。" + extracted.Error, "openai-edits");
                }
            }
            catch (Exception exception)
            {
                return Failed(exception.Message, "openai-edits");
            }
        }

        private async Task<ApiResponse> SendJsonAsync(
            HttpMethod method,
            string url,
            string apiKey,
            object payload,
            CancellationToken cancellationToken)
        {
            var json = payload == null ? null : JsonSerializer.Serialize(payload);
            HttpContent content = json == null ? null : new StringContent(json, Encoding.UTF8, "application/json");
            return await SendContentAsync(method, url, apiKey, content, cancellationToken);
        }

        private async Task<ApiResponse> SendContentAsync(
            HttpMethod method,
            string url,
            string apiKey,
            HttpContent content,
            CancellationToken cancellationToken)
        {
            var requestTimer = Stopwatch.StartNew();
            services?.LogTiming(traceId, "TA的相机 HTTP 请求", detail:
                method + " " + SafeUrl(url) + "｜model=" + settings.Model);
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            using (var request = new HttpRequestMessage(method, url))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                request.Content = content;
                try
                {
                    using (var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token))
                    {
                        var body = await response.Content.ReadAsStringAsync();
                        var result = new ApiResponse
                        {
                            Success = response.IsSuccessStatusCode,
                            StatusCode = (int)response.StatusCode,
                            Body = body,
                            ContentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty
                        };
                        if (!result.Success)
                            result.Error = "HTTP " + result.StatusCode + "：" + Truncate(ExtractErrorText(body), 500);
                        services?.LogTiming(traceId,
                            result.Success ? "TA的相机 HTTP 响应" : "TA的相机 API 错误",
                            requestTimer.ElapsedMilliseconds,
                            "status=" + result.StatusCode + "｜content_type=" + result.ContentType +
                            "｜body_chars=" + body.Length +
                            (result.Success ? string.Empty : "｜body=" + Truncate(ExtractErrorText(body), 500)));
                        return result;
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    services?.LogTiming(traceId, "TA的相机 HTTP 请求超时", requestTimer.ElapsedMilliseconds,
                        "url=" + SafeUrl(url) + "｜timeout=" + settings.TimeoutSeconds + "s");
                    return new ApiResponse { Error = "请求超时（" + settings.TimeoutSeconds + "秒）。" };
                }
                catch (Exception exception)
                {
                    services?.LogTiming(traceId, "TA的相机 HTTP 请求异常", requestTimer.ElapsedMilliseconds,
                        exception.GetType().Name + "：" + Truncate(exception.Message, 500));
                    return new ApiResponse { Error = exception.GetType().Name + "：" + exception.Message };
                }
            }
        }

        private async Task<ImageGenerationResult> ExtractImagesAsync(
            IEnumerable<JsonElement> roots,
            string apiKey,
            string protocol,
            CancellationToken cancellationToken)
        {
            var result = new ImageGenerationResult { Protocol = protocol };
            var urls = new List<string>();
            foreach (var root in roots)
            {
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) continue;
                var format = GetString(root, "output_format");
                var mime = format == "jpeg" ? "image/jpeg" : format == "webp" ? "image/webp" : "image/png";
                foreach (var item in data.EnumerateArray())
                {
                    var base64 = GetString(item, "b64_json");
                    if (base64.Length > 0) AddBase64(base64, mime, "b64_json", result.Images);
                    else
                    {
                        var url = GetString(item, "url");
                        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                            (uri.Scheme == "https" || uri.Scheme == "http")) urls.Add(url);
                    }
                }
            }
            foreach (var url in urls.Distinct(StringComparer.Ordinal))
            {
                var downloaded = await DownloadAsync(url, apiKey, cancellationToken);
                if (downloaded != null) result.Images.Add(downloaded);
                else result.Error += (result.Error.Length == 0 ? string.Empty : "；") + "图片 URL 下载失败";
            }
            result.Images = result.Images.Where(x => x?.Bytes != null && x.Bytes.Length > 100)
                .GroupBy(x => Convert.ToBase64String(x.Bytes.Take(32).ToArray()))
                .Select(x => x.First()).ToList();
            services?.LogTiming(traceId, "TA的相机 响应图片提取", detail:
                "protocol=" + protocol + "｜inline=" + result.Images.Count + "｜urls=" + urls.Count +
                (string.IsNullOrWhiteSpace(result.Error) ? string.Empty : "｜error=" + Truncate(result.Error, 400)));
            return result;
        }

        private static void AddBase64(string value, string mime, string source, List<GeneratedImageData> images)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            try
            {
                var clean = Regex.Replace(value, @"\s+", string.Empty);
                var bytes = Convert.FromBase64String(clean);
                if (bytes.Length > 100) images.Add(new GeneratedImageData
                {
                    Bytes = bytes, MimeType = string.IsNullOrWhiteSpace(mime) ? "image/png" : mime, Source = source
                });
            }
            catch { }
        }

        private async Task<GeneratedImageData> DownloadAsync(string url, string apiKey, CancellationToken cancellationToken)
        {
            foreach (var withAuth in new[] { false, true })
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(Math.Min(300, settings.TimeoutSeconds)));
                    request.Headers.UserAgent.ParseAdd("Mozilla/5.0 TraceSoul2/2.0");
                    if (withAuth) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                    try
                    {
                        using (var response = await http.SendAsync(request, timeout.Token))
                        {
                            if (!response.IsSuccessStatusCode) continue;
                            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                            if (bytes.Length <= 100) continue;
                            services?.LogTiming(traceId, "TA的相机 图片 URL 下载成功", detail:
                                "url=" + SafeUrl(url) + "｜bytes=" + bytes.Length + "｜auth=" + withAuth);
                            return new GeneratedImageData
                            {
                                Bytes = bytes,
                                MimeType = response.Content.Headers.ContentType?.MediaType ?? "image/png",
                                Source = url
                            };
                        }
                    }
                    catch { }
                }
            }
            return null;
        }

        private static string SafeUrl(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "(空)";
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return Truncate(value, 180);
            return uri.GetLeftPart(UriPartial.Path);
        }

        private static List<JsonElement> ParseRoots(string body, out string error)
        {
            error = string.Empty;
            var roots = new List<JsonElement>();
            if (string.IsNullOrWhiteSpace(body)) { error = "响应正文为空。"; return roots; }
            try
            {
                using (var doc = JsonDocument.Parse(body)) roots.Add(doc.RootElement.Clone());
            }
            catch (JsonException) { error = "图片接口未返回标准 JSON。"; }
            return roots;
        }
        private string NormalizeOpenAiBase()
        {
            var root = (settings.BaseUrl ?? string.Empty).Trim().TrimEnd('/');
            return Regex.IsMatch(root, @"/v\d+(?:beta\d*)?$", RegexOptions.IgnoreCase) ? root : root + "/v1";
        }

        private static string ExtractApiError(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object) return "未知上游错误。";
            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String) return error.GetString();
                if (error.ValueKind == JsonValueKind.Object)
                {
                    var message = GetString(error, "message");
                    if (message.Length > 0) return message;
                }
                return Truncate(error.ToString(), 500);
            }
            return "上游任务失败。";
        }

        private static string ExtractErrorText(string body)
        {
            try
            {
                using (var doc = JsonDocument.Parse(body)) return ExtractApiError(doc.RootElement);
            }
            catch { return body ?? string.Empty; }
        }

        private static string GetString(JsonElement root, string name)
        {
            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) &&
                   value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
        }

        private static ImageGenerationResult Failed(string error, string protocol = "configuration")
        {
            return new ImageGenerationResult { Error = error ?? "未知错误", Protocol = protocol };
        }

        private static string Truncate(string value, int max)
        {
            value = value ?? string.Empty;
            return value.Length <= max ? value : value.Substring(0, max);
        }

        private sealed class ApiResponse
        {
            public bool Success;
            public int StatusCode;
            public string Body = string.Empty;
            public string ContentType = string.Empty;
            public string Error = string.Empty;
        }
    }
}
