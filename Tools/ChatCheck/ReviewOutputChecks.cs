using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Host;
using TraceSoul2.Logic;
using TraceSoul2.Manager;

internal static partial class Program
{
    private static async Task RunReviewOutputChecksAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "review-output-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "llm-providers.json");
            using var server = new FailureHttpServer(200,
                "{\"choices\":[{\"message\":{\"content\":\"{}\"},\"finish_reason\":\"stop\"}]}");
            var store = new LlmProviderStore(path);
            store.Upsert(new LlmProviderRecord { id = "default", model = "test-model", apiKey = "test",
                baseUrl = server.Url, temperature = 1.6f, maxTokens = 8192 });
            var messages = new List<DeepSeekMessageData> { new DeepSeekMessageData("user", "Return JSON.") };
            await store.CreateCurrentClient().CompleteJsonAsync(messages);
            await store.CreateReviewClient().CompleteJsonAsync(messages);
            using (var chat = JsonDocument.Parse(server.Requests[0]))
            using (var review = JsonDocument.Parse(server.Requests[1]))
            {
                Require(Math.Abs(chat.RootElement.GetProperty("temperature").GetDouble() - 1.6) < 0.001,
                    "复盘低温不能改变聊天请求温度");
                Require(Math.Abs(review.RootElement.GetProperty("temperature").GetDouble() - 0.2) < 0.001,
                    "旧配置未设置复盘温度时，请求必须使用 0.2，不能继承聊天高温");
            }
            store.SetReviewTemperature(0);
            store = new LlmProviderStore(path);
            await store.CreateReviewClient("default", "override-model").CompleteJsonAsync(messages);
            using (var request = JsonDocument.Parse(server.Requests.Last()))
                Require(request.RootElement.GetProperty("temperature").GetDouble() == 0 &&
                    request.RootElement.GetProperty("model").GetString() == "override-model",
                    "复盘零温度必须持久化，服务器模型覆盖与复盘温度必须同时生效");
            foreach (var invalid in new[] { -1f, 1.6f, float.NaN, float.PositiveInfinity })
            {
                try { store.SetReviewTemperature(invalid); throw new Exception("Expected validation error"); }
                catch (ArgumentException) { }
            }
            Require(store.ReviewTemperature == 0 && store.Get("default").temperature == 1.6f,
                "拒绝非法复盘设置，不覆盖已保存配置或聊天温度");

            var model = new ReviewSequenceClient("{\"value\":\"x\": 3}", "{\"value\":\"fixed\"}");
            var result = await DeepSeekStructuredOutputLogic.CompleteAsync<ReviewCheckResult>(model, messages,
                x => !string.IsNullOrEmpty(x.value), "缺少 value", CancellationToken.None);
            Require(result.value == "fixed" && model.Calls == 2 && model.Repair.Contains("字节位置") &&
                !model.Repair.Contains("缺少 value"), "JSON 语法错误应反馈具体位置，不误报缺少必填字段；仅纠正一次");
            model = new ReviewSequenceClient("{\"value\":\"private-text\": 3}", "{\"value\":[]}");
            try
            {
                await DeepSeekStructuredOutputLogic.CompleteAsync<ReviewCheckResult>(model, messages,
                    x => true, "缺少 value", CancellationToken.None);
                throw new Exception("Expected structured failure");
            }
            catch (InvalidOperationException error)
            {
                Require(model.Calls == 2 && error.Message.Contains("首次错误") && error.Message.Contains("纠正后错误") &&
                    !error.Message.Contains("private-text"), "两次格式失败都保存安全诊断，不无限重试或泄漏内容");
            }
            model = new ReviewSequenceClient("{}", "{\"value\":\"fixed\"}");
            await DeepSeekStructuredOutputLogic.CompleteAsync<ReviewCheckResult>(model, messages,
                x => !string.IsNullOrEmpty(x.value), "缺少 value", CancellationToken.None);
            Require(model.Repair.Contains("缺少 value"), "结构合法但缺必填字段时仍须反馈字段验证原因");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine("Review output checks passed: isolated temperature, persistence, request payloads and bounded JSON correction.");
    }

    public sealed class ReviewCheckResult { public string value { get; set; } }
    private sealed class ReviewSequenceClient : ILlmClient
    {
        private readonly string[] responses;
        public int Calls;
        public string Repair;
        public string ProviderId => "test";
        public string Model => "test";
        public ReviewSequenceClient(params string[] values) { responses = values; }
        public Task<string> CompleteJsonAsync(List<DeepSeekMessageData> messages, CancellationToken token = default, string promptCacheKey = null)
        { if (Calls > 0) Repair = messages.Last().content; return Task.FromResult(responses[Calls++]); }
        public Task<string> CompleteTextAsync(List<DeepSeekMessageData> messages, CancellationToken token = default, string promptCacheKey = null)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken token = default) => throw new NotSupportedException();
    }
}
