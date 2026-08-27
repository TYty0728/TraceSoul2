using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Plugins;

namespace TraceSoul2.ExternalPlugins.GameSession
{
    internal sealed class GameBridgeRequest
    {
        public string op { get; set; }
        public string request_id { get; set; }
        public string conversation_id { get; set; }
        public string session_id { get; set; }
        public string profile_id { get; set; }
        public string adapter_id { get; set; }
        public string game_id { get; set; }
        public string title { get; set; }
        public string role_instruction { get; set; }
        public object environment { get; set; }
        public string kind { get; set; }
        public string actor { get; set; }
        public string content { get; set; }
        public object payload { get; set; }
        public object state { get; set; }
        public long occurred_unix_ms { get; set; }
        public string mode { get; set; }
        public int take { get; set; }
        public string game_path { get; set; }
        public string companion { get; set; }
        public string sprite_base64 { get; set; }
        public string portrait_base64 { get; set; }
    }

    internal sealed class GameSessionWebSocketEndpoint : ITraceWebSocketEndpoint
    {
        private const int MaxMessageBytes = 18 * 1024 * 1024;
        private readonly GameSessionConfig config;
        private readonly GameSessionController controller;
        private readonly StardewInstaller stardewInstaller;
        private int activeConnections;

        /// <summary>当前连在 WS 桥上的游戏 mod 数；平台句柄据此报告连接状态。</summary>
        public int ActiveConnections
        {
            get { return System.Threading.Volatile.Read(ref activeConnections); }
        }
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        public GameSessionWebSocketEndpoint(GameSessionConfig config, GameSessionController controller,
            StardewInstaller stardewInstaller)
        {
            this.config = config;
            this.controller = controller;
            this.stardewInstaller = stardewInstaller;
        }

        public string Path { get { return config.websocket_path; } }

        public bool Accept(string authorizationHeader, string queryString)
        {
            if (string.IsNullOrWhiteSpace(config.access_token)) return true;
            var candidates = new HashSet<string>(StringComparer.Ordinal);
            foreach (var part in (authorizationHeader ?? string.Empty)
                         .Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var value = part.Trim();
                if (value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    value = value.Substring(7).Trim();
                else if (value.StartsWith("Token ", StringComparison.OrdinalIgnoreCase))
                    value = value.Substring(6).Trim();
                if (value.Length > 0) candidates.Add(value);
            }
            foreach (var pair in (queryString ?? string.Empty).TrimStart('?')
                         .Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                if (eq <= 0 || !string.Equals(pair.Substring(0, eq), "access_token",
                        StringComparison.OrdinalIgnoreCase)) continue;
                candidates.Add(Uri.UnescapeDataString(pair.Substring(eq + 1)));
            }
            return candidates.Contains(config.access_token);
        }

        public async Task OnConnectedAsync(WebSocket socket, CancellationToken token)
        {
            System.Threading.Interlocked.Increment(ref activeConnections);
            try
            {
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(
                       token, controller.ShutdownToken))
            {
                var activeToken = linked.Token;
                while (!activeToken.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    string text;
                    try { text = await ReceiveTextAsync(socket, activeToken); }
                    catch (OperationCanceledException) when (activeToken.IsCancellationRequested) { break; }
                    catch (WebSocketException) { break; }
                    if (text == null) break;
                    GameBridgeRequest request = null;
                    try
                    {
                        request = JsonSerializer.Deserialize<GameBridgeRequest>(text, JsonOptions);
                        if (request == null) throw new JsonException("请求为空。");
                        var data = await HandleAsync(request, activeToken);
                        await SendAsync(socket, new
                        {
                            ok = true,
                            request_id = request.request_id ?? string.Empty,
                            op = (request.op ?? string.Empty).Trim().ToLowerInvariant(),
                            data
                        }, activeToken);
                    }
                    catch (OperationCanceledException) when (activeToken.IsCancellationRequested) { break; }
                    catch (Exception exception)
                    {
                        await SendAsync(socket, new
                        {
                            ok = false,
                            request_id = request == null ? string.Empty : request.request_id ?? string.Empty,
                            op = request == null ? string.Empty : request.op ?? string.Empty,
                            error = exception.Message,
                            error_detail = exception.ToString()
                        }, activeToken);
                    }
                }
            }
            }
            finally
            {
                System.Threading.Interlocked.Decrement(ref activeConnections);
            }
        }

        private async Task<object> HandleAsync(GameBridgeRequest request, CancellationToken token)
        {
            var op = (request.op ?? string.Empty).Trim().ToLowerInvariant();
            if (op == "hello" || op == "ping")
                return new { protocol = "game.session.v1", server_time_unix_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            if (op == "stardew_install_status")
                return stardewInstaller.GetStatus(request.game_path);
            if (op == "stardew_install")
                return stardewInstaller.BeginInstall(request.game_path);
            if (op == "stardew_launch")
                return stardewInstaller.Launch(request.game_path);
            if (op == "stardew_customize")
                return stardewInstaller.CustomizeAppearance(request.game_path, request.companion,
                    request.sprite_base64, request.portrait_base64);
            if (op == "start")
            {
                var started = await controller.StartAsync(request.conversation_id, request.profile_id,
                    request.game_id, request.title, request.adapter_id, request.role_instruction,
                    Serialize(request.environment, "{}"), true, token);
                return controller.PublicSession(started.Item1, true);
            }
            if (op == "event")
            {
                var record = await controller.AppendEventAsync(new GameEventInput
                {
                    session_id = request.session_id,
                    kind = request.kind,
                    actor = request.actor,
                    content = request.content,
                    payload = request.payload,
                    state = request.state,
                    occurred_unix_ms = request.occurred_unix_ms
                }, token);
                return new { session_id = record.SessionId, seq = record.Seq, accepted = true };
            }
            if (op == "status")
                return controller.PublicSession(controller.Status(request.conversation_id, request.session_id), true);
            if (op == "history")
                return controller.PublicHistory(request.conversation_id, request.session_id, request.take);
            if (op == "end" || op == "abort")
            {
                var ended = await controller.EndAsync(request.session_id, request.conversation_id,
                    op == "abort" || string.Equals(request.mode, "abort", StringComparison.OrdinalIgnoreCase),
                    true, token);
                return controller.PublicSession(ended.Session, false);
            }
            throw new InvalidOperationException("未知的桥接操作：" + op);
        }

        private static async Task<string> ReceiveTextAsync(WebSocket socket, CancellationToken token)
        {
            var buffer = new byte[8192];
            using (var stream = new System.IO.MemoryStream())
            {
                while (true)
                {
                    var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
                            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, token);
                        return null;
                    }
                    if (result.MessageType != WebSocketMessageType.Text)
                        throw new InvalidOperationException("游戏桥接只接受 JSON 文本消息。");
                    stream.Write(buffer, 0, result.Count);
                    if (stream.Length > MaxMessageBytes)
                        throw new InvalidOperationException("游戏桥接消息超过 1 MiB 上限。");
                    if (result.EndOfMessage) return Encoding.UTF8.GetString(stream.ToArray());
                }
            }
        }

        private static Task SendAsync(WebSocket socket, object value, CancellationToken token)
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value));
            return socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);
        }

        private static string Serialize(object value, string fallback)
        {
            if (value == null) return fallback;
            if (value is JsonElement) return ((JsonElement)value).GetRawText();
            return JsonSerializer.Serialize(value);
        }
    }
}
