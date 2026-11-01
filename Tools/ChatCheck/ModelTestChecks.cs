using System;
using System.Collections.Generic;
using System.IO;
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
