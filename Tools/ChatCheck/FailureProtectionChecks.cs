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
using TraceSoul2.Plugins.Builtin;

internal static partial class Program
{
    private static async Task RunFailureProtectionChecksAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tracesoul-failure-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var guard = new FailureProtection(directory);
            await RunStickerFailureProtectionCheckAsync(guard, directory);
            var inner = new FailureTestClient();
            var client = new ProtectedLlmClient(inner, guard);
            await ExpectFailureAsync(() => client.CompleteTextAsync(new List<DeepSeekMessageData>()));
            Require(inner.Calls == 1 && guard.List().Single().Reason.Contains("402") && !guard.IsPaused("provider:test"),
                "模型余额错误必须持久化报告，但不得暂停聊天");
            for (var i = 0; i < 10; i++)
                await ExpectFailureAsync(() => new ProtectedLlmClient(inner, new FailureProtection(directory))
                    .CompleteTextAsync(new List<DeepSeekMessageData>()));
            Require(inner.Calls == 11 && guard.List().Count == 1, "后续调用不被锁住，同一未处理错误报告去重");

            var notifications = 0;
            var session = "{\"session_type\":\"private\",\"session_id\":\"12345\"}";
            Task<string> Send(string action, Dictionary<string, object> args, CancellationToken token)
            {
                notifications++;
                var payload = AssertNotificationWirePayload(action, args);
                Require(action == "send_private_msg" && Convert.ToInt64(args["user_id"]) == 12345,
                    "通知应直接送达 QQ 会话");
                Require(!payload.Contains("secret-key") && !payload.Contains("private-chat"), "通知不得包含上游正文和密钥");
                Require(payload.Contains("ERROR") && !payload.Contains("已暂停"), "模型错误通知不得声称对话暂停");
                return Task.FromResult("{}");
            }
            await FailureNotifications.SendPendingAsync(guard, false, session, Send, _ => { }, default);
            Require(notifications == 0 && guard.List().Single().NotificationState == 0, "QQ 离线时保留待通知状态");
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
                FailureNotifications.SendPendingAsync(new FailureProtection(directory), true, session, Send, _ => { }, default)));
            Require(notifications == 1, "并发轮询只通知一次");
            await FailureNotifications.SendPendingAsync(new FailureProtection(directory), true, session, Send, _ => { }, default);
            Require(notifications == 1, "重启后不得重复发送已尝试的通知");

            inner.Fail = false;
            Require(await client.CompleteTextAsync(new List<DeepSeekMessageData>()) == "ok" && inner.Calls == 12,
                "模型恢复后下一次调用直接成功，无需手动恢复");
            guard.Resume("provider:test");
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                await ExpectFailureAsync(() => client.CompleteTextAsync(new List<DeepSeekMessageData>(), cancelled.Token));
                Require(guard.List().Count == 0 && inner.Calls == 12, "调用方取消不得被视为模型故障");
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
            Console.WriteLine("Failure protection checks passed: non-blocking errors/warnings, legacy upgrade, daily pause, notification dedupe, cancellation and HTTP request bounds.");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static string AssertNotificationWirePayload(string action, Dictionary<string, object> args)
    {
        string text = null;
        foreach (var echo in new[] { null, "echo-\"通知\"\t" })
        {
            var wire = OneBotPlatformPlugin.SerializeActionRequest(action, args, echo);
            using var document = JsonDocument.Parse(wire);
            var root = document.RootElement;
            Require(root.GetProperty("action").GetString() == action &&
                root.TryGetProperty("echo", out var actualEcho) == (echo != null) &&
                (echo == null || actualEcho.GetString() == echo), "HTTP/WS 动作封装必须正确转义并保留 echo");
            var message = root.GetProperty("params").GetProperty("message");
            Require(message.ValueKind == JsonValueKind.Array && message.GetArrayLength() == 1 &&
                message[0].GetProperty("type").GetString() == "text" && !wire.Contains("AnonymousType"),
                "错误通知最终传输必须是消息段数组，不能发送匿名类型名或二次编码字符串");
            text = message[0].GetProperty("data").GetProperty("text").GetString();
            Require(text.Contains("\n") && root.GetProperty("params").GetProperty("user_id").GetInt64() == 12345,
                "最终通知须保留完整正文和数值型会话 ID");
        }
        return text;
    }

    private static async Task RunStickerFailureProtectionCheckAsync(FailureProtection guard, string directory)
    {
        var results = new List<TraceCapabilityResultData>
        {
            new TraceCapabilityResultData { CapabilityId = "qq.text.send", Status = "success" },
            new TraceCapabilityResultData
            {
                CapabilityId = "qq.sticker.send", Status = "failed",
                Summary = "没有匹配上的情绪表情（最高相似度 0.45，阈值 0.45）。"
            },
            new TraceCapabilityResultData { CapabilityId = "time.continue", Status = "success" }
        };
        var turn = new ChatTurnResultData("已发送的文字", "reflex", "", "", null, null, results);
        guard.ObserveTurnFailures(turn);
        var restarted = new FailureProtection(directory);
        restarted.ThrowIfPaused("dialogue");
        var inner = new FailureTestClient { Fail = false };
        var client = new ProtectedLlmClient(inner, restarted);
        Require(await client.CompleteTextAsync(new List<DeepSeekMessageData>()) == "ok" && inner.Calls == 1,
            "文字成功但表情失败后，下一轮必须仍能调用模型");
        var notifications = 0;
        await FailureNotifications.SendPendingAsync(restarted, true,
            "{\"session_type\":\"private\",\"session_id\":\"12345\"}",
            (_, args, _) =>
            {
                notifications++;
                var payload = AssertNotificationWirePayload("send_private_msg", args);
                Require(payload.Contains("WARNING") && !payload.Contains("ERROR") && !payload.Contains("已暂停"),
                    "表情失败只能发送 WARNING，不能声称停聊");
                return Task.FromResult("{}");
            }, _ => { }, default);
        Require(restarted.List().Single().Severity == "warning" && notifications == 1 && !restarted.IsPaused("warning:qq.sticker.send"),
            "单次表情失败应报告 WARNING，不得暂停对话");
        guard.Resume("warning:qq.sticker.send");

        // 表情 WARNING 不能掩盖同轮其它 ERROR，但两者都不暂停对话。
        foreach (var capability in new[] { "memory.archive", "identity.review", "turn.complete", "qq.text.send" })
        {
            results.Add(new TraceCapabilityResultData { CapabilityId = capability, Status = "failed" });
            guard.ObserveTurnFailures(turn);
            Require(!new FailureProtection(directory).IsPaused("dialogue") &&
                    guard.List().Any(x => x.Key == "dialogue" && x.Severity == "error"),
                "其它能力故障应记录 ERROR，不能停聊：" + capability);
            guard.Resume("dialogue");
            results.RemoveAt(results.Count - 1);
        }

        inner.Fail = true;
        await ExpectFailureAsync(() => client.CompleteTextAsync(new List<DeepSeekMessageData>()));
        guard.ObserveTurnFailures(turn);
        await ExpectFailureAsync(() => client.CompleteTextAsync(new List<DeepSeekMessageData>()));
        Require(inner.Calls == 3 && !guard.IsPaused("provider:test") &&
                guard.List().Any(x => x.Key == "provider:test" && x.Severity == "error"),
            "真实模型错误应记录 ERROR，但后续新调用不被锁住");
        guard.Resume("provider:test");
        guard.Resume("warning:qq.sticker.send");

        // 用旧结构建库，验证真实升级会新增字段并解除旧版对话/供应商停聊。
        var legacyDirectory = Path.Combine(directory, "legacy");
        Directory.CreateDirectory(legacyDirectory);
        using (var db = new SQLite.SQLiteConnection(Path.Combine(legacyDirectory, "failure-protection.sqlite3")))
        {
            db.Execute("CREATE TABLE failure_stops (Key TEXT PRIMARY KEY, Label TEXT, Reason TEXT, CreatedUnixMs BIGINT, NotificationState INTEGER)");
            foreach (var key in new[] { "dialogue", "provider:test", "daily" })
                db.Execute("INSERT INTO failure_stops VALUES (?,?,?,1,1)", key, "旧错误", "故障");
        }
        var upgraded = new FailureProtection(legacyDirectory);
        Require(!upgraded.IsPaused("dialogue") && !upgraded.IsPaused("provider:test") && upgraded.IsPaused("daily") &&
                upgraded.List().Count == 3 && upgraded.List().All(x => x.NotificationState == 1),
            "升级必须自动解除旧停聊标记，保留报告、通知去重和日构建暂停");
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
        public readonly List<string> Requests = new List<string>();
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
                        Requests.Add(Encoding.UTF8.GetString(request));
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
