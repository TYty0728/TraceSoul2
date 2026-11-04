using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Host;
using TraceSoul2.Manager;

internal static partial class Program
{
    private static async Task RunModelTestChecksAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "model-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var model in new[] { "glm-5.3", "glm-5.3-flash", "GLM-5.3-FLASH" })
            {
                using var server = new FailureHttpServer(200, FallbackOk);
                var path = Path.Combine(root, Guid.NewGuid().ToString("N"), "providers.json");
                var store = new LlmProviderStore(path);
                store.Upsert(new LlmProviderRecord { id = "glm", baseUrl = server.Url, model = model,
                    apiKey = "test", timeout = 5, thinkingEnabled = true, reasoningEffort = "max" });
                store.Select("glm", model);
                var before = File.ReadAllText(path);
                var messages = new List<DeepSeekMessageData> { new("user", "Return JSON") };
                foreach (var client in new[] { store.CreateReviewClient(), store.CreateReviewAttemptClient(),
                    store.CreateTestClient("glm", model, "review") })
                    await client.CompleteJsonAsync(messages);
                Require(server.Calls == 3, "GLM复盘、日构建与测试各发一次请求");
                foreach (var body in server.Requests)
                {
                    using var request = JsonDocument.Parse(body);
                    var value = request.RootElement;
                    Require(value.GetProperty("thinking").GetProperty("type").GetString() == "enabled" &&
                        value.GetProperty("reasoning_effort").GetString() == "low",
                        "GLM 5.3复盘关思考偏好必须映射为enabled/low");
                    Require(value.GetProperty("response_format").GetProperty("type").GetString() == "json_object" &&
                        value.GetProperty("temperature").GetSingle() == store.ReviewTemperature,
                        "兼容思考参数保留复盘JSON模式和温度");
                }
                Require(File.ReadAllText(path) == before && store.Protection.List().Count == 0,
                    "GLM兼容不得修改保存的配置或触发保护");
                foreach (var effort in new[] { "low", "high", "max" })
                {
                    var config = new DeepSeekConfigData { Model = model, ThinkingEnabled = true, ReasoningEffort = effort };
                    using var text = JsonDocument.Parse(DeepSeekClientManager.BuildChatRequestJson(config, messages, 1f, false, false));
                    Require(text.RootElement.GetProperty("reasoning_effort").GetString() == effort &&
                        !text.RootElement.TryGetProperty("response_format", out _), "GLM 5.3聊天保留合法思考强度及文本模式");
                }
            }
            foreach (var native in new[] { false, true })
            foreach (var status in new[] { 200, 503 })
            {
                using var server = new FailureHttpServer(status, native
                    ? "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"OK\"}]},\"finishReason\":\"STOP\"}]}"
                    : "{\"choices\":[{\"message\":{\"content\":\"OK\"},\"finish_reason\":\"stop\"}]}");
                var path = Path.Combine(root, native + "-" + status, "providers.json");
                var store = MakeFallbackStore(path, ("probe", server.Url, native ? LlmProviderCatalog.GoogleGenAi : null));
                var before = File.ReadAllText(path);
                var client = store.CreateTestClient("probe", "probe-model");
                var messages = new List<DeepSeekMessageData> { new("user", "Reply OK") };
                if (status == 200) Require(await client.CompleteTextAsync(messages) == "OK", "测试支持兼容及原生协议");
                else await ExpectFailureAsync(() => client.CompleteTextAsync(messages));
                Require(server.Calls == 1, "测试仅调用一次，不重试或走备用");
                Require(store.Protection.List().Count == 0, "测试失败不写入生产错误保护");
                Require(File.ReadAllText(path) == before, "测试不修改模型选择或供应商配置");
                try { store.CreateTestClient("probe", "missing"); throw new Exception("Expected rejection"); }
                catch (ArgumentException) { }
                if (!native)
                {
                    var config = ((DeepSeekClientManager)store.CreateTestClient("probe", "probe-model", "review")).ConfigSnapshot;
                    Require(!config.ThinkingEnabled && config.TimeoutSeconds == 600 && config.Temperature == store.ReviewTemperature,
                        "复盘测试沿用关思考、复盘温度与超时");
                }
            }
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine("Model test checks passed: both protocols, success/failure, single request, configuration and protection isolation.");
    }
}
