using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Logic;
using TraceSoul2.Manager;
using TraceSoul2.Host;

internal static partial class Program
{
    private static async Task RunContentRejectionChecksAsync()
    {
        var messages = new List<DeepSeekMessageData> { new("user", "Return the test detail.") };
        foreach (var reply in new[] {
            (400, "{\"error\":{\"code\":\"sensitive_words_detected\",\"message\":\"private test material\"}}"),
            (403, "{\"error\":{\"code\":\"content_policy_violation\",\"message\":\"private test material\"}}"),
            (200, "{\"choices\":[{\"finish_reason\":\"content_filter\",\"message\":{\"content\":null}}]}"),
            (200, "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"refusal\":\"private test material\",\"content\":null}}]}"),
            (200, "{\"error\":{\"type\":\"DataInspectionFailed\",\"message\":\"private test material\"}}"),
            (200, "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"抱歉，我无法满足这个请求。\"}}]}"),
            (200, "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"I cannot assist with this request.\"}}]}") })
        {
            using var server = new FailureHttpServer(reply.Item1, reply.Item2);
            var client = new DeepSeekClientManager(new DeepSeekConfigData { ApiKey = "test", BaseUrl = server.Url,
                Model = "test", EmptyContentRetries = 2, TransientErrorRetries = 0 });
            try { await client.CompleteJsonAsync(messages); throw new Exception("Expected content rejection"); }
            catch (LlmContentRejectedException error)
            {
                Require(server.Calls == 1 && !error.Message.Contains("private") &&
                    !DeepSeekClientManager.IsRetryableProviderException(error),
                    "明确内容拒绝只请求一次，脱敏且不进入空响应或服务故障重试");
            }
        }
        foreach (var body in new[] {
            "{\"promptFeedback\":{\"blockReason\":\"SAFETY\"}}",
            "{\"candidates\":[{\"finishReason\":\"PROHIBITED_CONTENT\"}]}" })
        {
            try { LlmContentRejectionLogic.CheckResponse(body); throw new Exception("Expected Gemini rejection"); }
            catch (LlmContentRejectedException) { }
        }
        using (var nativeServer = new FailureHttpServer(200,
            "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"抱歉，我无法满足这个请求。\"}]},\"finishReason\":\"STOP\"}]}"))
        {
            var nativeClient = new GeminiClientManager(new DeepSeekConfigData { ApiKey = "test", BaseUrl = nativeServer.Url,
                Model = "test", EmptyContentRetries = 2, TransientErrorRetries = 0 });
            try { await nativeClient.CompleteJsonAsync(messages); throw new Exception("Expected native refusal"); }
            catch (LlmContentRejectedException) { Require(nativeServer.Calls == 1, "Gemini自然语言拒绝同样不进入JSON未完重试"); }
        }
        foreach (var body in new[] {
            "{\"error\":{\"code\":\"invalid_api_key\",\"message\":\"Unauthorized sensitive account\"}}",
            "{\"error\":{\"message\":\"Insufficient Balance\"}}",
            "{\"choices\":[{\"finish_reason\":\"length\",\"message\":{\"content\":\"content_filter\"}}]}",
            "{\"detail\":\"我记得她提到敏感、审核和拒绝这些词。\"}", "not json" })
            LlmContentRejectionLogic.CheckResponse(body);
        foreach (var status in new[] { 401, 403, 429, 503 })
        {
            using var server = new FailureHttpServer(status, "{\"error\":{\"message\":\"ordinary service failure\"}}");
            var client = new DeepSeekClientManager(new DeepSeekConfigData { ApiKey = "test", BaseUrl = server.Url,
                Model = "test", EmptyContentRetries = 0, TransientErrorRetries = 0 });
            try { await client.CompleteJsonAsync(messages); throw new Exception("Expected service failure"); }
            catch (LlmContentRejectedException) { throw new Exception("Service error misclassified as content rejection"); }
            catch (InvalidOperationException) { Require(server.Calls == 1, "鉴权、限流和服务错误保持故障，不当成内容兜底"); }
        }
        foreach (var text in new[] { "抱歉，我无法满足这个请求。", "我不能生成这类内容，因为涉及内容政策。",
            "I'm sorry, but I cannot assist with this request.", "I can't provide explicit sexual content." })
            Require(LlmContentRejectionLogic.IsRefusal(text), "识别对当前加工任务的明确拒绝");
        foreach (var text in new[] { "我无法帮你做决定，但愿意陪你一起想。", "她说：抱歉，我无法满足这个请求。",
            "我想起内容审核失败这件事。", "我不能生成新的文件，这是当时的限制。" })
            Require(!LlmContentRejectionLogic.IsRefusal(text), "事件里的否定、引语或审核词不触发兜底");
        var plain = new AgentSequenceLlm("抱歉，我无法满足这个请求。");
        try
        {
            await DeepSeekStructuredOutputLogic.CompleteAsync<ReviewCheckResult>(plain, messages, x => true,
                "missing", CancellationToken.None);
            throw new Exception("Expected refusal before JSON repair");
        }
        catch (LlmContentRejectedException) { Require(plain.Requests.Count == 1, "自然语言拒绝不再要求同一模型纠正JSON"); }
        var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "review-refusal-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(folder);
        try
        {
            using var server = new FailureHttpServer(400, "{\"error\":{\"code\":\"content_filter\",\"message\":\"blocked\"}}");
            var providers = new LlmProviderStore(System.IO.Path.Combine(folder, "providers.json"));
            providers.Upsert(new LlmProviderRecord { id = "default", model = "test", apiKey = "test", baseUrl = server.Url });
            try { await providers.CreateReviewClient().CompleteJsonAsync(messages); throw new Exception("Expected review rejection"); }
            catch (LlmContentRejectedException) { Require(providers.Protection.List().Count == 0, "复盘可处理的单条拒绝不先发通道故障通知"); }
            try { await providers.CreateCurrentClient().CompleteJsonAsync(messages); throw new Exception("Expected dialogue rejection"); }
            catch (LlmContentRejectedException) { Require(providers.Protection.List().Count == 1, "普通对话的拒绝仍保留故障报告"); }
        }
        finally { System.IO.Directory.Delete(folder, true); }
        Console.WriteLine("Content rejection checks passed: explicit provider signals, no retry, refusal prose and false-positive guards.");
    }
}
