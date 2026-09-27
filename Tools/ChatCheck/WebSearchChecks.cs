using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.ExternalPlugins.Tavily;
using TraceSoul2.Logic;
using TraceSoul2.Manager;
using TraceSoul2.Plugins;
using TraceSoul2.Plugins.Builtin;

internal static partial class Program
{
    private static async Task RunWebSearchChecksAsync()
    {
        using var store = new SqliteMemoryManager(":memory:");
        store.SavePairIdentity("小雨", "小光", "雨雨");
        var services = new TracePluginServices(store, new HierarchicalVectorRouterLogic(new FakeEncoder()));
        using var manager = new TracePluginManager(store, services);
        manager.RegisterExternal(new DialogueTracePlugin());
        manager.RegisterExternal(new InnerLifePlugin());
        var handler = new WebSearchHandler();
        using var http = new HttpClient(handler);
        var config = new TavilyConfig { ApiKey = "offline-test-token" };
        var plugin = new TavilyPlugin(http, config);
        manager.RegisterExternal(plugin);
        var turn = new TraceTurnContext("web-check", new MomentRecord { Content = "查询资料" }, new List<MomentRecord>(), 0, false, services);
        BrainCapabilityCallData Call(string id, params (string name, string value)[] arguments) => new()
        {
            call_id = Guid.NewGuid().ToString("N"), capability_id = id,
            arguments = arguments.Select(x => new BrainCallArgumentData { name = x.name, value = x.value }).ToList()
        };
        Task<TraceCapabilityResultData> Execute(BrainCapabilityCallData call, CancellationToken token = default) => manager.ExecuteAsync(call, turn, token);
        handler.Responder = (request, token) => Task.FromResult(WebJson(new
        {
            results = new[] {
                new { title = "公开资料", url = "https://example.com/source", content = "可核对的搜索片段", published_date = "2026-09-27" },
                new { title = "重复", url = "https://example.com/source", content = "重复结果", published_date = "" },
                new { title = "不安全链接", url = "http://127.0.0.1/data", content = "不应呈现", published_date = "" } }
        }));
        Require(AgentLoopLogic.BuildCatalog(manager.GetAvailableActionCatalog(turn)).Count(x => x.Id.StartsWith("web.")) == 2,
            "搜索与读取应通过统一行动目录跨平台可用");
        var search = await Execute(Call("web.search", ("query", "当天天气资料"), ("topic", "news"), ("time_range", "day")));
        using (var result = JsonDocument.Parse(search.Payload))
        using (var request = JsonDocument.Parse(handler.Bodies.Last()))
        {
            Require(search.Status == "success" && result.RootElement.GetProperty("sources").GetArrayLength() == 1,
                "搜索须去重并过滤无效来源，保留可读片段");
            Require(result.RootElement.GetProperty("retrieved_at").GetString().Length > 0 && search.EvidenceRefs.Single() == "https://example.com/source",
                "搜索须保留 URL 和获取时间");
            Require(!request.RootElement.GetProperty("include_answer").GetBoolean() && !request.RootElement.GetProperty("include_raw_content").GetBoolean() &&
                request.RootElement.GetProperty("time_range").GetString() == "day" && request.RootElement.GetProperty("topic").GetString() == "news",
                "先搜索片段，不附带生成答案和所有网页全文；时间筛选须传递");
            Require(handler.Auth.Last() == "Bearer offline-test-token" && !handler.Bodies.Last().Contains(config.ApiKey),
                "密钥只进入请求认证头");
        }
        Require(search.ProducedEvent?.EvidenceType == EvidenceTypeValues.PluginObserved && search.ProducedEvent.Content.Contains("尚未核对原文"),
            "搜索观察不能冒充已经验证的世界事实");

        var credentialUrlFixture = new UriBuilder("https://example.com/") { UserName = "user", Password = "pass" }.Uri.AbsoluteUri;
        foreach (var bad in new[] { "file:///tmp/a", "http://localhost/", "http://10.0.0.1/", "http://2130706433/", "http://[::1]/", "http://100.64.0.1/", credentialUrlFixture, "http://host.local/" })
        {
            var before = handler.Bodies.Count;
            var result = await Execute(Call("web.read", ("url", bad)));
            Require(result.Status == "failed" && handler.Bodies.Count == before, "非法网页地址不能发起请求");
        }
        var prior = handler.Bodies.Count;
        Require((await Execute(Call("web.search", ("query", "")))).Status == "failed" &&
            (await Execute(Call("web.search", ("query", "资料"), ("time_range", "forever")))).Status == "failed" && handler.Bodies.Count == prior,
            "无效查询或时间范围必须在请求前拒绝");

        handler.Responder = (request, token) => Task.FromResult(WebJson(new
        { results = new[] { new { url = "https://example.com/article", raw_content = new string('\u0001', 15000) } }, failed_results = Array.Empty<object>() }));
        var read = await Execute(Call("web.read", ("url", "https://example.com/article"), ("query", "相关证据")));
        using (var result = JsonDocument.Parse(read.Payload))
        using (var request = JsonDocument.Parse(handler.Bodies.Last()))
        {
            Require(read.Status == "success" && read.Payload.Length <= TavilyPlugin.PayloadCharLimit &&
                result.RootElement.GetProperty("sources")[0].GetProperty("truncated").GetBoolean(), "超长/转义内容必须保持完整 JSON 并明确截断");
            Require(request.RootElement.GetProperty("query").GetString() == "相关证据" &&
                handler.Urls.Last() == "https://api.tavily.com/extract", "按需读取应调用 Extract 并传递页内查询");
        }
        handler.Responder = (request, token) => Task.FromResult(WebJson(new { results = Array.Empty<object>(), failed_results = new[] { new { url = "https://example.com/article", error = "private server details" } } }));
        var unread = await Execute(Call("web.read", ("url", "https://example.com/article")));
        Require(unread.Status == "failed" && unread.ProducedEvent == null && !unread.Payload.Contains("private server"), "HTTP 200 但无正文仍是读取失败");
        var empty = await Execute(Call("web.search", ("query", "找不到的资料")));
        Require(empty.Status == "empty" && empty.ProducedEvent == null, "搜索空结果不伪造观察内容");
        foreach (var status in new[] { 401, 429, 500 })
        {
            handler.Responder = (request, token) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
            { Content = new StringContent("private-error-" + config.ApiKey) });
            var before = handler.Bodies.Count;
            var failed = await Execute(Call("web.search", ("query", "查询")));
            Require(failed.Status == "failed" && !failed.Payload.Contains(config.ApiKey) && !failed.Summary.Contains("private-error") &&
                handler.Bodies.Count == before + 1, "HTTP 失败不泄露响应正文或密钥，不自动重试消耗额度");
        }
        handler.Responder = (request, token) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{bad-json") });
        Require((await Execute(Call("web.search", ("query", "查询")))).Payload.Contains("invalid_response"), "畸形结果不能被当成成功");
        handler.Responder = (request, token) => Task.FromResult(WebJson(new { unexpected = "missing results" }));
        Require((await Execute(Call("web.search", ("query", "查询")))).Payload.Contains("invalid_response"), "缺失 results 不是空搜索");
        handler.Responder = (request, token) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', 2 * 1024 * 1024 + 1)) });
        Require((await Execute(Call("web.search", ("query", "查询")))).Payload.Contains("response_too_large"), "响应需有独立字节上限");
        handler.Responder = (request, token) => throw new OperationCanceledException();
        Require((await Execute(Call("web.search", ("query", "查询")))).Payload.Contains("timeout"), "内部超时应成为工具失败反馈");
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel();
            var cancelled = false;
            try { await Execute(Call("web.search", ("query", "查询")), cancel.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Require(cancelled, "用户/宿主取消须向上传播");
        }
        using (var cancel = new CancellationTokenSource())
        {
            handler.Responder = async (request, token) =>
            {
                cancel.Cancel();
                await Task.Delay(Timeout.Infinite, token);
                return WebJson(new { results = Array.Empty<object>() });
            };
            var cancelled = false;
            try { await Execute(Call("web.search", ("query", "取消正在执行的查询")), cancel.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Require(cancelled, "请求进行中也必须响应宿主取消");
        }

        // 真正经过 Kernel 与插件管理器，检查工具续推、来源入库及普通聊天不联网。
        handler.Responder = (request, token) => Task.FromResult(request.RequestUri.AbsolutePath == "/search"
            ? WebJson(new { results = new[] { new { title = "资料来源", url = "https://example.com/source", content = "搜索片段" } } })
            : WebJson(new { results = new[] { new { url = "https://example.com/source", raw_content = "原文证据：试验资料42。" } } }));
        var options = new JsonSerializerOptions { IncludeFields = true };
        string Step(AgentStepData step) => JsonSerializer.Serialize(step, options);
        var llm = new AgentSequenceLlm(
            Step(new AgentStepData { step = "continue", actions = new List<BrainCapabilityCallData> { Call("web.search", ("query", "公开资料")) } }),
            Step(new AgentStepData { step = "continue", actions = new List<BrainCapabilityCallData> { Call("web.read", ("url", "https://example.com/source")) } }),
            Step(new AgentStepData { step = "finish", reply = "查到了试验资料42，来源：https://example.com/source" }));
        await new KernelLogic(store, llm, manager).ChatAsync("web-kernel", "查一下公开资料", historyWindowMax: 8);
        Require(llm.Requests.Count == 3 && llm.TextRequests == 0 && llm.Requests[2].Contains("原文证据：试验资料42"),
            "Agent 必须搜索→阅读→直接回应，不添加额外摘要或表达模型");
        Require(store.GetRecentMoments("web-kernel", 20).Count(x => x.SourcePluginId == "web.tavily" && x.Role == "system_event") == 2,
            "搜索和阅读留存带来源的观察，不冒充对方发言");
        var callCount = handler.Bodies.Count;
        var direct = new AgentSequenceLlm(Step(new AgentStepData { step = "finish", reply = "我在。" }));
        await new KernelLogic(store, direct, manager).ChatAsync("web-direct", "在吗", historyWindowMax: 8);
        Require(direct.Requests.Count == 1 && handler.Bodies.Count == callCount, "普通闲聊保留单次生成且不联网");
        manager.SetEnabled("web.tavily", false);
        Require(manager.GetAvailableActionCatalog(turn).All(x => !x.Id.StartsWith("web.")), "停用后必须从能力目录消失");
        manager.Unregister("web.tavily");
        // 使用新插件 ID 的持久开关前恢复启用状态，再验证缺 Key 隐藏。
        manager.RegisterExternal(new TavilyPlugin(http, new TavilyConfig()));
        manager.SetEnabled("web.tavily", true);
        Require(manager.GetAvailableActionCatalog(turn).All(x => !x.Id.StartsWith("web.")), "未配置 Key 时不能向模型承诺联网能力");

        var directory = Path.Combine(Path.GetTempPath(), "tracesoul-web-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "plugin.json"), "{\"search_depth\":\"fast\",\"max_results\":4}");
            File.WriteAllText(Path.Combine(directory, "config.json"), "{\"api_key\":\"offline-test-token\",\"max_results\":2}");
            var loaded = TavilyConfig.Load(directory, directory);
            Require(loaded.Available && loaded.MaxResults == 2 && loaded.SearchDepth == "fast", "持久配置覆盖包默认值且保留未覆盖字段");
        }
        finally
        {
            if (string.Equals(Path.GetDirectoryName(Path.GetFullPath(directory)), Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                Directory.Delete(directory, true);
        }
        Console.WriteLine("Web search checks passed: Tavily HTTP contract, config, bounds, errors, cancellation, availability, provenance and Kernel search/read/reply.");
    }

    private static HttpResponseMessage WebJson(object value) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

    private sealed class WebSearchHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Responder;
        public List<string> Bodies { get; } = new();
        public List<string> Auth { get; } = new();
        public List<string> Urls { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Bodies.Add(await request.Content.ReadAsStringAsync(token));
            Auth.Add(request.Headers.Authorization?.ToString()); Urls.Add(request.RequestUri.AbsoluteUri);
            return await Responder(request, token);
        }
    }
}
