using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Manager;
using TraceSoul2.Plugins;
using TraceSoul2.Prompts;

namespace TraceSoul2.Logic
{
    /// <summary>
    /// 入站识图：用控制台「识图多模态」槽看她发来的图，把看见的结果写进这一拍，
    /// 心智和开口只吃这段，不再对着 [图片] 四个字脑补。
    /// QQ 入站常见是腾讯 CDN 链 + NapCat 缓存名；先问 get_image，兼容共享文件、Base64 与刷新后的 URL。
    /// </summary>
    public static class VisionLogic
    {
        public const int MaxImages = 4;
        public const int MaxBytes = 12 * 1024 * 1024;
        public const int MaxSeenChars = 400;
        public static readonly TimeSpan SeeTimeout = TimeSpan.FromSeconds(45);

        private static readonly HttpClient Http = CreateHttp();

        private static HttpClient CreateHttp()
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "image/avif,image/webp,image/*,*/*;q=0.8");
            return http;
        }

        public static bool HasInboundImages(string payloadJson)
        {
            return ReadInboundImageLocations(payloadJson).Count > 0;
        }

        public static List<string> ReadInboundImageLocations(string payloadJson)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(payloadJson)) return result;
            try
            {
                using (var doc = JsonDocument.Parse(payloadJson))
                {
                    if (!doc.RootElement.TryGetProperty("image_urls", out var items) ||
                        items.ValueKind != JsonValueKind.Array)
                        return result;
                    foreach (var item in items.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.String) continue;
                        var location = (item.GetString() ?? string.Empty).Trim();
                        if (location.Length == 0) continue;
                        if (!result.Contains(location, StringComparer.OrdinalIgnoreCase))
                            result.Add(location);
                    }
                }
            }
            catch { /* 载荷异常时按没有图处理。 */ }
            return result;
        }

        public static string AttachSeen(string content, string seen)
        {
            content = (content ?? string.Empty).TrimEnd();
            seen = (seen ?? string.Empty).Trim();
            if (seen.Length == 0) return content;
            if (content.IndexOf(CorePrompts.Vision.SeenPrefix, StringComparison.Ordinal) >= 0)
                return content;
            if (content.Length == 0) return CorePrompts.Vision.SeenPrefix + seen;
            return content + "\n" + CorePrompts.Vision.SeenPrefix + seen;
        }

        public static async Task<string> SeeInboundAsync(
            PluginEventData source,
            TracePluginServices services,
            CancellationToken cancellationToken)
        {
            var locations = ReadInboundImageLocations(source == null ? null : source.PayloadJson);
            if (locations.Count == 0) return string.Empty;

            var directory = services == null ? null : services.Providers;
            var endpoint = directory == null
                ? null
                : directory.ResolveExplicitSlot(LlmSlotNames.Multimodal);
            if (endpoint == null || string.IsNullOrWhiteSpace(endpoint.ApiKey))
                return CorePrompts.Vision.Unconfigured;

            var images = await LoadImagesAsync(locations, services, cancellationToken);
            if (images.Count == 0)
            {
                services?.LogTiming(source == null ? null : source.TraceId, "识图取图失败",
                    detail: "locations=" + locations.Count + "｜未取得图片；跨机器部署需可访问的 URL、Base64 或共享文件");
                return CorePrompts.Vision.LoadFailed;
            }

            var client = directory.CreateClient(endpoint.ProviderId, endpoint.Model, false);
            if (client == null) return CorePrompts.Vision.Unconfigured;

            var ask = CorePrompts.Vision.UserAsk(source.Content);
            var messages = new List<DeepSeekMessageData>
            {
                new DeepSeekMessageData("system", CorePrompts.Vision.System),
                new DeepSeekMessageData("user", ask) { images = images }
            };
            try
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    timeout.CancelAfter(SeeTimeout);
                    var raw = await client.CompleteTextAsync(messages, timeout.Token);
                    raw = OneLine(raw);
                    if (raw.Length == 0)
                    {
                        services?.LogTiming(source == null ? null : source.TraceId, "识图模型空回复");
                        return CorePrompts.Vision.LoadFailed;
                    }
                    return Limit(raw, MaxSeenChars);
                }
            }
            catch (Exception exception)
            {
                services?.LogTiming(source == null ? null : source.TraceId, "识图模型失败",
                    detail: exception.GetType().Name);
                return CorePrompts.Vision.LoadFailed;
            }
        }

        public static Task<List<LlmImagePartData>> LoadImagesAsync(
            IEnumerable<string> locations,
            CancellationToken cancellationToken)
        {
            return LoadImagesAsync(locations, null, cancellationToken);
        }

        public static async Task<List<LlmImagePartData>> LoadImagesAsync(
            IEnumerable<string> locations,
            TracePluginServices services,
            CancellationToken cancellationToken)
        {
            var listed = (locations ?? Enumerable.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(LoadPriority)
                .ToList();
            var result = new List<LlmImagePartData>();
            foreach (var location in listed)
            {
                if (result.Count >= MaxImages) break;
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var image = await LoadOneAsync(location, cancellationToken);
                    if (image == null && IsProtocolCacheName(location))
                        image = await LoadViaProtocolAsync(services, location, cancellationToken);
                    if (image != null) result.Add(image);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch { /* 单张失败不影响其余。 */ }
            }
            return result;
        }

        private static int LoadPriority(string location)
        {
            if (string.IsNullOrWhiteSpace(location)) return 9;
            if (location.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
                location.StartsWith("base64://", StringComparison.OrdinalIgnoreCase))
                return 0;
            if (ExistingFilePath(location) != null) return 0;
            if (IsProtocolCacheName(location)) return 1;
            return 2;
        }

        public static bool IsProtocolCacheName(string location)
        {
            if (string.IsNullOrWhiteSpace(location)) return false;
            if (location.IndexOf("://", StringComparison.Ordinal) >= 0) return false;
            if (location.IndexOfAny(new[] { '/', '\\' }) >= 0) return false;
            if (Path.IsPathRooted(location)) return false;
            return location.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
        }

        private static async Task<LlmImagePartData> LoadOneAsync(
            string location, CancellationToken cancellationToken)
        {
            if (location.StartsWith("base64://", StringComparison.OrdinalIgnoreCase))
            {
                var raw = location.Substring("base64://".Length).Trim();
                var bytes = Convert.FromBase64String(raw);
                return WrapBytes(bytes);
            }
            if (location.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return new LlmImagePartData { url = location };

            if (Uri.TryCreate(location, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                using (var response = await Http.GetAsync(uri, cancellationToken))
                {
                    if (!response.IsSuccessStatusCode) return null;
                    var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                    var mime = response.Content.Headers.ContentType == null
                        ? null
                        : response.Content.Headers.ContentType.MediaType;
                    return WrapBytes(bytes, mime);
                }
            }

            var path = ExistingFilePath(location);
            if (path == null) return null;
            var fileBytes = await File.ReadAllBytesAsync(path, cancellationToken);
            return WrapBytes(fileBytes, LlmImagePartData.GuessMime(fileBytes));
        }

        private static string ExistingFilePath(string location)
        {
            if (string.IsNullOrWhiteSpace(location)) return null;
            try
            {
                var path = location.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
                    ? new Uri(location).LocalPath
                    : location;
                return File.Exists(path) ? path : null;
            }
            catch
            {
                return null;
            }
        }

        private static async Task<LlmImagePartData> LoadViaProtocolAsync(
            TracePluginServices services,
            string file,
            CancellationToken cancellationToken)
        {
            if (services == null || string.IsNullOrWhiteSpace(file)) return null;
            var adapter = (services.PlatformAdapters ?? new List<ITracePlatformAdapter>())
                .FirstOrDefault(x => x != null &&
                    string.Equals(x.PlatformId, "builtin.onebot", StringComparison.OrdinalIgnoreCase));
            if (adapter == null)
            {
                services.LogTiming(null, "识图取图失败", detail: "没有 QQ 适配器，无法 get_image");
                return null;
            }
            foreach (var action in new[] { "get_image", "get_file" })
            {
                try
                {
                    var raw = await adapter.CallActionAsync(
                        action,
                        new Dictionary<string, object> { { "file", file } },
                        cancellationToken);
                    var resolved = ReadGetImageLocations(raw);
                    services.LogTiming(null, "识图 " + action,
                        detail: resolved.Count == 0 ? "回包没有可用图片来源" :
                            string.Join(",", resolved.Select(DescribeLocation)));
                    foreach (var location in resolved)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            var image = await LoadOneAsync(location, cancellationToken);
                            if (image != null) return image;
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                        catch { /* 单个来源无效时，继续试同一回包里的其他来源。 */ }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    services.LogTiming(null, "识图 " + action + " 失败",
                        detail: exception.GetType().Name + ": " + Truncate(exception.Message, 160));
                }
            }
            return null;
        }

        private static string DescribeLocation(string location)
        {
            if (string.IsNullOrWhiteSpace(location)) return "empty";
            if (location.StartsWith("base64://", StringComparison.OrdinalIgnoreCase) ||
                location.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return "inline-image";
            if (location.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                location.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase))
                return "local-http";
            if (location.StartsWith("https://multimedia.nt.qq.com.cn", StringComparison.OrdinalIgnoreCase) ||
                location.IndexOf("qpic.cn", StringComparison.OrdinalIgnoreCase) >= 0)
                return "qq-cdn";
            if (ExistingFilePath(location) != null) return "local-file";
            if (Path.IsPathRooted(location) || location.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                return "unreadable-file";
            return "other";
        }

        private static string Truncate(string value, int max)
        {
            value = value ?? string.Empty;
            return value.Length <= max ? value : value.Substring(0, max);
        }

        public static string ReadGetImageLocation(string json)
        {
            return ReadGetImageLocations(json).FirstOrDefault();
        }

        // 一个回包可同时含 NapCat 所在机器的路径、内联原图和 CDN URL。
        // 路径在跨容器部署时可能不存在，不能因拿到了 file 就丢弃其余来源。
        public static List<string> ReadGetImageLocations(string json)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(json)) return result;
            try
            {
                using (var document = JsonDocument.Parse(json))
                {
                    var root = document.RootElement;
                    JsonElement data;
                    if (!root.TryGetProperty("data", out data) || data.ValueKind != JsonValueKind.Object)
                        data = root;
                    foreach (var key in new[] { "file", "path", "file_path", "base64", "url" })
                    {
                        if (!data.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String)
                            continue;
                        var location = (value.GetString() ?? string.Empty).Trim();
                        if (location.Length == 0) continue;
                        if (key == "base64")
                        {
                            if (!location.StartsWith("data:", StringComparison.OrdinalIgnoreCase) &&
                                !location.StartsWith("base64://", StringComparison.OrdinalIgnoreCase))
                                location = "base64://" + location;
                        }
                        else if (IsProtocolCacheName(location)) continue;
                        if (!result.Contains(location, StringComparer.Ordinal)) result.Add(location);
                    }
                }
            }
            catch { /* 回包异常时按取图失败。 */ }
            return result.OrderBy(LoadPriority).ToList();
        }

        private static LlmImagePartData WrapBytes(byte[] bytes, string mime = null)
        {
            if (bytes == null || bytes.Length < 32 || bytes.Length > MaxBytes) return null;
            if (bytes[0] == (byte)'<' || bytes[0] == (byte)'{' || bytes[0] == (byte)'[')
                return null;
            mime = string.IsNullOrWhiteSpace(mime) ? LlmImagePartData.GuessMime(bytes) : mime.Trim();
            if (mime.IndexOf("html", StringComparison.OrdinalIgnoreCase) >= 0 ||
                mime.IndexOf("json", StringComparison.OrdinalIgnoreCase) >= 0 ||
                mime.IndexOf("text/", StringComparison.OrdinalIgnoreCase) >= 0)
                return null;
            return new LlmImagePartData { bytes = bytes, mime = mime };
        }

        private static string OneLine(string value)
        {
            value = (value ?? string.Empty).Replace("\r\n", "\n").Trim();
            while (value.IndexOf("\n\n", StringComparison.Ordinal) >= 0)
                value = value.Replace("\n\n", "\n");
            return value;
        }

        private static string Limit(string value, int max)
        {
            value = value ?? string.Empty;
            return value.Length <= max ? value : value.Substring(0, max);
        }
    }
}
