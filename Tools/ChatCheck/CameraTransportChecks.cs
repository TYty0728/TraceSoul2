using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.ExternalPlugins;

internal static partial class Program
{
    private static async Task RunCameraTransportChecksAsync()
    {
        var references = new List<ReferenceImageData> {
            new() { Category = "人物", Role = "角色", FileName = "face.png", MimeType = "image/png", Bytes = new byte[] { 1, 2, 3 } },
            new() { Category = "人物", Role = "角色", FileName = "side.webp", MimeType = "image/webp", Bytes = new byte[] { 4, 5, 6 } },
            new() { Category = "外套", Role = "服饰", FileName = "coat.jpg", MimeType = "image/jpeg", Bytes = new byte[] { 7, 8, 9 } }
        };
        var plugin = new QqImageGenPlugin();
        typeof(QqImageGenPlugin).GetField("characterDetails", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(plugin, "安静疏离，神情自然");
        typeof(QqImageGenPlugin).GetField("stylePrompt", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(plugin, "柔和冷色调");
        var prompt = (string)typeof(QqImageGenPlugin).GetMethod("BuildPrompt", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(plugin, new object[] { "在窗边自拍", "selfie", references, "3:4" });
        Require(prompt.Contains(QqImageGenPrompts.LockFace) && prompt.Contains("安静疏离，神情自然") &&
            prompt.Contains("柔和冷色调") && prompt.Contains("图1、图2=分类「人物」/用途「角色」") &&
            prompt.Contains("图3=分类「外套」/用途「服饰」") && prompt.Contains(QqImageGenPrompts.ReferenceFusionRules.Trim()),
            "通用传输保留锁脸、全部角色视角、服饰分工、气质、画风和参考图编号");

        foreach (var model in new[] { "gpt-image-2", "gpt-image-2.5", "gpt-image-2.5-sunburst", "gpt-image-2.5-flare", "vendor/gpt-image-2" })
        {
            var settings = CameraSettings(model);
            using var handler = new CameraHttpProbe();
            using var client = new ImageGenerationClient(settings, null, null, handler);
            var generated = await client.GenerateAsync(prompt, references, "3:4", default);
            Require(generated.Success && generated.Images[0].Bytes.SequenceEqual(CameraHttpProbe.OutputImage), "标准 base64 响应落入图片结果");
            Require(handler.Paths.SequenceEqual(new[] { "/v1/images/edits" }), "GPT Image 首次直接请求标准 edits");
            Require(handler.Fields["model"] == model && handler.Fields["prompt"] == prompt &&
                handler.Fields["size"] == "1152x1536" && handler.Fields["n"] == "1" &&
                !handler.Fields.ContainsKey("response_format") && !handler.Fields.ContainsKey("input_fidelity"),
                "模型名与完整提示原样传递，自拍保持竖图且无不兼容参数");
            Require(handler.Files.Count == references.Count && handler.Files.All(x => x.Name == "image[]"), "多参考图统一 image[] 上传");
            for (var i = 0; i < references.Count; i++)
                Require(handler.Files[i].Bytes.SequenceEqual(references[i].Bytes) && handler.Files[i].Mime == references[i].MimeType,
                    "参考图顺序、内容、MIME 与提示编号一致");
        }

        foreach (var fixture in new[] {
            (Model: "gpt-image-2", Ratio: "4:3", Expected: "1536x1152"),
            (Model: "gpt-image-2.5", Ratio: "16:9", Expected: "1536x864"),
            (Model: "gpt-image-2", Ratio: "", Expected: "auto"),
            (Model: "gpt-image-2", Ratio: "1:1", Expected: "1024x1024"),
            (Model: "gpt-image-2", Ratio: "0:4", Expected: "auto"),
            (Model: "gpt-image-2", Ratio: "1:4", Expected: "auto"),
            (Model: "gpt-image-1.5", Ratio: "3:4", Expected: "1024x1536"),
            (Model: "chatgpt-image-latest", Ratio: "", Expected: "auto"),
            (Model: "dall-e-3", Ratio: "", Expected: "1024x1024") })
        {
            var settings = CameraSettings(fixture.Model);
            using var handler = new CameraHttpProbe();
            using var client = new ImageGenerationClient(settings, null, null, handler);
            var result = await client.GenerateAsync(prompt, null, fixture.Ratio, default);
            using var json = JsonDocument.Parse(handler.Json);
            Require(result.Success && handler.Paths.SequenceEqual(new[] { "/v1/images/generations" }) &&
                json.RootElement.GetProperty("size").GetString() == fixture.Expected &&
                json.RootElement.GetProperty("model").GetString() == fixture.Model &&
                json.RootElement.GetProperty("prompt").GetString() == prompt &&
                json.RootElement.TryGetProperty("response_format", out _) == (fixture.Model == "dall-e-3"),
                "无参考图使用标准 generations；自动尺寸和旧模型参数正确");
        }

        using (var handler = new CameraHttpProbe())
        using (var client = new ImageGenerationClient(CameraSettings("gpt-image-2"), null, null, handler))
        {
            var result = await client.GenerateAsync(prompt, references.Take(1).ToList(), "3:4", default);
            Require(result.Success && handler.Files.Single().Name == "image", "单参考图使用 image");
        }
        foreach (var status in new[] { 400, 401, 403, 404, 405, 429, 500, 503 })
        {
            using var handler = new CameraHttpProbe { Status = status };
            using var client = new ImageGenerationClient(CameraSettings("gpt-image-2"), null, null, handler);
            var result = await client.GenerateAsync(prompt, references, "3:4", default);
            Require(!result.Success && handler.Paths.SequenceEqual(new[] { "/v1/images/edits" }),
                "GPT Image 错误不转 videos、不重开生成、不丢失参考图退成纯文生图：" + status);
        }
        foreach (var failure in new[] { "empty", "timeout", "network" })
        {
            using var handler = new CameraHttpProbe { Failure = failure };
            using var client = new ImageGenerationClient(CameraSettings("gpt-image-2.5"), null, null, handler);
            var result = await client.GenerateAsync(prompt, references, "3:4", default);
            Require(!result.Success && handler.Paths.Count == 1, "空响应或传输失败不重复生图：" + failure);
        }
        using (var handler = new CameraHttpProbe { Failure = "url" })
        using (var client = new ImageGenerationClient(CameraSettings("gpt-image-2"), null, null, handler))
        {
            var result = await client.GenerateAsync(prompt, references, "3:4", default);
            Require(result.Success && result.Images[0].Bytes.SequenceEqual(CameraHttpProbe.OutputImage) &&
                handler.Paths.SequenceEqual(new[] { "/v1/images/edits", "/output.png" }),
                "兼容中转返回 URL 后下载图片");
        }
        // The standard-only path also applies to provider aliases.
        using (var handler = new CameraHttpProbe { Status = 404 })
        using (var client = new ImageGenerationClient(CameraSettings("compatible-image"), null, null, handler))
        {
            await client.GenerateAsync(prompt, references, "3:4", default);
            Require(handler.Paths.SequenceEqual(new[] { "/v1/images/edits" }), "通用模型也只调用一次标准接口");
        }
        foreach (var mode in new[] { "async", "sync", "standard" })
        {
            var path = Path.Combine(Path.GetTempPath(), "camera-config-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                File.WriteAllText(path, JsonSerializer.Serialize(new {
                    model = "gpt-image-2", base_url = "https://camera.invalid/v1", api_key = "test-key",
                    api_mode = mode, image_api_format = "gemini", standard_size = "1024x1024",
                    poll_interval = 30, max_retry_attempts = 10, enable_thinking = true,
                    character_details = "安静疏离，神情自然", style_prompt = "柔和冷色调"
                }));
                var migrated = new QqImageGenPlugin();
                typeof(QqImageGenPlugin).GetMethod("ApplyConfig", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(migrated, new object[] { path });
                var settings = (ImageGenerationSettings)typeof(QqImageGenPlugin)
                    .GetMethod("ResolveSettings", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(migrated, new object[] { null });
                var migratedPrompt = (string)typeof(QqImageGenPlugin)
                    .GetMethod("BuildPrompt", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(migrated, new object[] { "在窗边自拍", "selfie", references, "3:4" });
                using var handler = new CameraHttpProbe();
                using var client = new ImageGenerationClient(settings, null, null, handler);
                var result = await client.GenerateAsync(migratedPrompt, references, "3:4", default);
                Require(result.Success && handler.Paths.Single() == "/v1/images/edits" &&
                    handler.Fields["size"] == "1152x1536" && migratedPrompt == prompt,
                    "旧协议/方图/轮询配置自动忽略，旧凭据和视觉设定仍可用：" + mode);
            }
            finally { File.Delete(path); }
        }
        Console.WriteLine("Camera transport checks passed: standard Images API, GPT Image 2/2.5, references, sizes, failures and old config migration.");
    }

    private static ImageGenerationSettings CameraSettings(string model) => new() {
        BaseUrl = "https://camera.invalid/v1", Model = model, ApiKeys = new() { "test-key" }
    };

    private sealed class CameraHttpProbe : HttpMessageHandler
    {
        public static readonly byte[] OutputImage = Enumerable.Range(0, 128).Select(x => (byte)x).ToArray();
        public readonly List<string> Paths = new();
        public readonly Dictionary<string, string> Fields = new();
        public readonly List<(string Name, string Mime, byte[] Bytes)> Files = new();
        public string Json;
        public int Status = 200;
        public string Failure;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Paths.Add(request.RequestUri.AbsolutePath);
            if (request.Method == HttpMethod.Get)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(OutputImage) };
            Require(request.Headers.Authorization?.Parameter == "test-key", "标准 Bearer 鉴权");
            if (Failure == "timeout") throw new OperationCanceledException();
            if (Failure == "network") throw new HttpRequestException("fixture connection failure");
            if (request.Content is MultipartFormDataContent multipart)
            {
                foreach (var part in multipart)
                {
                    var disposition = part.Headers.ContentDisposition;
                    var name = disposition.Name.Trim('"');
                    if (disposition.FileName != null)
                        Files.Add((name, part.Headers.ContentType.MediaType, await part.ReadAsByteArrayAsync(token)));
                    else Fields[name] = await part.ReadAsStringAsync(token);
                }
            }
            else Json = await request.Content.ReadAsStringAsync(token);
            return new HttpResponseMessage((HttpStatusCode)Status) { Content = new StringContent(
                Failure == "empty" ? "{\"data\":[]}" :
                Failure == "url" ? "{\"data\":[{\"url\":\"https://camera.invalid/output.png\"}]}" :
                Status == 200 ? JsonSerializer.Serialize(new { data = new[] { new { b64_json = Convert.ToBase64String(OutputImage) } } }) :
                "{\"error\":{\"message\":\"fixture rejection\"}}", Encoding.UTF8, "application/json") };
        }
    }
}
