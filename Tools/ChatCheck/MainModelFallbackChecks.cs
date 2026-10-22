using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Host;
using TraceSoul2.Logic;
using TraceSoul2.Manager;
using TraceSoul2.Plugins;
using TraceSoul2.Plugins.Builtin;

internal static partial class Program
{
    private const string FallbackOk = "{\"choices\":[{\"message\":{\"content\":\"{\\\"step\\\":\\\"finish\\\",\\\"reply\\\":\\\"ok\\\"}\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":12,\"completion_tokens\":3}}";

    private static async Task RunMainModelFallbackChecksAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "main-fallback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await CheckFallbackWireAsync(root);
            await CheckFallbackClientBoundaryAsync();
            await CheckFallbackKernelAsync();
            CheckFallbackConfiguration(root);
            Require(FailureProtection.SafeReason(new InvalidOperationException("retry exhausted", new TimeoutException())) ==
                "请求超时或任务被中断", "包装后的超时仍给出准确安全原因");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine("Main model fallback checks passed: three-stage wire bounds, cancellation, continuity, safe failures and persisted choices.");
    }

    private static LlmProviderStore MakeFallbackStore(string path, params (string Id, string Url, string Type)[] providers)
    {
        var store = new LlmProviderStore(path);
        foreach (var p in providers)
            store.Upsert(new LlmProviderRecord { id = p.Id, baseUrl = p.Url, type = p.Type,
                model = p.Id + "-model", apiKey = "test", timeout = 5, transientRetries = 2 });
        store.Select(providers[0].Id, providers[0].Id + "-model");
        store.SetMainFallbacks(new MainModelFallbackSettings {
            secondary = providers.Length > 1 ? new LlmSlotRef { providerId = providers[1].Id, model = providers[1].Id + "-model" } : null,
            tertiary = providers.Length > 2 ? new LlmSlotRef { providerId = providers[2].Id, model = providers[2].Id + "-model" } : null });
        return store;
    }

    private static async Task CheckFallbackWireAsync(string root)
    {
        var messages = new List<DeepSeekMessageData> { new DeepSeekMessageData("system", "same identity"),
            new DeepSeekMessageData("user", "same private turn") };
        foreach (var firstStatus in new[] { 500, 429, 401, 402, 403, 404, 408, 200 })
        {
            using var first = new FailureHttpServer(firstStatus, firstStatus == 200
                ? "{\"choices\":[{\"message\":{\"content\":\"\"},\"finish_reason\":\"stop\"}]}"
                : "{\"error\":{\"message\":\"private upstream secret-key\"}}");
            using var second = new FailureHttpServer(503, "{\"error\":{\"message\":\"unavailable\"}}");
            using var third = new FailureHttpServer(200, FallbackOk);
            var store = MakeFallbackStore(Path.Combine(root, "wire-" + firstStatus, "providers.json"),
                ("primary", first.Url, null), ("secondary", second.Url, null), ("tertiary", third.Url, null));
            var logs = new List<string>();
            var client = store.CreateCurrentClient(logs.Add);
            var result = await client.CompleteJsonAsync(messages, promptCacheKey: "same-cache");
            Require(result.Contains("ok") && first.Calls == 1 && second.Calls == 1 && third.Calls == 1,
                "每级一次 HTTP 请求且按序转三级：" + firstStatus);
            Require(client.ProviderId == "tertiary" && client.Model == "tertiary-model" &&
                ((ILlmEndpoint)client).BaseUrl == third.Url && ((ILlmUsageReporter)client).LastUsage != null,
                "实际供应商、模型、地址和用量跟随成功通道");
            Require(store.Protection.List().Count == 0 && logs.Count(x => x.Contains("主模型切换")) == 2 &&
                logs.All(x => !x.Contains("secret-key") && !x.Contains("private upstream")),
                "备用成功不产生失败报告，切换日志不含上游正文");
            var bodies = new[] { first.Requests.Single(), second.Requests.Single(), third.Requests.Single() }
                .Select(x => JsonDocument.Parse(x).RootElement.GetProperty("messages").GetRawText()).ToList();
            Require(bodies.Distinct().Count() == 1, "三通道使用完全相同的身份和本轮消息，不复制新主体");
            await client.CompleteTextAsync(messages);
            Require(first.Calls == 1 && second.Calls == 1 && third.Calls == 2, "本轮后续表达沿用已成功通道，不重新碰失败通道");
            await store.CreateCurrentClient().CompleteJsonAsync(messages);
            Require(first.Calls == 2 && second.Calls == 2 && third.Calls == 3, "新轮次重新从主模型开始");
            Require(store.CurrentId == "primary", "临时切换不改用户保存的主模型");
        }
        using (var primary = new FailureHttpServer(200, FallbackOk))
        using (var second = new FailureHttpServer(500, "{}"))
        {
            var store = MakeFallbackStore(Path.Combine(root, "success", "providers.json"),
                ("primary", primary.Url, null), ("secondary", second.Url, null));
            await store.CreateCurrentClient().CompleteJsonAsync(messages);
            Require(primary.Calls == 1 && second.Calls == 0, "主模型成功不触发备用");
        }
        using (var primary = new FailureHttpServer(500, "{}"))
        using (var second = new FailureHttpServer(500, "{}"))
        using (var third = new FailureHttpServer(500, "{}"))
        {
            var store = MakeFallbackStore(Path.Combine(root, "exhausted", "providers.json"),
                ("primary", primary.Url, null), ("secondary", second.Url, null), ("tertiary", third.Url, null));
            var client = store.CreateCurrentClient();
            try { await client.CompleteJsonAsync(messages); throw new Exception("Expected failure"); }
            catch (MainModelFallbackException error)
            { Require(error.Message.Contains("primary") && error.Message.Contains("tertiary"), "最终错误包含各通道安全诊断"); }
            Require(primary.Calls == 1 && second.Calls == 1 && third.Calls == 1 && store.Protection.List().Count == 1,
                "三通道耗尽只报告一次，不循环");
            await ExpectFailureAsync(() => client.CompleteJsonAsync(messages));
            Require(primary.Calls + second.Calls + third.Calls == 3, "耗尽的本轮客户端不能重新花费");
        }
        foreach (var native in new[] { false, true })
        {
            using var primary = new FailureHttpServer(503, "{}");
            using var second = new FailureHttpServer(200, !native
                ? "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"ok\"}]},\"finishReason\":\"STOP\"}]}" : FallbackOk);
            var store = MakeFallbackStore(Path.Combine(root, "native-" + native, "providers.json"),
                ("primary", primary.Url, native ? LlmProviderCatalog.GoogleGenAi : null),
                ("secondary", second.Url, native ? null : LlmProviderCatalog.GoogleGenAi));
            await store.CreateCurrentClient().CompleteTextAsync(messages);
            Require(primary.Calls == 1 && second.Calls == 1, "Gemini 原生与兼容协议遵守每级一次");
        }
    }

    private static async Task CheckFallbackClientBoundaryAsync()
    {
        var messages = new List<DeepSeekMessageData> { new DeepSeekMessageData("user", "unchanged") };
        var first = new FallbackProbe("first", () => throw new TimeoutException());
        var second = new FallbackProbe("second", () => "ok");
        var route = new MainModelFallbackClient(new[] { first, second });
        Require(await route.CompleteJsonAsync(messages, promptCacheKey: "cache") == "ok" && first.Calls == 1 && second.Calls == 1 &&
            ReferenceEquals(first.Messages, second.Messages) && second.CacheKey == "cache", "超时切换携带同一上下文与缓存键");
        using var cancel = new CancellationTokenSource();
        first = new FallbackProbe("first", () => { cancel.Cancel(); throw new HttpRequestException("cancel race"); });
        second = new FallbackProbe("second", () => "ok");
        route = new MainModelFallbackClient(new[] { first, second });
        try { await route.CompleteJsonAsync(messages, cancel.Token); throw new Exception("Expected cancellation"); }
        catch (OperationCanceledException) { }
        Require(second.Calls == 0, "用户取消，即使表现为网络错误也不切换");
        first = new FallbackProbe("first", () => throw new InvalidOperationException("output schema rejected"));
        second = new FallbackProbe("second", () => "ok");
        await ExpectFailureAsync(() => new MainModelFallbackClient(new[] { first, second }).CompleteJsonAsync(messages));
        Require(second.Calls == 0, "本地业务校验或未知异常不当作通道故障自动重试");
        first = new FallbackProbe("first", () => throw new JsonException("private upstream body"));
        second = new FallbackProbe("second", () => "ok");
        Require(await new MainModelFallbackClient(new[] { first, second }).CompleteJsonAsync(messages) == "ok" && second.Calls == 1,
            "供应商外层 HTTP JSON 损坏可切换，生成正文的业务校验仍在外层处理");
    }

    private static void CheckFallbackConfiguration(string root)
    {
        var path = Path.Combine(root, "config", "providers.json");
        var store = new LlmProviderStore(path);
        Require(string.IsNullOrEmpty(store.MainFallbacks().secondary.providerId), "旧配置默认无备用");
        foreach (var id in new[] { "primary", "secondary", "tertiary" })
            store.Upsert(new LlmProviderRecord { id = id, model = id, apiKey = "test", baseUrl = "http://127.0.0.1:1/" + id });
        store.Select("primary", "primary");
        var settings = new MainModelFallbackSettings {
            secondary = new LlmSlotRef { providerId = "secondary", model = "secondary" },
            tertiary = new LlmSlotRef { providerId = "tertiary", model = "tertiary" } };
        store.SetMainFallbacks(settings);
        store = new LlmProviderStore(path);
        Require(store.MainFallbacks().secondary.providerId == "secondary" && store.MainFallbacks().tertiary.providerId == "tertiary",
            "保存和重启保留顺序");
        var before = File.ReadAllText(path);
        foreach (var bad in new[] {
            new LlmSlotRef { providerId = "primary", model = "primary" },
            new LlmSlotRef { providerId = "tertiary", model = "tertiary" },
            new LlmSlotRef { providerId = "missing", model = "missing" } })
        {
            try { store.SetMainFallbacks(new MainModelFallbackSettings { secondary = bad, tertiary = settings.tertiary }); throw new Exception("Expected rejection"); }
            catch (ArgumentException) { }
            Require(File.ReadAllText(path) == before, "无效或重复选择原子拒绝，不部分保存");
        }
        try { store.Select("secondary", "secondary"); throw new Exception("Expected duplicate rejection"); }
        catch (ArgumentException) { }
        Require(store.CurrentId == "primary", "修改主模型也检查重复通道");
        store.UpsertModel("secondary", "secondary", false, null);
        var logs = new List<string>();
        store.CreateCurrentClient(logs.Add);
        Require(logs.Any(x => x.Contains("secondary")), "已停用备用不能被调用");
        store.Delete("secondary");
        Require(string.IsNullOrEmpty(store.MainFallbacks().secondary.providerId), "删除供应商清理其备用引用");
        store.DeleteModel("tertiary", "tertiary");
        Require(string.IsNullOrEmpty(store.MainFallbacks().tertiary.providerId), "删除模型清理其备用引用");
        store.SetMainFallbacks(new MainModelFallbackSettings());
        Require(string.IsNullOrEmpty(new LlmProviderStore(path).MainFallbacks().tertiary.providerId), "清空配置关闭备用并持久化");
    }

    private static async Task CheckFallbackKernelAsync()
    {
        using var store = new SqliteMemoryManager(":memory:");
        store.SavePairIdentity("测试者", "同伴", "测试者");
        var services = new TracePluginServices(store, new HierarchicalVectorRouterLogic(new FakeEncoder()));
        using var manager = new TracePluginManager(store, services);
        manager.RegisterExternal(new DialogueTracePlugin());
        manager.RegisterExternal(new InnerLifePlugin());
        var probe = new AgentProbePlugin();
        manager.RegisterExternal(probe);
        var sequence = new Queue<string>(new[] {
            "{\"step\":\"continue\",\"actions\":[{\"call_id\":\"only-once\",\"capability_id\":\"check.agent.read\",\"arguments\":[{\"name\":\"query\",\"value\":\"test\"}]}]}" });
        var first = new FallbackProbe("first", () => sequence.Count > 0 ? sequence.Dequeue() : throw new TimeoutException());
        var second = new FallbackProbe("second", () => "{\"step\":\"finish\",\"reply\":\"完成。\",\"inner\":\"同一份内心\"}");
        await new KernelLogic(store, new MainModelFallbackClient(new[] { first, second }), manager)
            .ChatAsync("fallback-one-soul", "查一次资料");
        var moments = store.GetRecentMoments("fallback-one-soul", 30);
        Require(probe.Count == 1 && moments.Count(m => m.Content == "查一次资料") == 1 &&
            moments.Count(m => m.Content == "完成。") == 1, "执行工具后推理超时只切模型，工具、入站及回复均不重复");
        Require(store.LoadOrCreateInnerRuntime("fallback-one-soul").Narrative == "同一份内心", "备用写回同一人格runtime");
        Require(second.Messages.Any(m => m.content != null && m.content.Contains("答案42")), "备用接续原工具回执");
    }

    private sealed class FallbackProbe : ILlmClient
    {
        private readonly Func<string> call;
        public string ProviderId { get; }
        public string Model => ProviderId;
        public int Calls;
        public List<DeepSeekMessageData> Messages;
        public string CacheKey;
        public FallbackProbe(string id, Func<string> call) { ProviderId = id; this.call = call; }
        public Task<string> CompleteJsonAsync(List<DeepSeekMessageData> messages, CancellationToken token = default, string promptCacheKey = null)
        { Calls++; Messages = messages; CacheKey = promptCacheKey; return Task.FromResult(call()); }
        public Task<string> CompleteTextAsync(List<DeepSeekMessageData> messages, CancellationToken token = default, string promptCacheKey = null)
            => CompleteJsonAsync(messages, token, promptCacheKey);
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken token = default)
            => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }
}
