using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using TraceSoul2.Plugins;

namespace TraceSoul2.ExternalPlugins.RealtimeCall;

public sealed class CallControlEndpoint : ITraceWebSocketEndpoint, IDisposable
{
    public const int MaxMessageBytes = 16 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly RealtimeSessionRegistry registry;
    private readonly CallDialogueHub? dialogue;
    private readonly string conversationId;
    private readonly byte[] tokenBytes;
    private readonly TimeSpan idleTimeout;
    private readonly CancellationTokenSource shutdown = new();
    private int connections;
    private int disposed;
    public int ActiveConnections => Volatile.Read(ref connections);
    public bool Configured => tokenBytes.Length >= 32;
    public string Path => "/plugins/realtime-call/ws";

    public CallControlEndpoint(RealtimeSessionRegistry registry, string conversationId, string accessToken,
        TimeSpan? idleTimeout = null, CallDialogueHub? dialogue = null)
    {
        this.registry = registry;
        this.conversationId = conversationId;
        tokenBytes = Encoding.UTF8.GetBytes(accessToken);
        this.idleTimeout = idleTimeout ?? TimeSpan.FromSeconds(120);
        this.dialogue = dialogue;
    }

    public bool Accept(string authorizationHeader, string queryString)
    {
        if (Volatile.Read(ref disposed) != 0 || !Configured || authorizationHeader == null ||
            !authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
        var candidate = Encoding.UTF8.GetBytes(authorizationHeader[7..]);
        return CryptographicOperations.FixedTimeEquals(candidate, tokenBytes);
    }

    public async Task OnConnectedAsync(WebSocket socket, CancellationToken token)
    {
        var count = Interlocked.Increment(ref connections);
        try
        {
            if (count > 8 || !Configured || Volatile.Read(ref disposed) != 0)
            {
                await Close(socket, WebSocketCloseStatus.PolicyViolation, "unavailable", token);
                return;
            }
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, shutdown.Token);
            using var sendGate = new SemaphoreSlim(1, 1);
            async Task SendText(string value, CancellationToken cancellationToken)
            {
                using var sendToken = CancellationTokenSource.CreateLinkedTokenSource(linked.Token, cancellationToken);
                await sendGate.WaitAsync(sendToken.Token);
                try
                {
                    if (socket.State != WebSocketState.Open) throw new WebSocketException("socket_closed");
                    var data = Encoding.UTF8.GetBytes(value);
                    await socket.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Text, true, sendToken.Token);
                }
                finally { sendGate.Release(); }
            }
            using var connection = new CallControlConnection(registry, conversationId, dialogue, SendText);
            var buffer = new byte[4096];
            while (socket.State == WebSocketState.Open && !linked.IsCancellationRequested)
            {
                using var messageTimeout = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                messageTimeout.CancelAfter(idleTimeout);
                using var message = new MemoryStream();
                WebSocketReceiveResult frame;
                do
                {
                    frame = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), messageTimeout.Token);
                    if (frame.MessageType == WebSocketMessageType.Close)
                    {
                        await Close(socket, WebSocketCloseStatus.NormalClosure, "closed", linked.Token);
                        return;
                    }
                    if (frame.MessageType != WebSocketMessageType.Text || message.Length + frame.Count > MaxMessageBytes)
                    {
                        await Close(socket, WebSocketCloseStatus.PolicyViolation, "text_control_only", linked.Token);
                        return;
                    }
                    message.Write(buffer, 0, frame.Count);
                } while (!frame.EndOfMessage);
                var response = connection.Handle(StrictUtf8.GetString(message.ToArray()));
                await SendText(response, messageTimeout.Token);
            }
        }
        catch (OperationCanceledException) { socket.Abort(); }
        catch (WebSocketException) { socket.Abort(); }
        catch (DecoderFallbackException) { socket.Abort(); }
        finally { Interlocked.Decrement(ref connections); }
    }

    private static async Task Close(WebSocket socket, WebSocketCloseStatus status, string reason, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            await socket.CloseOutputAsync(status, reason, timeout.Token);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        shutdown.Cancel();
        dialogue?.Stop();
        registry.Stop();
        // 尚在退出的连接仍持有此 token；不提前 Dispose CTS。
    }
}
