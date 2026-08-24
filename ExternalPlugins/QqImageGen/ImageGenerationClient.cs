using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
        public string ApiFormat = "auto";
        public string ApiMode = "auto";
        public string Proxy;
        public string SafetySettings = "BLOCK_NONE";
        public string ImageSize = "2K";
        public string StandardSize = "1024x1024";
        public int TimeoutSeconds = 600;
        public int MaxRetries = 3;
        public int PollIntervalSeconds = 5;
        public bool EnableThinking;
        public bool LogRequestBody;
        public bool Debug;
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
        private static readonly Regex VersionSuffix = new Regex(@"/v\d+(?:beta\d*)?/?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex DataUriRegex = new Regex(
            @"data:image/(?<mime>png|jpeg|jpg|webp|gif);base64,(?<data>[A-Za-z0-9+/=\s]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex MarkdownUrlRegex = new Regex(
            @"!\[[^\]]*\]\((?<url>https?://[^\s\)]+)\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex HttpUrlRegex = new Regex(
            @"https?://[^\s\)\]\""']{20,}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly ImageGenerationSettings settings;
        private readonly TracePluginServices services;
        private readonly string traceId;
        private readonly HttpClient http;

        public ImageGenerationClient(ImageGenerationSettings settings, TracePluginServices services, string traceId)
        {
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
            this.services = services;
            this.traceId = traceId;
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

            var errors = new List<string>();
            var attempts = Math.Max(1, Math.Min(10, settings.MaxRetries));
            services?.LogTiming(traceId, "TA的相机 上游生成开始", detail:
                "model=" + settings.Model + "｜format=" + (IsGemini() ? "gemini" : "openai") +
                "｜modes=" + string.Join(",", IsGemini() ? new[] { "native" } : ResolveOpenAiModes()) +
                "｜refs=" + (references?.Count ?? 0) + "｜aspect=" + (aspectRatio ?? string.Empty));
            for (var attempt = 0; attempt < attempts; attempt++)
            {
                var key = keys[attempt % keys.Count];
                services?.LogTiming(traceId, "TA的相机 重试生成", detail:
                    "attempt=" + (attempt + 1) + "/" + attempts + "｜key_index=" + (attempt % keys.Count));
                var timer = Stopwatch.StartNew();
                var result = await GenerateOnceAsync(prompt, references ?? new List<ReferenceImageData>(),
                    aspectRatio, key, cancellationToken);
                services?.LogTiming(traceId, result.Success ? "生图上游完成" : "生图上游未完成",
                    timer.ElapsedMilliseconds,
                    "protocol=" + result.Protocol + "｜attempt=" + (attempt + 1) + "/" + attempts +
                    (result.Success ? "｜images=" + result.Images.Count : "｜" + Truncate(result.Error, 400)));
                if (result.Success) return result;
                errors.Add(result.Protocol + "：" + result.Error);
                if (attempt < attempts - 1)
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, 1 << Math.Min(attempt, 3))), cancellationToken);
            }
            return Failed("重试失败：" + string.Join("；", errors.Distinct()));
        }

        private async Task<ImageGenerationResult> GenerateOnceAsync(
            string prompt,
            IReadOnlyList<ReferenceImageData> references,
            string aspectRatio,
            string apiKey,
            CancellationToken cancellationToken)
        {
            if (IsGemini()) return await GenerateGeminiAsync(prompt, references, aspectRatio, apiKey, cancellationToken);

            var modes = ResolveOpenAiModes();
            var errors = new List<string>();
            foreach (var mode in modes)
            {
                ImageGenerationResult result;
                if (mode == "async")
                    result = await GenerateOpenAiAsyncTaskAsync(prompt, references, aspectRatio, apiKey, cancellationToken);
                else if (mode == "sync")
                    result = await GenerateOpenAiChatAsync(prompt, references, apiKey, cancellationToken);
                else
                    result = await GenerateOpenAiStandardAsync(prompt, references, apiKey, cancellationToken);
                if (result.Success) return result;
                errors.Add(result.Protocol + "：" + result.Error);
            }
            return new ImageGenerationResult
            {
                Protocol = "openai-fallbacks",
                Error = string.Join("；", errors)
            };
        }

        private List<string> ResolveOpenAiModes()
        {
            var configured = (settings.ApiMode ?? "auto").Trim().ToLowerInvariant();
            if (configured == "async" || configured == "sync" || configured == "standard")
                return new List<string> { configured };
            var model = (settings.Model ?? string.Empty).ToLowerInvariant();
            if (model.Contains("gpt-image") || model.Contains("生图"))
                return new List<string> { "async", "standard", "sync" };
            return new List<string> { "standard", "sync", "async" };
        }

        private bool IsGemini()
        {
            var format = (settings.ApiFormat ?? "auto").Trim().ToLowerInvariant();
            if (format == "gemini") return true;
            if (format == "openai") return false;
            return (settings.Model ?? string.Empty).IndexOf("gemini", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private async Task<ImageGenerationResult> GenerateOpenAiAsyncTaskAsync(
            string prompt,
            IReadOnlyList<ReferenceImageData> references,
            string aspectRatio,
            string apiKey,
            CancellationToken cancellationToken)
        {
            var payload = new Dictionary<string, object>
            {
                { "model", settings.Model }, { "prompt", prompt }
            };
            if (!string.IsNullOrWhiteSpace(aspectRatio)) payload["aspect_ratio"] = aspectRatio;
            if (references.Count > 0)
                payload["images"] = references.Select(ToDataUri).ToList();
            var url = CleanBase() + "/v1/videos";
            var response = await SendJsonAsync(HttpMethod.Post, url, apiKey, payload, false, cancellationToken);
            if (!response.Success) return Failed(response.Error, "openai-async");
            var roots = ParseRoots(response.Body, response.ContentType, out var parseError);
            if (roots.Count == 0) return Failed(parseError, "openai-async");
            var root = roots.Last();
            var status = GetString(root, "status");
            if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
                return Failed(ExtractApiError(root), "openai-async");
            if (string.Equals(status, "queued", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "in_progress", StringComparison.OrdinalIgnoreCase))
            {
                var taskId = GetString(root, "id");
                if (string.IsNullOrWhiteSpace(taskId)) return Failed("任务响应没有 id。", "openai-async");
                services?.LogTiming(traceId, "TA的相机 异步任务开始轮询", detail:
                    "task=" + taskId + "｜status=" + status);
                return await PollOpenAiTaskAsync(taskId, apiKey, cancellationToken);
            }
            var extracted = await ExtractImagesAsync(roots, apiKey, "openai-async", cancellationToken);
            return extracted.Success ? extracted : Failed("任务响应中没有图片。" + extracted.Error, "openai-async");
        }

        private async Task<ImageGenerationResult> PollOpenAiTaskAsync(
            string taskId,
            string apiKey,
            CancellationToken cancellationToken)
        {
            var started = Stopwatch.StartNew();
            var url = CleanBase() + "/v1/videos/" + Uri.EscapeDataString(taskId);
            var interval = Math.Max(2, Math.Min(30, settings.PollIntervalSeconds));
            string lastStatus = string.Empty;
            while (started.Elapsed < TimeSpan.FromSeconds(settings.TimeoutSeconds))
            {
                var response = await SendJsonAsync(HttpMethod.Get, url, apiKey, null, false, cancellationToken);
                if (!response.Success)
                {
                    lastStatus = response.Error;
                    await Task.Delay(TimeSpan.FromSeconds(interval), cancellationToken);
                    continue;
                }
                var roots = ParseRoots(response.Body, response.ContentType, out var parseError);
                if (roots.Count == 0)
                {
                    lastStatus = parseError;
                    await Task.Delay(TimeSpan.FromSeconds(interval), cancellationToken);
                    continue;
                }
                var root = roots.Last();
                var status = GetString(root, "status");
                var progress = GetNumberText(root, "progress");
                services?.LogTiming(traceId, "TA的相机 异步任务轮询", detail:
                    "task=" + taskId + "｜status=" + status + "｜progress=" + progress +
                    "｜waited=" + (int)started.Elapsed.TotalSeconds + "s");
                if (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
                {
                    services?.LogTiming(traceId, "TA的相机 异步任务完成", detail:
                        "task=" + taskId + "｜waited=" + (int)started.Elapsed.TotalSeconds + "s");
                    var extracted = await ExtractImagesAsync(roots, apiKey, "openai-async", cancellationToken);
                    return extracted.Success ? extracted : Failed("任务完成但没有图片：" + extracted.Error, "openai-async");
                }
                if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
                    return Failed(ExtractApiError(root), "openai-async");
                lastStatus = status;
                var dynamicInterval = started.Elapsed < TimeSpan.FromMinutes(1) ? interval : interval * 2;
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, dynamicInterval)), cancellationToken);
            }
            return Failed("任务轮询超时（" + settings.TimeoutSeconds + "秒，最后状态=" + lastStatus + "）。",
                "openai-async");
        }

        private async Task<ImageGenerationResult> GenerateOpenAiStandardAsync(
            string prompt,
            IReadOnlyList<ReferenceImageData> references,
            string apiKey,
            CancellationToken cancellationToken)
        {
            if (references.Count > 0)
            {
                var edit = await GenerateOpenAiEditAsync(prompt, references, apiKey, cancellationToken);
                if (edit.Success) return edit;
            }
            var payload = new Dictionary<string, object>
            {
                { "model", settings.Model }, { "prompt", prompt }, { "n", 1 },
                { "size", settings.StandardSize }, { "response_format", "b64_json" }
            };
            var response = await SendJsonAsync(HttpMethod.Post, NormalizeOpenAiBase() + "/images/generations",
                apiKey, payload, false, cancellationToken);
            if (!response.Success) return Failed(response.Error, "openai-images");
            var roots = ParseRoots(response.Body, response.ContentType, out var parseError);
            if (roots.Count == 0) return Failed(parseError, "openai-images");
            var extracted = await ExtractImagesAsync(roots, apiKey, "openai-images", cancellationToken);
            return extracted.Success ? extracted : Failed("响应中没有 b64_json/url。" + extracted.Error, "openai-images");
        }

        private async Task<ImageGenerationResult> GenerateOpenAiEditAsync(
            string prompt,
            IReadOnlyList<ReferenceImageData> references,
            string apiKey,
            CancellationToken cancellationToken)
        {
            try
            {
                using (var content = new MultipartFormDataContent())
                {
                    content.Add(new StringContent(settings.Model), "model");
                    content.Add(new StringContent(prompt), "prompt");
                    content.Add(new StringContent(settings.StandardSize), "size");
                    content.Add(new StringContent("b64_json"), "response_format");
                    for (var i = 0; i < references.Count; i++)
                    {
                        var part = new ByteArrayContent(references[i].Bytes);
                        part.Headers.ContentType = new MediaTypeHeaderValue(references[i].MimeType ?? "image/png");
                        content.Add(part, i == 0 ? "image" : "image[]", references[i].FileName ?? ("ref" + i + ".png"));
                    }
                    var response = await SendContentAsync(HttpMethod.Post, NormalizeOpenAiBase() + "/images/edits",
                        apiKey, content, false, cancellationToken);
                    if (!response.Success) return Failed(response.Error, "openai-edits");
                    var roots = ParseRoots(response.Body, response.ContentType, out var parseError);
                    if (roots.Count == 0) return Failed(parseError, "openai-edits");
                    return await ExtractImagesAsync(roots, apiKey, "openai-edits", cancellationToken);
                }
            }
            catch (Exception exception)
            {
                return Failed(exception.Message, "openai-edits");
            }
        }

        private async Task<ImageGenerationResult> GenerateOpenAiChatAsync(
            string prompt,
            IReadOnlyList<ReferenceImageData> references,
            string apiKey,
            CancellationToken cancellationToken)
        {
            var parts = new List<object> { new { type = "text", text = prompt } };
            for (var i = 0; i < references.Count; i++)
            {
                parts.Add(new { type = "text", text = ReferenceLabel(references[i], i) });
                parts.Add(new
                {
                    type = "image_url", image_url = new { url = ToDataUri(references[i]) }
                });
            }
            var payload = new
            {
                model = settings.Model,
                messages = new[] { new { role = "user", content = parts } }
            };
            var response = await SendJsonAsync(HttpMethod.Post, NormalizeOpenAiBase() + "/chat/completions",
                apiKey, payload, false, cancellationToken);
            if (!response.Success) return Failed(response.Error, "openai-chat");
            var roots = ParseRoots(response.Body, response.ContentType, out var parseError);
            if (roots.Count == 0) return Failed(parseError, "openai-chat");
            var extracted = await ExtractImagesAsync(roots, apiKey, "openai-chat", cancellationToken);
            return extracted.Success ? extracted : Failed("聊天响应中没有图片。" + extracted.Error, "openai-chat");
        }

        private async Task<ImageGenerationResult> GenerateGeminiAsync(
            string prompt,
            IReadOnlyList<ReferenceImageData> references,
            string aspectRatio,
            string apiKey,
            CancellationToken cancellationToken)
        {
            var parts = new List<object> { new { text = prompt } };
            for (var i = 0; i < references.Count; i++)
            {
                parts.Add(new { text = ReferenceLabel(references[i], i) });
                parts.Add(new
                {
                    inline_data = new
                    {
                        mime_type = references[i].MimeType ?? "image/png",
                        data = Convert.ToBase64String(references[i].Bytes)
                    }
                });
            }
            var imageConfig = new Dictionary<string, object>();
            if (!string.IsNullOrWhiteSpace(aspectRatio)) imageConfig["aspectRatio"] = aspectRatio;
            if (!string.IsNullOrWhiteSpace(settings.ImageSize) &&
                settings.Model.IndexOf("gemini-3", StringComparison.OrdinalIgnoreCase) >= 0)
                imageConfig["imageSize"] = settings.ImageSize;
            var generationConfig = new Dictionary<string, object>
            {
                { "responseModalities", new[] { "TEXT", "IMAGE" } }
            };
            if (imageConfig.Count > 0) generationConfig["imageConfig"] = imageConfig;
            if (settings.EnableThinking) generationConfig["thinkingConfig"] = new { thinkingBudget = 8192 };
            var safety = new List<object>();
            foreach (var category in new[]
                     {
                         "HARM_CATEGORY_HARASSMENT", "HARM_CATEGORY_HATE_SPEECH",
                         "HARM_CATEGORY_SEXUALLY_EXPLICIT", "HARM_CATEGORY_DANGEROUS_CONTENT",
                         "HARM_CATEGORY_CIVIC_INTEGRITY"
                     })
                safety.Add(new { category, threshold = settings.SafetySettings });
            var payload = new
            {
                contents = new[] { new { parts } },
                generationConfig,
                safetySettings = safety
            };
            var url = CleanBase() + "/v1beta/models/" + Uri.EscapeDataString(settings.Model) + ":generateContent";
            var response = await SendJsonAsync(HttpMethod.Post, url, apiKey, payload, true, cancellationToken);
            if (!response.Success) return Failed(response.Error, "gemini-native");
            var roots = ParseRoots(response.Body, response.ContentType, out var parseError);
            if (roots.Count == 0) return Failed(parseError, "gemini-native");
            var block = FindSafetyBlock(roots);
            if (block.Length > 0) return Failed("安全审核拦截：" + block, "gemini-native");
            var extracted = await ExtractImagesAsync(roots, apiKey, "gemini-native", cancellationToken);
            return extracted.Success ? extracted : Failed("响应中没有 inlineData：" + extracted.Error, "gemini-native");
        }

        private async Task<ApiResponse> SendJsonAsync(
            HttpMethod method,
            string url,
            string apiKey,
            object payload,
            bool googleKey,
            CancellationToken cancellationToken)
        {
            var json = payload == null ? null : JsonSerializer.Serialize(payload);
            if (settings.LogRequestBody && json != null)
                services?.LogTiming(traceId, "TA的相机 本次请求体(base64已截断)", detail:
                    SanitizeJsonForLog(json));
            HttpContent content = json == null ? null : new StringContent(json, Encoding.UTF8, "application/json");
            return await SendContentAsync(method, url, apiKey, content, googleKey, cancellationToken);
        }

        private async Task<ApiResponse> SendContentAsync(
            HttpMethod method,
            string url,
            string apiKey,
            HttpContent content,
            bool googleKey,
            CancellationToken cancellationToken)
        {
            var requestTimer = Stopwatch.StartNew();
            services?.LogTiming(traceId, "TA的相机 HTTP 请求", detail:
                method + " " + SafeUrl(url) + "｜model=" + settings.Model);
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            using (var request = new HttpRequestMessage(method, url))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
                if (googleKey) request.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey);
                else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
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
            foreach (var root in roots) Walk(root, string.Empty, result.Images, urls);
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

        private void Walk(JsonElement element, string propertyName,
            List<GeneratedImageData> images, List<string> urls)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                if (TryInlineImage(element, images)) return;
                foreach (var property in element.EnumerateObject())
                {
                    var name = property.Name;
                    if (name.Equals("b64_json", StringComparison.OrdinalIgnoreCase) &&
                        property.Value.ValueKind == JsonValueKind.String)
                    {
                        AddBase64(property.Value.GetString(), "image/png", "b64_json", images);
                        continue;
                    }
                    if ((name.Equals("url", StringComparison.OrdinalIgnoreCase) ||
                         name.Equals("image_url", StringComparison.OrdinalIgnoreCase)) &&
                        property.Value.ValueKind == JsonValueKind.String)
                    {
                        AddUrlOrData(property.Value.GetString(), urls, images);
                        continue;
                    }
                    Walk(property.Value, name, images, urls);
                }
                return;
            }
            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray()) Walk(item, propertyName, images, urls);
                return;
            }
            if (element.ValueKind == JsonValueKind.String &&
                (propertyName.Equals("content", StringComparison.OrdinalIgnoreCase) ||
                 propertyName.Equals("text", StringComparison.OrdinalIgnoreCase) ||
                 propertyName.Equals("output", StringComparison.OrdinalIgnoreCase)))
                ExtractFromText(element.GetString(), images, urls);
        }

        private static bool TryInlineImage(JsonElement element, List<GeneratedImageData> images)
        {
            JsonElement inline;
            if (!(element.TryGetProperty("inline_data", out inline) || element.TryGetProperty("inlineData", out inline)) ||
                inline.ValueKind != JsonValueKind.Object) return false;
            var mime = GetString(inline, "mime_type");
            if (mime.Length == 0) mime = GetString(inline, "mimeType");
            var data = GetString(inline, "data");
            AddBase64(data, mime.Length == 0 ? "image/png" : mime, "inlineData", images);
            return data.Length > 0;
        }

        private static void ExtractFromText(string text, List<GeneratedImageData> images, List<string> urls)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            foreach (Match match in DataUriRegex.Matches(text))
                AddBase64(match.Groups["data"].Value, "image/" + match.Groups["mime"].Value.Replace("jpg", "jpeg"),
                    "data-uri", images);
            foreach (Match match in MarkdownUrlRegex.Matches(text)) urls.Add(match.Groups["url"].Value);
            if (urls.Count == 0)
                foreach (Match match in HttpUrlRegex.Matches(text))
                {
                    var url = match.Value.TrimEnd('.', ',', '，', '。');
                    if (!url.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
                        !url.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) urls.Add(url);
                }
        }

        private static void AddUrlOrData(string value, List<string> urls, List<GeneratedImageData> images)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (value.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
                ExtractFromText(value, images, urls);
            else if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                     (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)) urls.Add(value);
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

        private static string SanitizeJsonForLog(string json)
        {
            json = json ?? string.Empty;
            return Regex.Replace(json, "\"(?<value>[^\"\\\\]{200,})\"", match =>
            {
                var value = match.Groups["value"].Value;
                return "\"" + value.Substring(0, 80) + "...<截断，总长度" + value.Length + ">..." +
                       value.Substring(value.Length - 20) + "\"";
            });
        }

        private static string SafeUrl(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "(空)";
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return Truncate(value, 180);
            return uri.GetLeftPart(UriPartial.Path);
        }

        private static List<JsonElement> ParseRoots(string body, string contentType, out string error)
        {
            error = string.Empty;
            var roots = new List<JsonElement>();
            body = (body ?? string.Empty).Trim();
            if (body.Length == 0) { error = "响应正文为空。"; return roots; }
            var isStream = (contentType ?? string.Empty).IndexOf("event-stream", StringComparison.OrdinalIgnoreCase) >= 0 ||
                           body.StartsWith("data:", StringComparison.OrdinalIgnoreCase);
            if (!isStream)
            {
                try
                {
                    using (var doc = JsonDocument.Parse(body)) roots.Add(doc.RootElement.Clone());
                    return roots;
                }
                catch (Exception exception)
                {
                    error = "JSON 解析失败：" + exception.Message + "｜body=" + Truncate(body, 300);
                    return roots;
                }
            }
            foreach (var line in body.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
                var json = trimmed.Substring(5).Trim();
                if (json.Length == 0 || json == "[DONE]") continue;
                try { using (var doc = JsonDocument.Parse(json)) roots.Add(doc.RootElement.Clone()); }
                catch { }
            }
            if (roots.Count == 0) error = "SSE 响应没有可解析的 JSON 事件。";
            return roots;
        }

        private string CleanBase()
        {
            return VersionSuffix.Replace((settings.BaseUrl ?? string.Empty).Trim().TrimEnd('/'), string.Empty);
        }

        private string NormalizeOpenAiBase()
        {
            var root = (settings.BaseUrl ?? string.Empty).Trim().TrimEnd('/');
            return Regex.IsMatch(root, @"/v\d+(?:beta\d*)?$", RegexOptions.IgnoreCase) ? root : root + "/v1";
        }

        private static string ToDataUri(ReferenceImageData image)
        {
            return "data:" + (image.MimeType ?? "image/png") + ";base64," + Convert.ToBase64String(image.Bytes);
        }

        private static string ReferenceLabel(ReferenceImageData image, int index)
        {
            return "[参考图" + (index + 1) + "｜分类=" + (image.Category ?? "未分类") +
                   "｜用途=" + (image.Role ?? "辅助") + "]";
        }

        private static string FindSafetyBlock(IEnumerable<JsonElement> roots)
        {
            foreach (var root in roots)
            {
                if (root.ValueKind != JsonValueKind.Object) continue;
                if (root.TryGetProperty("promptFeedback", out var feedback) && feedback.ValueKind == JsonValueKind.Object)
                {
                    var reason = GetString(feedback, "blockReason");
                    if (reason.Length > 0) return reason;
                }
                if (root.TryGetProperty("candidates", out var candidates) && candidates.ValueKind == JsonValueKind.Array &&
                    candidates.GetArrayLength() > 0)
                {
                    var reason = GetString(candidates[0], "finishReason");
                    if (reason == "SAFETY" || reason == "BLOCKED" || reason == "PROHIBITED_CONTENT") return reason;
                }
            }
            return string.Empty;
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

        private static string GetNumberText(JsonElement root, string name)
        {
            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value)
                ? value.ToString() : string.Empty;
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
