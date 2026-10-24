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
        await RunContentRejectionChecksAsync();
        await RunUnifiedMainModelChecksAsync();
        await RunJevRouteChecksAsync();
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

    private static async Task RunUnifiedMainModelChecksAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "unified-main-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var server = new FailureHttpServer(200,
                "{\"choices\":[{\"message\":{\"content\":\"{}\"},\"finish_reason\":\"stop\"}]}");
            foreach (var mode in new[] { "active", "mixed-case", "disabled", "no-key", "missing", "same-provider" })
            {
                var path = Path.Combine(root, mode + ".json");
                var original = JsonSerializer.Serialize(new {
                    currentId = mode == "mixed-case" ? "CHAT" : "chat", thinking = new { providerId = mode == "missing" ? "gone" :
                        mode == "same-provider" ? "chat" : "reason", model = "previous-effective" },
                    jev = new { providerId = "reason", model = "decision" },
                    review = new { providerId = "chat", model = "review" },
                    providers = new[] {
                        new { id = "chat", model = "main", apiKey = "test", baseUrl = server.Url,
                            thinkingEnabled = mode != "disabled", models = new[] {
                                new { id = "main", roles = new[] { "thinking" }, enabled = true } } },
                        new { id = "reason", model = "reason-default", apiKey = mode == "no-key" ? "" : "test",
                            baseUrl = server.Url, thinkingEnabled = false, models = new[] {
                                new { id = "reason-default", roles = new[] { "thinking" }, enabled = true } } }
                    }
                });
                File.WriteAllText(path, original);
                var store = new LlmProviderStore(path);
                var migrated = mode == "active" || mode == "mixed-case" || mode == "same-provider";
                Require(store.CurrentId == (mode == "active" || mode == "mixed-case" ? "reason" : "chat") &&
                    store.CreateCurrentClient().Model == (migrated ? "previous-effective" : "main"),
                    "统一主模型保留升级前实际路由：" + mode);
                Require(File.ReadAllText(path + ".before-main-unification.bak") == original &&
                    !store.Slots().ContainsKey(LlmSlotNames.Thinking), "保留迁移前配置且不再公开独立思考槽");
                using (var json = JsonDocument.Parse(File.ReadAllText(path)))
                    Require(!json.RootElement.TryGetProperty("thinking", out _), "迁移后的配置不再保存思考槽");
                Require(store.ResolveSlot("thinking").Model == store.ResolveExplicitSlot("chat").Model &&
                    store.Get("chat").models.All(x => !x.roles.Contains("thinking")) &&
                    store.Slots()[LlmSlotNames.Jev].model == "decision" &&
                    store.Slots()[LlmSlotNames.Review].model == "review", "兼容旧解析名与用途标记，保留JEV及复盘");
                await store.CreateCurrentClient().CompleteJsonAsync(new List<DeepSeekMessageData> {
                    new DeepSeekMessageData("user", "Return JSON.") });
                using (var request = JsonDocument.Parse(server.Requests.Last()))
                    Require(request.RootElement.GetProperty("model").GetString() == store.Slots()["chat"].model,
                        "实际请求与页面主模型选择一致");
                store.Select("chat", "new-main");
                var current = store.Get("chat");
                current.thinkingEnabled = true;
                store.Upsert(current);
                store = new LlmProviderStore(path);
                Require(store.CreateCurrentClient().Model == "new-main" &&
                    File.ReadAllText(path + ".before-main-unification.bak") == original,
                    "重新选择及开推理不切换路由，重载不重复迁移或覆盖备份");
            }
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine("Unified main model checks passed: migration, fallback, wire requests, reload and legacy aliases.");
    }

    private static async Task RunJevRouteChecksAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "jev-route-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "llm-providers.json");
            // 模拟升级前配置，没有jev字段。
            File.WriteAllText(path, "{\"currentId\":\"chat\",\"providers\":[{\"id\":\"chat\",\"model\":\"chat-model\",\"apiKey\":\"test\",\"thinkingEnabled\":true}]}");
            var store = new LlmProviderStore(path);
            Require(string.IsNullOrEmpty(store.Slots()[LlmSlotNames.Jev].providerId) && store.CreateJevClient() == null &&
                store.ResolveSlot(LlmSlotNames.Jev) == null, "旧配置默认不启用JEV，不自动借用聊天模型");
            using var server = new FailureHttpServer(200,
                "{\"choices\":[{\"message\":{\"content\":\"{}\"},\"finish_reason\":\"stop\"}]}");
            store.Upsert(new LlmProviderRecord { id = "decision", model = "default-model", apiKey = "test",
                baseUrl = server.Url, temperature = 0.35f, thinkingEnabled = true });
            store.SetSlot(LlmSlotNames.Review, "chat", "review-model");
            store.SetReviewTemperature(0.1f);
            store.SetSlot(" JEV ", "decision", "jev-model");
            store = new LlmProviderStore(path);
            var client = store.CreateJevClient();
            Require(client != null && client.ProviderId == "decision" && client.Model == "jev-model" &&
                store.ResolveSlot(LlmSlotNames.Jev).Model == "jev-model" &&
                store.ResolveExplicitSlot(LlmSlotNames.Jev).ProviderId == "decision" &&
                store.CurrentId == "chat" && store.CreateCurrentClient().Model == "chat-model" &&
                store.CreateReviewClient().Model == "review-model" && server.Requests.Count == 0,
                "JEV选择跨重启保留，与主模型/复盘独立；配置和创建客户端本身不请求模型");
            await client.CompleteJsonAsync(new List<DeepSeekMessageData> { new DeepSeekMessageData("user", "Return JSON.") });
            using (var request = JsonDocument.Parse(server.Requests.Single()))
                Require(request.RootElement.GetProperty("model").GetString() == "jev-model" &&
                    Math.Abs(request.RootElement.GetProperty("temperature").GetDouble() - 0.35) < 0.001,
                    "实际回环请求走指定JEV模型及其供应商设置，不借用复盘专用温度");
            store.SetSlot(LlmSlotNames.Jev, "", "");
            Require(store.ResolveSlot(LlmSlotNames.Jev) == null && store.CreateJevClient() == null,
                "清空JEV槽不会按模型残留jev用途标记重新启用");
            store.SetSlot(LlmSlotNames.Jev, "decision", "jev-model");
            store.DeleteModel("decision", "jev-model");
            store = new LlmProviderStore(path);
            Require(string.IsNullOrEmpty(store.Slots()[LlmSlotNames.Jev].providerId) && store.CreateJevClient() == null,
                "删除JEV模型同步清空路由并持久化，不改用供应商另一个模型");
            store.SetSlot(LlmSlotNames.Jev, "decision", "another-model");
            store.Delete("decision");
            store = new LlmProviderStore(path);
            Require(store.CreateJevClient() == null && store.Slots()[LlmSlotNames.Chat].model == "chat-model" &&
                store.Slots()[LlmSlotNames.Review].model == "review-model", "删除JEV供应商保留其他槽的选择");
            store.Upsert(new LlmProviderRecord { id = "no-key", model = "not-ready", baseUrl = server.Url });
            store.SetSlot(LlmSlotNames.Jev, "no-key", "not-ready");
            Require(store.CreateJevClient() == null && store.ResolveSlot(LlmSlotNames.Jev) == null && server.Requests.Count == 1,
                "明确选择但不可用的JEV不偷偷回退或发起额外请求");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine("JEV route checks passed: legacy default, independent request, persistence, explicit opt-in and deletion.");
    }
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
