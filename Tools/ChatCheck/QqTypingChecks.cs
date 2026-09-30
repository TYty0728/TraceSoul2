using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Logic;
using TraceSoul2.Manager;
using TraceSoul2.Plugins;
using TraceSoul2.Plugins.Builtin;

internal static partial class Program
{
    private static async Task RunQqTypingChecksAsync()
    {
        using var store = new SqliteMemoryManager(":memory:");
        store.SavePairIdentity("小雨", "小光", "雨雨");
        EnvironmentLogic.SaveSettings(store, new EnvironmentSettings { OwnerAccounts = new()
            { new OwnerAccountBindingData { PlatformId = "builtin.onebot", UserId = "123" } } });
        var services = new TracePluginServices(store, new HierarchicalVectorRouterLogic(new FakeEncoder()));
        using var manager = new TracePluginManager(store, services);
        manager.RegisterExternal(new DialogueTracePlugin());
        manager.RegisterExternal(new InnerLifePlugin());
        var qq = new OneBotPlatformPlugin();
        manager.RegisterExternal(qq);
        var handler = new TypingTransport();
        using var http = new HttpClient(handler);
        void Field(string name, object value) => typeof(OneBotPlatformPlugin)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(qq, value);
        Field("http", http); Field("connected", true);
        qq.Config.enabled = true; qq.Config.reply_enabled = true;
        qq.Config.mode = "forward"; qq.Config.http_url = "https://qq.invalid";
        int Active() => ((IEnumerable)typeof(OneBotPlatformPlugin)
            .GetField("activeTypingStates", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(qq)).Cast<object>().Count();
        string Frame(int id, string type = "private", int user = 123) => JsonSerializer.Serialize(new
        {
            post_type = "message", self_id = 999, user_id = user, group_id = 456,
            message_type = type, message_id = id,
            message = new[] { new { type = "text", data = new { text = "看看资料" } } }
        });
        var completions = new List<string>();
        services.EventCompletedHooks.Add(source => { completions.Add(source.TraceId); return Task.CompletedTask; });

        qq.HandleInbound(Frame(1), null);
        Require(handler.Typing == 1 && Active() == 1, "QQ 入队即发送输入状态，不等待 Poll、识图或模型");
        qq.HandleInbound(Frame(1), null);
        var source = qq.TakeInbound().Single();
        Require(handler.Typing == 1, "重复入站不新建输入状态");
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueModel = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var llm = new TypingLlm { Step = async (step, token) =>
        {
            Require(Active() >= 1 && completions.Count == 0, "思考和工具续推全程持有输入状态");
            if (step == 1)
            {
                entered.TrySetResult(true);
                await continueModel.Task.WaitAsync(token);
                return "{\"step\":\"continue\",\"actions\":[{\"call_id\":\"recall\",\"capability_id\":\"memory.recall\",\"arguments\":[{\"name\":\"query\",\"value\":\"资料\"}]}]}";
            }
            return "{\"step\":\"finish\",\"reply\":\"看过啦。\"}";
        } };
        var work = new KernelLogic(store, llm, manager).ProcessPluginEventAsync("typing", source);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        qq.HandleInbound(Frame(2), null);
        var queued = qq.TakeInbound().Single();
        Require(Active() == 2, "处理期间收到下一条消息也持有独立状态");
        continueModel.TrySetResult(true);
        await handler.Sending.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Require(Active() == 2 && completions.Count == 0, "末条发送未完成不能提前结束输入状态");
        handler.ReleaseSend.TrySetResult(true);
        await work;
        Require(Active() == 1 && completions.SequenceEqual(new[] { source.TraceId }),
            "只结束已发完轮次，不能关闭排队轮次的输入状态");
        var throwing = new TypingLlm { Step = (_, _) => throw new InvalidOperationException("offline model failure") };
        try { await new KernelLogic(store, throwing, manager).ProcessPluginEventAsync("typing", queued); }
        catch (InvalidOperationException) { }
        Require(Active() == 0 && completions.Last() == queued.TraceId, "模型异常也释放对应轮次");

        qq.HandleInbound(Frame(3), null);
        var invalid = qq.TakeInbound().Single(); invalid.Content = "";
        try { await new KernelLogic(store, llm, manager).ProcessPluginEventAsync("typing", invalid); }
        catch (ArgumentException) { }
        Require(Active() == 0 && completions.Last() == invalid.TraceId, "上下文建立前失败也释放输入状态");

        qq.HandleInbound(Frame(4), null);
        var cancelled = qq.TakeInbound().Single();
        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel();
            try { await new KernelLogic(store, throwing, manager).ProcessPluginEventAsync("typing", cancelled, cancellationToken: cts.Token); }
            catch (OperationCanceledException) { }
        }
        Require(Active() == 0 && completions.Last() == cancelled.TraceId, "取消不能残留输入刷新");

        handler.FailSend = true;
        qq.HandleInbound(Frame(5), null);
        var failure = qq.TakeInbound().Single();
        try { await new KernelLogic(store, new AgentSequenceLlm("{\"step\":\"finish\",\"reply\":\"离线测试\"}"), manager)
            .ProcessPluginEventAsync("typing", failure); }
        catch (InvalidOperationException) { }
        Require(Active() == 0 && completions.Last() == failure.TraceId, "发送失败仍释放状态");
        handler.FailSend = false;

        // 用真实延迟发送队列模拟文字先完成、照片稍后完成，不访问图片 URL。
        qq.HandleInbound(Frame(9), null);
        var photoSource = qq.TakeInbound().Single();
        var photoKernel = new KernelLogic(store, new AgentSequenceLlm("{\"step\":\"finish\",\"reply\":\"照片随后到\"}"), manager);
        Func<TraceTurnContext, Task> queuePhoto = turn =>
        {
            typeof(KernelLogic).GetMethod("EnqueueImageWork", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(photoKernel, new object[] { new List<BrainCapabilityCallData> { new()
                {
                    call_id = "late-photo", capability_id = "qq.image.send", arguments = new()
                        { new BrainCallArgumentData { name = "file", value = "https://image.invalid/test.png" } }
                } }, turn, turn.ConversationId });
            return Task.CompletedTask;
        };
        services.TurnCompleteHooks.Add(queuePhoto);
        await photoKernel.ProcessPluginEventAsync("typing", photoSource);
        services.TurnCompleteHooks.Remove(queuePhoto);
        Require(Active() == 1 && !completions.Contains(photoSource.TraceId), "文字结束但仍有延迟照片时不能结束状态");
        var sendPhoto = await photoKernel.TakeDeferredWork().AnalyzeAsync(default);
        handler.Sending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.ReleaseSend = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var sendingPhoto = sendPhoto(default);
        await handler.Sending.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Require(Active() == 1, "照片发送尚未结束仍须持有输入状态");
        handler.ReleaseSend.TrySetResult(true);
        await sendingPhoto;
        Require(Active() == 0 && completions.Count(x => x == photoSource.TraceId) == 1,
            "末张延迟照片完成后只结束一次");

        var before = handler.Typing;
        qq.HandleInbound(Frame(6, "group"), null);
        Require(handler.Typing == before && Active() == 0, "群消息不向个人发送输入状态");
        qq.TakeInbound();
        qq.Config.reply_enabled = false;
        qq.HandleInbound(Frame(7), null);
        Require(handler.Typing == before && Active() == 0, "关闭回发时不显示输入状态");
        qq.TakeInbound(); qq.Config.reply_enabled = true;
        handler.BlockStatus = true;
        qq.HandleInbound(Frame(10), null);
        var blockedStatus = qq.TakeInbound().Single();
        try { await new KernelLogic(store, throwing, manager).ProcessPluginEventAsync("typing", blockedStatus); }
        catch (InvalidOperationException) { }
        Require(Active() == 0 && handler.StatusCancelled, "整轮结束也取消尚未返回的状态请求");
        handler.BlockStatus = false;
        handler.FailStatus = true;
        qq.HandleInbound(Frame(8), null);
        var statusFailure = qq.TakeInbound().Single();
        await new KernelLogic(store, new AgentSequenceLlm("{\"step\":\"finish\",\"reply\":\"仍能回复\"}"), manager)
            .ProcessPluginEventAsync("typing", statusFailure);
        Require(Active() == 0 && completions.Last() == statusFailure.TraceId, "状态 API 失败不能阻断回复");
        qq.Config.mode = "reverse";
        using var socket = new TypingSocket();
        socket.Reply = payload => typeof(OneBotPlatformPlugin)
            .GetMethod("HandleSocketFrame", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(qq, new object[] { payload, socket });
        qq.HandleInbound(Frame(11), socket);
        var socketSource = qq.TakeInbound().Single();
        Require(socket.Typing == 1 && Active() == 1, "反向 WS 收件不等待状态回包，能继续处理正文");
        await new KernelLogic(store, new AgentSequenceLlm("{\"step\":\"finish\",\"reply\":\"WS 回复\"}"), manager)
            .ProcessPluginEventAsync("typing", socketSource);
        Require(socket.Messages == 1 && Active() == 0 && socket.State == WebSocketState.Open,
            "状态 API 不回包时正文仍能发送，结束取消等待而不关闭共享 WS");
        var pending = (IDictionary)typeof(OneBotPlatformPlugin).GetField("pendingActions", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(qq);
        Require(pending.Count == 0, "取消后清除状态 API 的 echo 等待项");
        var finalCount = handler.Typing;
        await Task.Delay(5100);
        Require(handler.Typing == finalCount, "结束后不再定时刷新");
        Console.WriteLine("QQ typing checks passed: receipt, queue, continuation, final send, failure, cancellation and stop.");

    }

    private sealed class TypingTransport : HttpMessageHandler
    {
        public int Typing;
        public bool FailStatus;
        public bool BlockStatus, StatusCancelled;
        public bool FailSend;
        public TaskCompletionSource<bool> Sending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseSend = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            using var json = JsonDocument.Parse(await request.Content.ReadAsStringAsync(token));
            var action = json.RootElement.GetProperty("action").GetString();
            if (action == "set_input_status")
            {
                Require(json.RootElement.GetProperty("params").GetProperty("event_type").GetInt32() == 1,
                    "输入状态不能用正在说话冒充取消");
                Interlocked.Increment(ref Typing);
                if (BlockStatus)
                {
                    try { await Task.Delay(Timeout.Infinite, token); }
                    catch (OperationCanceledException) { StatusCancelled = true; throw; }
                }
                if (FailStatus) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }
            else if (action == "send_private_msg")
            {
                Sending.TrySetResult(true);
                await ReleaseSend.Task.WaitAsync(token);
                if (FailSend) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"ok\",\"retcode\":0}") };
        }
    }

    private sealed class TypingSocket : WebSocket
    {
        public Action<string> Reply;
        public int Typing, Messages;
        private WebSocketState state = WebSocketState.Open;
        public override WebSocketState State => state;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string CloseStatusDescription => null;
        public override string SubProtocol => null;
        public override void Abort() => state = WebSocketState.Aborted;
        public override void Dispose() => state = WebSocketState.Closed;
        public override Task CloseAsync(WebSocketCloseStatus status, string text, CancellationToken token) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string text, CancellationToken token) => Task.CompletedTask;
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token) => throw new NotSupportedException();
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken token)
        {
            using var request = JsonDocument.Parse(Encoding.UTF8.GetString(buffer));
            if (request.RootElement.GetProperty("action").GetString() == "set_input_status")
                Typing++; // 故意不返回状态回包，正文动作仍正常确认。
            else
            {
                Messages++;
                Reply(JsonSerializer.Serialize(new { status = "ok", retcode = 0,
                    echo = request.RootElement.GetProperty("echo").GetString() }));
            }
            return Task.CompletedTask;
        }
    }

    private sealed class TypingLlm : ILlmClient
    {
        public Func<int, CancellationToken, Task<string>> Step;
        private int count;
        public string ProviderId => "typing-check";
        public string Model => "typing-check";
        public Task<string> CompleteJsonAsync(List<DeepSeekMessageData> messages, CancellationToken cancellationToken = default, string promptCacheKey = null)
            => Step(++count, cancellationToken);
        public Task<string> CompleteTextAsync(List<DeepSeekMessageData> messages, CancellationToken cancellationToken = default, string promptCacheKey = null)
            => throw new Exception("No refinement expected");
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(new[] { Model });
    }
}
