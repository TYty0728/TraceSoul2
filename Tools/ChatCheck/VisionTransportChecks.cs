using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Host;
using TraceSoul2.Logic;
using TraceSoul2.Manager;
using TraceSoul2.Plugins;
using TraceSoul2.Plugins.Builtin;
using TraceSoul2.Prompts;
using TraceSoul2.Util;

internal static partial class Program
{
    private static async Task RunVisionTransportChecksAsync()
    {
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var store = new SqliteMemoryManager(":memory:");
        foreach (var contentType in new[] { "application/octet-stream", "image/jpeg" })
        {
            using var cdn = new VisionHttpFixture(png, contentType);
            var images = await VisionLogic.LoadImagesAsync(new[] { cdn.Url }, deadline.Token);
            Require(images.Count == 1 && images[0].mime == "image/png" && images[0].bytes.SequenceEqual(png),
                "远端 PNG 即使标为二进制或错误 JPEG 类型，也应按像素签名发送 image/png");
            await cdn.Request;
        }

        var inline = "data:application/octet-stream;base64," + Convert.ToBase64String(png);
        var normalized = await VisionLogic.LoadImagesAsync(new[] { inline }, deadline.Token);
        Require(normalized.Count == 1 && normalized[0].ToDataUri().StartsWith("data:image/png;base64,"),
            "内联图片也必须规范 MIME");
        var otherPng = png.Concat(new byte[] { 0 }).ToArray();
        var uniqueImages = await VisionLogic.LoadImagesAsync(new[]
        {
            inline, "base64://" + Convert.ToBase64String(png),
            "data:image/png;base64," + Convert.ToBase64String(png),
            "data:image/jpeg;base64," + Convert.ToBase64String(png),
            "base64://" + Convert.ToBase64String(otherPng)
        }, deadline.Token);
        Require(uniqueImages.Count == 2 && uniqueImages[1].bytes.SequenceEqual(otherPng),
            "相同图片的多个来源只占一个名额，不能挤掉后续不同图片");
        using (var errorPage = new VisionHttpFixture(Encoding.UTF8.GetBytes("  <html>" + new string('x', 100)), "application/octet-stream"))
        {
            Require((await VisionLogic.LoadImagesAsync(new[] { errorPage.Url }, deadline.Token)).Count == 0,
                "CDN 错误页面不能作为 JPEG 发送");
            await errorPage.Request;
        }

        var observation = "画面是一张设备状态截图。\n" + new string('细', 410) + "底部读得清的编号是 ZX-7419，重量是 0.74 kg。";
        foreach (var native in new[] { true, false })
        {
            var reply = native
                ? JsonSerializer.Serialize(new { candidates = new[] { new { content = new { parts = new[] { new { text = observation } } }, finishReason = "STOP" } } })
                : JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = observation }, finish_reason = "stop" } } });
            using var api = new VisionHttpFixture(Encoding.UTF8.GetBytes(reply), "application/json");
            using var cdn = new VisionHttpFixture(png, "application/octet-stream");
            using var originalCdn = new VisionHttpFixture(png, "image/jpeg");
            var services = VisionServices(store, api.Url, native);
            var protocol = new FakeOneBotVisionAdapter
            {
                Response = TraceJson.ToJson(new { data = new { file = "/unavailable-napcat-cache/test.png", url = cdn.Url } })
            };
            services.PlatformAdapters.Add(protocol);
            var qq = new OneBotPlatformPlugin();
            var inbound = new OneBotPlatformAdapter(qq).ConvertInbound(TraceJson.ToJson(new
            {
                post_type = "message", self_id = 999, user_id = 123, message_type = "private", message_id = 42,
                message = new[] { new { type = "image", data = new { file = "remote-image.png", url = originalCdn.Url } } }
            }));
            inbound.TraceId = "vision-check";
            Require(VisionLogic.ReadInboundImageLocations(inbound.PayloadJson).Count == 2,
                "复现 QQ 一张图片同时给缓存名和 URL 的双来源载荷");
            var seen = await VisionLogic.SeeInboundAsync(inbound, services, deadline.Token);
            Require(seen == observation, "跨容器应通过 CDN 完成识图，保留旧 400 字上限后的文字、数值和单位");
            using var request = JsonDocument.Parse(await api.Request);
            await cdn.Request;
            await originalCdn.Request;
            if (native)
            {
                Require(request.RootElement.GetProperty("contents")[0].GetProperty("parts").GetArrayLength() == 2,
                    "同一 QQ 图片的缓存名和 CDN URL 不能重复发送给 Gemini 原生接口");
                var part = request.RootElement.GetProperty("contents")[0].GetProperty("parts")[1].GetProperty("inline_data");
                Require(part.GetProperty("mime_type").GetString() == "image/png" &&
                        part.GetProperty("data").GetString() == Convert.ToBase64String(png),
                    "Gemini 原生请求应包含准确的 MIME 和图片数据");
            }
            else
            {
                Require(request.RootElement.GetProperty("messages")[1].GetProperty("content").GetArrayLength() == 2,
                    "同一 QQ 图片的缓存名和 CDN URL 不能重复发送给 OpenAI 兼容中转");
                var image = request.RootElement.GetProperty("messages")[1].GetProperty("content")[1].GetProperty("image_url");
                Require(image.GetProperty("url").GetString() == "data:image/png;base64," + Convert.ToBase64String(png),
                    "OpenAI 兼容请求应包含准确的图片 Data URI");
                var system = request.RootElement.GetProperty("messages")[0].GetProperty("content").GetString();
                Require(system.Contains("关键文字、数值和单位") && system.Contains("确实可见的人物动作") &&
                    system.Contains("不必判断发图的人本人在做什么") && !system.Contains("是否能判断她在吃"),
                    "视觉提示优先保留看得见的细节和原文，活动只有画面可见时才记录");
            }
        }

        var twoPictures = "图 1：设备状态。" + new string('甲', 380) + "\n图 2：货物标签。" + new string('乙', 380) + "\n编号 ZX-7419。";
        var twoPictureReply = JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = twoPictures }, finish_reason = "stop" } } });
        using (var api = new VisionHttpFixture(Encoding.UTF8.GetBytes(twoPictureReply), "application/json"))
        {
            var seen = await VisionLogic.SeeInboundAsync(new PluginEventData
            {
                Content = "原消息中的附文", TraceId = "vision-multiple",
                PayloadJson = TraceJson.ToJson(new { image_urls = new[] { inline, inline, "base64://" + Convert.ToBase64String(otherPng) } })
            }, VisionServices(store, api.Url, false), deadline.Token);
            using var request = JsonDocument.Parse(await api.Request);
            var content = request.RootElement.GetProperty("messages")[1].GetProperty("content");
            Require(seen == twoPictures && content.GetArrayLength() == 3 &&
                content[0].GetProperty("text").GetString().Contains("最多 1000 字"),
                "按实际送入的图片数量给描写空间，逐图观察及末尾细节全部保留");
        }

        foreach (var native in new[] { true, false })
        {
            var error = TraceJson.ToJson(new { error = new { message =
                "Unsupported MIME type; key=vision-test-secret; https://example.test/?token=private-token " +
                "data:image/png;base64," + Convert.ToBase64String(png) } });
            using var api = new VisionHttpFixture(Encoding.UTF8.GetBytes(error), "application/json", 400);
            var services = VisionServices(store, api.Url, native);
            var logs = new List<string>();
            services.TimingLog = logs.Add;
            var seen = await VisionLogic.SeeInboundAsync(new PluginEventData
            {
                Content = "看图", TraceId = "vision-failure",
                PayloadJson = TraceJson.ToJson(new { image_urls = new[] { inline } })
            }, services, deadline.Token);
            await api.Request;
            var failure = logs.Single(x => x.Contains("识图模型失败"));
            Require(seen == CorePrompts.Vision.LoadFailed && failure.Contains("400") && failure.Contains("Unsupported MIME"),
                "识图失败日志必须保留供应商状态码和具体原因");
            Require(!failure.Contains("vision-test-secret") && !failure.Contains("private-token") && !failure.Contains("iVBORw0"),
                "识图失败日志不得泄漏 API Key、签名 URL 或图片 Base64");
        }
    }

    private static TracePluginServices VisionServices(IMemoryStore store, string url, bool native)
    {
        var config = new DeepSeekConfigData
        {
            ProviderId = "vision-test", Model = "vision-test", BaseUrl = url, ApiKey = "vision-test-secret",
            MaxTokens = 512, Temperature = .7f, TopP = 1f, TimeoutSeconds = 5,
            EmptyContentRetries = 0, TransientErrorRetries = 0
        };
        return new TracePluginServices(store, new HierarchicalVectorRouterLogic(new FakeEncoder()))
        {
            Providers = new FakeVisionDirectory
            {
                Client = native ? (ILlmClient)new GeminiClientManager(config) : new DeepSeekClientManager(config),
                Endpoint = new LlmEndpointData { ProviderId = config.ProviderId, Model = config.Model, ApiKey = config.ApiKey }
            }
        };
    }

    // 仅在回环地址模拟 CDN/供应商，验证实际 HTTP 与序列化，不联系真实服务。
    private sealed class VisionHttpFixture : IDisposable
    {
        private readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        public string Url { get; }
        public Task<string> Request { get; }

        public VisionHttpFixture(byte[] body, string mime, int status = 200)
        {
            listener.Start();
            Url = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
            Request = ServeAsync(body, mime, status);
        }

        private async Task<string> ServeAsync(byte[] body, string mime, int status)
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = client.GetStream();
            var header = new StringBuilder();
            var one = new byte[1];
            while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                await stream.ReadExactlyAsync(one, timeout.Token);
                header.Append((char)one[0]);
                if (header.Length > 32768) throw new InvalidOperationException("HTTP header too large");
            }
            var length = header.ToString().Split('\n').FirstOrDefault(x => x.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
            var bytes = new byte[length == null ? 0 : int.Parse(length.Split(':')[1].Trim())];
            await stream.ReadExactlyAsync(bytes, timeout.Token);
            var response = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + " Test\r\nContent-Type: " + mime +
                "\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(response, timeout.Token);
            await stream.WriteAsync(body, timeout.Token);
            return Encoding.UTF8.GetString(bytes);
        }

        public void Dispose()
        {
            timeout.Cancel();
            listener.Stop();
            timeout.Dispose();
        }
    }
}
