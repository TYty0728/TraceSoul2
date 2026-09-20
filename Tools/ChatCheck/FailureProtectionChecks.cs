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
using TraceSoul2.Manager;

internal static partial class Program
{
    private static async Task RunFailureProtectionChecksAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tracesoul-failure-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var guard = new FailureProtection(directory);
            var inner = new FailureTestClient();
            var client = new ProtectedLlmClient(inner, guard);
            await ExpectFailureAsync(() => client.CompleteTextAsync(new List<DeepSeekMessageData>()));
            Require(inner.Calls == 1 && guard.List().Single().Reason.Contains("402"), "模型余额错误必须持久化暂停");
            for (var i = 0; i < 10; i++)
                await ExpectFailureAsync(() => new ProtectedLlmClient(inner, new FailureProtection(directory))
                    .CompleteTextAsync(new List<DeepSeekMessageData>()));
            Require(inner.Calls == 1, "新客户端/新保护实例不得绕过暂停继续请求");

            var notifications = 0;
            var session = "{\"session_type\":\"private\",\"session_id\":\"12345\"}";
            Task<string> Send(string action, Dictionary<string, object> args, CancellationToken token)
            {
                notifications++;
                var payload = JsonSerializer.Serialize(args);
                Require(action == "send_private_msg" && Convert.ToInt64(args["user_id"]) == 12345,
                    "通知应直接送达 QQ 会话");
                Require(!payload.Contains("secret-key") && !payload.Contains("private-chat"), "通知不得包含上游正文和密钥");
                return Task.FromResult("{}");
            }
            await FailureNotifications.SendPendingAsync(guard, false, session, Send, _ => { }, default);
            Require(notifications == 0 && guard.List().Single().NotificationState == 0, "QQ 离线时保留待通知状态");
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
                FailureNotifications.SendPendingAsync(new FailureProtection(directory), true, session, Send, _ => { }, default)));
            Require(notifications == 1, "并发轮询只通知一次");
            await FailureNotifications.SendPendingAsync(new FailureProtection(directory), true, session, Send, _ => { }, default);
            Require(notifications == 1, "重启后不得重复发送已尝试的通知");

            guard.Resume("provider:test");
            inner.Fail = false;
            Require(await client.CompleteTextAsync(new List<DeepSeekMessageData>()) == "ok" && inner.Calls == 2,
                "必须显式恢复后才允许再次请求");
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                await ExpectFailureAsync(() => client.CompleteTextAsync(new List<DeepSeekMessageData>(), cancelled.Token));
                Require(guard.List().Count == 0 && inner.Calls == 2, "调用方取消不得被视为模型故障");
            }

            guard.BeginAttempt("daily", "日构建复盘");
            guard.Resume("daily");
            Require(guard.IsPaused("daily") && guard.List().Single().NotificationState == -1,
                "日构建在运行期间必须占位，恢复接口不能解除执行中的占位");
            var restarted = new FailureProtection(directory);
            restarted.RecoverInterruptedAttempts();
            Require(restarted.IsPaused("daily") && restarted.List().Single().NotificationState == 0,
                "进程异常退出后必须暂停，不能重新从头自动收费");
            notifications = 0;
            Task<string> FailedSend(string action, Dictionary<string, object> args, CancellationToken token)
            { notifications++; throw new IOException("ack lost"); }
            await FailureNotifications.SendPendingAsync(restarted, true, session, FailedSend, _ => { }, default);
            await FailureNotifications.SendPendingAsync(restarted, true, session, FailedSend, _ => { }, default);
            Require(notifications == 1, "通知发送失败或回执不明时不能自动重复发 QQ");
            restarted.Resume("daily");
            restarted.BeginAttempt("daily", "日构建复盘");
            restarted.FailAttempt("daily", new TimeoutException());
            Require(restarted.IsPaused("daily") && restarted.List().Single().Reason.Contains("超时"),
                "子进程失败后保留暂停及安全原因");

            var batchCalls = 0;
            var reported = 0;
            try
            {
                await ProtectedParallelWork.RunAsync(Enumerable.Range(0, 100), 4, async (item, token) =>
                {
                    Interlocked.Increment(ref batchCalls);
                    await Task.Delay(20, token);
                    throw new InvalidOperationException("细节输出缺少 detail");
                }, error => Interlocked.Increment(ref reported));
                throw new Exception("Batch should fail");
            }
            catch (InvalidOperationException) { }
            Require(batchCalls <= 4 && reported == 1, "批量复盘首个输出校验失败后，队列中余下任务不得继续调用模型");

            var compatibilityConfig = new DeepSeekConfigData
            { ApiKey = "test", Model = "test", Temperature = 0.3f, EmptyContentRetries = 20, TransientErrorRetries = 20 };
            using (var server = new FailureHttpServer(200, "", call => call % 2 == 1
                ? (400, "{\"error\":{\"message\":\"temperature only 1 is allowed\"}}")
                : (200, "{\"choices\":[{\"message\":{\"content\":\"\"},\"finish_reason\":\"stop\"}]}")))
            {
                compatibilityConfig.BaseUrl = server.Url;
                await ExpectFailureAsync(() => new DeepSeekClientManager(compatibilityConfig)
                    .CompleteTextAsync(new List<DeepSeekMessageData>()));
                Require(server.Calls == 3, "温度兼容回退的额外 HTTP 请求也必须计入总上限");
            }

            foreach (var native in new[] { false, true })
            {
                using (var server = new FailureHttpServer(402, "{\"error\":{\"message\":\"Insufficient Balance Internal server error only 1 is allowed\"}}"))
                {
                    var llm = FailureHttpClient(server.Url, native);
                    await ExpectFailureAsync(() => llm.CompleteTextAsync(new List<DeepSeekMessageData>()));
                    Require(server.Calls == 1, "HTTP 402 即使正文像瞬时错误也不能重试");
                }
                var body = native
                    ? "{\"candidates\":[{\"content\":{\"parts\":[]},\"finishReason\":\"RECITATION\"}]}"
                    : "{\"choices\":[{\"message\":{\"content\":\"\"},\"finish_reason\":\"stop\"}]}";
                using (var server = new FailureHttpServer(200, body))
                {
                    await ExpectFailureAsync(() => FailureHttpClient(server.Url, native)
                        .CompleteTextAsync(new List<DeepSeekMessageData>()));
                    Require(server.Calls == 3, "空响应/recitation 的嵌套重试必须共用 3 次请求上限");
                }
                using (var server = new FailureHttpServer(500, "{\"error\":{\"message\":\"upstream error\"}}"))
                {
                    await ExpectFailureAsync(() => FailureHttpClient(server.Url, native)
                        .CompleteTextAsync(new List<DeepSeekMessageData>()));
                    Require(server.Calls == 3, "HTTP 500 配置再大的重试数也最多请求 3 次");
                }
            }
            Console.WriteLine("Failure protection checks passed: persistence, resume, notification dedupe, cancellation and HTTP request bounds.");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static ILlmClient FailureHttpClient(string url, bool native)
    {
        var config = new DeepSeekConfigData { BaseUrl = url, ApiKey = "test", Model = "test", EmptyContentRetries = 20, TransientErrorRetries = 20 };
        return native ? new GeminiClientManager(config) : new DeepSeekClientManager(config);
    }

    private static async Task ExpectFailureAsync(Func<Task<string>> call)
    {
        try { await call(); }
        catch { return; }
        throw new InvalidOperationException("Expected protected failure.");
    }

    private sealed class FailureTestClient : ILlmClient
    {
        public int Calls;
        public bool Fail = true;
        public string ProviderId => "test";
        public string Model => "test";
        public Task<string> CompleteTextAsync(List<DeepSeekMessageData> messages, CancellationToken token = default, string promptCacheKey = null)
        {
            Calls++;
            if (Fail) throw new InvalidOperationException("语言模型 API 402: secret-key private-chat");
            return Task.FromResult("ok");
        }
        public Task<string> CompleteJsonAsync(List<DeepSeekMessageData> messages, CancellationToken token = default, string promptCacheKey = null)
            => CompleteTextAsync(messages, token, promptCacheKey);
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken token = default)
            => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }

    private sealed class FailureHttpServer : IDisposable
    {
        private readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        private readonly Task worker;
        public int Calls;
        public string Url { get; }
        public FailureHttpServer(int status, string body, Func<int, (int Status, string Body)> respond = null)
        {
            listener.Start();
            Url = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
            worker = Task.Run(async () =>
            {
                try
                {
                    while (!cts.IsCancellationRequested)
                    {
                        using var client = await listener.AcceptTcpClientAsync(cts.Token);
                        using var stream = client.GetStream();
                        var header = new StringBuilder();
                        var one = new byte[1];
                        while (!header.ToString().EndsWith("\r\n\r\n"))
                        { await stream.ReadExactlyAsync(one, cts.Token); header.Append((char)one[0]); }
                        var length = header.ToString().Split('\n').FirstOrDefault(x => x.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                        var request = new byte[length == null ? 0 : int.Parse(length.Split(':')[1])];
                        await stream.ReadExactlyAsync(request, cts.Token);
                        var call = Interlocked.Increment(ref Calls);
                        var reply = respond == null ? (Status: status, Body: body) : respond(call);
                        var bytes = Encoding.UTF8.GetBytes(reply.Body);
                        var response = Encoding.ASCII.GetBytes("HTTP/1.1 " + reply.Status + " Test\r\nContent-Type: application/json\r\nContent-Length: " + bytes.Length + "\r\nConnection: close\r\n\r\n");
                        await stream.WriteAsync(response, cts.Token);
                        await stream.WriteAsync(bytes, cts.Token);
                    }
                }
                catch (OperationCanceledException) { }
            });
        }
        public void Dispose() { cts.Cancel(); worker.GetAwaiter().GetResult(); listener.Stop(); cts.Dispose(); }
    }
}
