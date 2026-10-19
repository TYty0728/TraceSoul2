using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Host;

internal static partial class Program
{
    private sealed class DecisionProbe : HttpMessageHandler
    {
        public string Url, Body, Auth, Relay;
        public int Count;
        public HttpStatusCode Status = HttpStatusCode.OK;
        public string Response = "{\"answers\":{\"next_step\":{\"type\":\"choice\",\"choice\":\"wait\",\"confidence\":0.8,\"probabilities\":{\"wait\":0.9,\"engage\":0.1}}},\"usage\":{\"cost\":0.00001}}";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Count++;
            Url = request.RequestUri.ToString();
            Auth = request.Headers.Authorization?.ToString();
            Relay = request.Headers.TryGetValues("X-TraceSoul-Relay-Key", out var values) ? values.Single() : null;
            Body = request.Content == null ? null : await request.Content.ReadAsStringAsync(token);
            return new HttpResponseMessage(Status) { Content = new StringContent(Response) };
        }
    }

    private static async Task RunDecisionsChecksAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tracesoul-decisions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "providers.json");
            var store = new LlmProviderStore(path);
            var original = store.CurrentId;
            var provider = store.AddFromTemplate("openrouter-decisions", null);
            Require(provider.models.Single().roles.SequenceEqual(new[] { "jev" }), "决策模板只有 JEV 用途");
            provider.apiKey = "test-upstream-secret";
            provider.relayKey = "test-relay-secret";
            provider.decisionMode = "relay";
            provider.baseUrl = "https://relay.example/tracesoul";
            provider.proxy = "http://127.0.0.1:7890";
            store.Upsert(provider);
            store.SetSlot("jev", provider.id, "typesafe/jev-1.14");
            store = new LlmProviderStore(path);
            Require(store.Get(provider.id).relayKey == provider.relayKey && store.Get(provider.id).decisionMode == "relay" &&
                store.ResolveExplicitSlot("jev").Model == "typesafe/jev-1.14" && store.CurrentId == original,
                "原生配置和 JEV 槽重载，保持主模型");
            void Reject(Action action, string message)
            {
                var rejected = false;
                try { action(); } catch (InvalidOperationException) { rejected = true; }
                Require(rejected, message);
            }
            Reject(() => store.Select(provider.id, provider.model), "原生决策不能作主模型");
            Reject(() => store.SetSlot("review", provider.id, provider.model), "原生决策不能作复盘模型");
            Reject(() => store.CreateClient(provider.id), "原生决策不能使用聊天客户端");
            Require(store.CurrentId == original, "拒绝错误选择不改主模型");
            var edit = store.Get(provider.id);
            edit.relayKey = "";
            edit.apiKey = "";
            store.Upsert(edit);
            Require(store.Get(provider.id).relayKey == provider.relayKey && store.Get(provider.id).apiKey == provider.apiKey, "空密钥保留已保存值");
            edit = store.Get(provider.id);
            edit.baseUrl = "https://relay.example/?secret=x";
            Reject(() => store.Upsert(edit), "无效 URL 不保存");
            Require(store.Get(provider.id).baseUrl == provider.baseUrl, "失败保存不污染内存");
            var fetched = store.MergeFetched(provider.id, new[] { "openai/gpt-6-luna-decisions" });
            Require(fetched.models.All(x => x.roles.SequenceEqual(new[] { "jev" })), "模型发现不添加聊天用途");
            using (var native = store.CreateJevDecisionClient()) Require(native != null, "原生 JEV 工厂可用");

            var probe = new DecisionProbe();
            using (var client = new DecisionsClient(provider, probe))
            {
                var result = JsonSerializer.Serialize(await client.TestAsync(provider.model, CancellationToken.None));
                Require(result.Contains("wait") && !result.Contains("secret"), "测试投影不包含凭据");
                Require(probe.Url == "https://relay.example/tracesoul/decisions" && probe.Auth == "Bearer test-upstream-secret" &&
                    probe.Relay == "test-relay-secret" && probe.Body.Contains("questions") && !probe.Body.Contains("messages"), "中转原生请求路径、凭据和载荷");
                probe.Response = "{\"data\":[{\"id\":\"typesafe/jev-1.14\"}]}";
                Require((await client.ListModelsAsync(CancellationToken.None)).Single() == provider.model && probe.Auth == null && probe.Body == null,
                    "中转模型列表不发送上游 Key 或推理");
            }
            provider.decisionMode = "direct";
            provider.baseUrl = "https://openrouter.ai/api";
            probe = new DecisionProbe();
            using (var client = new DecisionsClient(provider, probe))
            {
                await client.TestAsync(provider.model, CancellationToken.None);
                Require(probe.Url == "https://openrouter.ai/api/alpha/decisions" && probe.Relay == null, "直连不发送中转 Key");
                probe.Response = "{\"data\":[]}";
                await client.ListModelsAsync(CancellationToken.None);
                Require(probe.Url == "https://openrouter.ai/api/v1/models?output_modalities=decisions", "直连获取决策模型专用列表");
            }
            foreach (var status in new[] { HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests, HttpStatusCode.BadGateway, HttpStatusCode.TemporaryRedirect })
            {
                probe = new DecisionProbe { Status = status, Response = "test-upstream-secret" };
                using var client = new DecisionsClient(provider, probe);
                var failure = "";
                try { await client.TestAsync(provider.model, CancellationToken.None); }
                catch (InvalidOperationException e) { failure = e.Message; }
                Require(failure.Contains(((int)status).ToString()) && !failure.Contains("secret") && probe.Count == 1, "错误脱敏、不重试或跟随重定向");
            }
            foreach (var response in new[] { "not-json", "{\"answers\":{}}", "{\"answers\":{\"next_step\":{\"type\":\"choice\",\"choice\":\"unsafe\",\"confidence\":1,\"probabilities\":{\"wait\":1,\"engage\":0}}}}", new string('x', 1024 * 1024 + 1) })
            {
                probe = new DecisionProbe { Response = response };
                using var client = new DecisionsClient(provider, probe);
                var rejected = false;
                try { await client.TestAsync(provider.model, CancellationToken.None); } catch (InvalidOperationException) { rejected = true; }
                Require(rejected, "无效或过大响应不能成为有效决策");
            }
            var questions = JsonSerializer.SerializeToElement(new {
                binary = new { type = "noul", instructions = "Yes?" },
                level = new { type = "score", instructions = "Rate", criteria = new[] { "Low", "High" } } });
            DecisionsClient.ValidateQuestions(questions);
            using var answers = JsonDocument.Parse("{\"answers\":{\"binary\":{\"type\":\"noul\",\"noul\":0.4},\"level\":{\"type\":\"score\",\"score\":0.8,\"confidence\":0.6,\"probabilities\":{\"0\":0.2,\"1\":0.8}}}}");
            DecisionsClient.ValidateAnswers(answers.RootElement, questions);
            edit = store.Get(provider.id);
            edit.clearRelayKey = true;
            store.Upsert(edit);
            Require(string.IsNullOrEmpty(new LlmProviderStore(path).Get(provider.id).relayKey), "显式清除中转凭据可持久化");
            store.SetSlot("jev", null, null);
            Require(store.CreateJevDecisionClient() == null, "清空槽不回退主模型");
            Console.WriteLine("Native Decisions checks passed: configuration, routing, protocol, validation, secrets and no retries.");
        }
        finally { Directory.Delete(directory, true); }
    }
}
