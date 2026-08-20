using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Util;

namespace TraceSoul2.Plugins.Builtin
{
    /// <summary>
    /// QQ 平台（OneBot v11 / NapCat）连接桥。连接模式沿用 AstrBot aiocqhttp 的成熟做法：
    /// - 反向 WS（默认）：我们监听，NapCat 主动连进来（websocketClients → ws://127.0.0.1:{listen_port}/ws）；
    ///   事件与 API 动作都走同一根连接，不需要 httpServers；
    /// - 正向 WS（可选）：我们主动连 NapCat 的 websocketServers，动作走 http_url。
    /// 插件只负责连接/鉴权/收发与平台注入；QQ 消息与规范结构的互译全部在 OneBotPlatformAdapter（平台适配器）。
    /// 配置：数据目录 onebot.json { enabled, mode, listen_port, ws_url, http_url, access_token, self_id, reply_enabled }。
    /// </summary>
    public sealed class OneBotPlatformPlugin : ITracePlugin
    {
        private const string PluginId = "builtin.onebot";

        private readonly object gate = new object();
        private readonly object socketGate = new object();
        private readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);
        private readonly Queue<PluginEventData> inbound = new Queue<PluginEventData>();
        private readonly List<WebSocket> reverseSockets = new List<WebSocket>();
        private readonly HashSet<string> learnedSelfIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, TaskCompletionSource<string>> pendingActions =
            new Dictionary<string, TaskCompletionSource<string>>(StringComparer.Ordinal);
        private ClientWebSocket forwardSocket;
        private CancellationTokenSource forwardCts;
        private HttpClient http;
        private OneBotConfig config = new OneBotConfig();
        private OneBotPlatformAdapter adapter;
        private volatile bool connected;
        private volatile bool stopped;
        private long waitingSinceUnixMs;
        private string lastSessionType = string.Empty;
        private string lastSessionId = string.Empty;
        private WebSocket lastSessionSocket;
        // 暂存消息：文字先暂存，表情/图片追加到结尾，整轮收尾钩子里合并成一条发出。
        private string stagedText;
        private string stagedSessionType;
        private string stagedSessionId;
        private long nextEcho;
        private volatile string lastError = string.Empty;

        public TracePluginMetadataData Metadata { get; } = new TracePluginMetadataData
        {
            Id = PluginId,
            DisplayName = "QQ 平台（OneBot v11 / NapCat）",
            Version = "1.3.0",
            Author = "TraceSoul2",
            Role = PluginRoleValues.Platform,
            PlatformId = BodyIds.Qq,
            Description = "OneBot v11 连接桥（反向 WS 为主，与 AstrBot aiocqhttp 同款）。QQ 消息与规范结构的互译由平台适配器完成；出站由身体路由收口（可关回发）。只做连接与翻译，不做决策。"
        };

        // ---------- 适配器/表达器可见的运行态 ----------

        internal OneBotConfig Config { get { return config; } }
        internal string LastSessionType { get { lock (gate) return lastSessionType; } }
        internal string LastSessionId { get { lock (gate) return lastSessionId; } }

        private bool IsReverseMode { get { return !string.Equals(config.mode ?? "reverse", "forward", StringComparison.OrdinalIgnoreCase); } }

        public void Register(TracePluginContext context)
        {
            context.Services.Platforms.Register(new PlatformHandle
            {
                Id = "onebot",
                DisplayName = "QQ（OneBot v11 / NapCat）",
                IsConnected = () => connected,
                Details = () => StatusDetails()
            });
            config = OneBotConfig.Load(context.Services.DataDirectory);
            adapter = new OneBotPlatformAdapter(this);
            http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            // 平台适配器注册表：其它感官插件（表情/TTS/生图/说说）通过它把表达发到 QQ。
            context.Services.PlatformAdapters.RemoveAll(x => x.PlatformId == adapter.PlatformId);
            context.Services.PlatformAdapters.Add(adapter);
            context.AddCallable(new OneBotTextEffector(this));
            context.AddCallable(new OneBotImageEffector(this));
            context.AddBackgroundService(new OneBotInboxService(this));
            // 整轮收尾：把暂存的文字（可能带结尾表情）合并成一条 QQ 消息发出。
            context.Services.TurnCompleteHooks.Add(FlushStagedAsync);
            if (!config.enabled) return;
            waitingSinceUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (IsReverseMode)
            {
                context.Services.WebSocketEndpoints.Add(new ReverseWsEndpoint(this));
            }
            else if (!string.IsNullOrWhiteSpace(config.ws_url))
            {
                forwardCts = new CancellationTokenSource();
                _ = ConnectLoopAsync(forwardCts.Token);
            }
        }

        /// <summary>QQ 出站是否打开：启用 + 回发开关 + 已连接。关回发时文字下滑到控制台。</summary>
        internal bool CanOutbound()
        {
            return config.enabled && config.reply_enabled && connected;
        }

        // ---------- 消息组装（文字暂存 + 结尾表情追加 + 整轮合并发送） ----------

        internal void StageText(string text, string sessionType, string sessionId)
        {
            lock (gate)
            {
                stagedText = text;
                stagedSessionType = sessionType;
                stagedSessionId = sessionId;
            }
        }

        /// <summary>把表情/图片段追加到暂存文字结尾；没有暂存文字返回 false（走单独发送）。</summary>
        internal bool TryAppendSegment(string segment)
        {
            lock (gate)
            {
                if (string.IsNullOrWhiteSpace(stagedText)) return false;
                stagedText += segment;
                return true;
            }
        }

        private async Task FlushStagedAsync(TraceTurnContext turn)
        {
            string text, sessionType, sessionId;
            lock (gate)
            {
                if (string.IsNullOrWhiteSpace(stagedText)) return;
                text = stagedText;
                sessionType = stagedSessionType;
                sessionId = stagedSessionId;
                stagedText = null;
            }
            if (string.IsNullOrWhiteSpace(sessionId)) return;
            await CallActionAsync(sessionType == "group" ? "send_group_msg" : "send_private_msg",
                new Dictionary<string, object>
                {
                    { sessionType == "group" ? "group_id" : "user_id", long.Parse(sessionId) },
                    { "message", text }
                });
        }

        public void Shutdown()
        {
            stopped = true;
            try { forwardCts?.Cancel(); } catch { /* ignored */ }
            try { forwardSocket?.Dispose(); } catch { /* ignored */ }
            try { http?.Dispose(); } catch { /* ignored */ }
            lock (socketGate)
            {
                foreach (var socket in reverseSockets)
                {
                    try { socket.Abort(); } catch { /* ignored */ }
                }
                reverseSockets.Clear();
            }
        }

        private object StatusDetails()
        {
            string[] learned;
            int sockets;
            lock (gate) learned = learnedSelfIds.ToArray();
            lock (socketGate) sockets = reverseSockets.Count;
            var waiting = config.enabled && !connected;
            var waitingSeconds = waiting && waitingSinceUnixMs > 0
                ? Math.Max(0, (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - waitingSinceUnixMs) / 1000)
                : 0;
            return new
            {
                mode = IsReverseMode ? "reverse" : "forward",
                enabled = config.enabled,
                replyEnabled = config.reply_enabled,
                listenPort = config.listen_port,
                napcatUrl = "ws://127.0.0.1:" + config.listen_port + "/ws",
                wsUrl = config.ws_url,
                httpUrl = config.http_url,
                hasToken = !string.IsNullOrWhiteSpace(config.access_token),
                selfId = config.self_id,
                learnedSelfIds = learned,
                socketCount = sockets,
                lastError = lastError,
                waitingReconnect = waiting,
                waitingSeconds,
                hint = waiting
                    ? "等待 NapCat 重连（已等 " + waitingSeconds + " 秒，通常约 30 秒内连上）"
                    : string.Empty
            };
        }

        // ---------- 反向 WS 端点（宿主挂载，AstrBot aiocqhttp 同款） ----------

        private sealed class ReverseWsEndpoint : ITraceWebSocketEndpoint
        {
            private readonly OneBotPlatformPlugin owner;
            public ReverseWsEndpoint(OneBotPlatformPlugin owner) { this.owner = owner; }
            public string Path { get { return "/ws"; } }
            public bool Accept(string authorizationHeader, string queryString)
            {
                return owner.CheckToken(authorizationHeader, queryString);
            }
            public Task OnConnectedAsync(WebSocket socket, CancellationToken token)
            {
                return owner.ServeReverseSocketAsync(socket, token);
            }
        }

        /// <summary>反向 WS 鉴权：配置了 token 时，NapCat 必须带上其一（Authorization: Bearer 或 access_token 查询参数）。</summary>
        private bool CheckToken(string authorizationHeader, string queryString)
        {
            var configured = SplitTokens(config.access_token);
            if (configured.Count == 0) return true;
            var presented = new HashSet<string>(StringComparer.Ordinal);
            foreach (var part in (authorizationHeader ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var value = part.Trim();
                if (value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) value = value.Substring(7).Trim();
                else if (value.StartsWith("Token ", StringComparison.OrdinalIgnoreCase)) value = value.Substring(6).Trim();
                if (value.Length > 0) presented.Add(value);
            }
            foreach (var pair in (queryString ?? string.Empty).TrimStart('?')
                         .Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                if (eq <= 0 || pair.Substring(0, eq) != "access_token") continue;
                presented.Add(Uri.UnescapeDataString(pair.Substring(eq + 1)));
            }
            return presented.Overlaps(configured);
        }

        private static HashSet<string> SplitTokens(string raw)
        {
            return new HashSet<string>(
                (raw ?? string.Empty)
                    .Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim())
                    .Where(x => x.Length > 0),
                StringComparer.Ordinal);
        }

        private async Task ServeReverseSocketAsync(WebSocket socket, CancellationToken token)
        {
            lock (socketGate) reverseSockets.Add(socket);
            try
            {
                connected = true;
                waitingSinceUnixMs = 0;
                lastError = string.Empty;
                var buffer = new byte[65536];
                while (!stopped && !token.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    var builder = new StringBuilder();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                        if (result.MessageType == WebSocketMessageType.Close) break;
                        builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    } while (!result.EndOfMessage);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    HandleSocketFrame(builder.ToString(), socket);
                }
            }
            catch (OperationCanceledException)
            {
                /* 宿主关闭 */
            }
            catch (Exception exception)
            {
                lastError = exception.Message;
            }
            finally
            {
                lock (socketGate) reverseSockets.Remove(socket);
                lock (gate)
                {
                    if (ReferenceEquals(lastSessionSocket, socket)) lastSessionSocket = null;
                }
                connected = reverseSockets.Count > 0 && reverseSockets.Any(x => x.State == WebSocketState.Open);
                if (!connected && waitingSinceUnixMs == 0)
                    waitingSinceUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                try { socket.Dispose(); } catch { /* ignored */ }
            }
        }

        /// <summary>反向连接上的一帧：API 动作回包（echo）→ 完成挂起动作；meta_event → 学 self_id；消息事件 → 适配器翻译后进收件箱。</summary>
        private void HandleSocketFrame(string json, WebSocket socket)
        {
            if (json.Contains("\"echo\""))
            {
                var echo = JsonText.ExtractString(json, "echo");
                if (string.IsNullOrWhiteSpace(echo)) echo = JsonText.ExtractLong(json, "echo").ToString();
                if (string.IsNullOrWhiteSpace(echo)) return;
                lock (gate)
                {
                    TaskCompletionSource<string> pending;
                    if (pendingActions.TryGetValue(echo, out pending))
                    {
                        pendingActions.Remove(echo);
                        pending.TrySetResult(json);
                    }
                }
                return;
            }
            var postType = JsonText.ExtractString(json, "post_type");
            if (postType == "meta_event")
            {
                var selfId = JsonText.ExtractLong(json, "self_id");
                if (selfId > 0)
                {
                    lock (gate) learnedSelfIds.Add(selfId.ToString());
                }
                return;
            }
            if (postType != "message") return;
            HandleInbound(json, socket);
        }

        // ---------- 正向 WS 连接（可选模式） ----------

        private async Task ConnectLoopAsync(CancellationToken token)
        {
            while (!stopped && !token.IsCancellationRequested)
            {
                try
                {
                    using (var ws = new ClientWebSocket())
                    {
                        forwardSocket = ws;
                        if (!string.IsNullOrWhiteSpace(config.access_token))
                            ws.Options.SetRequestHeader("Authorization", "Bearer " + config.access_token);
                        await ws.ConnectAsync(new Uri(config.ws_url), token);
                        connected = true;
                        waitingSinceUnixMs = 0;
                        lastError = string.Empty;
                        var buffer = new byte[65536];
                        while (ws.State == WebSocketState.Open && !token.IsCancellationRequested)
                        {
                            var builder = new StringBuilder();
                            WebSocketReceiveResult result;
                            do
                            {
                                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                                if (result.MessageType == WebSocketMessageType.Close) break;
                                builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                            } while (!result.EndOfMessage);
                            if (result.MessageType == WebSocketMessageType.Close) break;
                            HandleSocketFrame(builder.ToString(), ws);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception exception)
                {
                    lastError = exception.Message;
                }
                connected = false;
                if (waitingSinceUnixMs == 0)
                    waitingSinceUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (stopped || token.IsCancellationRequested) break;
                await Task.Delay(5000, token);
            }
        }

        // ---------- 入站收口：适配器翻译成规范 Moment 后进收件箱 ----------

        private void HandleInbound(string json, WebSocket sourceSocket)
        {
            var moment = adapter.ConvertInbound(json);
            if (moment == null) return;
            OneBotSessionPayload session = null;
            try { session = TraceJson.FromJson<OneBotSessionPayload>(moment.PayloadJson ?? string.Empty); }
            catch { session = null; }
            lock (gate)
            {
                if (session != null && !string.IsNullOrWhiteSpace(session.session_id))
                {
                    lastSessionType = session.session_type ?? string.Empty;
                    lastSessionId = session.session_id;
                }
                lastSessionSocket = sourceSocket;
                inbound.Enqueue(moment);
            }
        }

        // ---------- 收件箱（后台服务 → Brain） ----------

        private sealed class OneBotInboxService : ITraceBackgroundService
        {
            private readonly OneBotPlatformPlugin owner;
            public OneBotInboxService(OneBotPlatformPlugin owner) { this.owner = owner; }

            public TraceContributionDescriptorData Descriptor { get; } = new TraceContributionDescriptorData
            {
                Id = "qq.inbox.service",
                DisplayName = "QQ 收件箱",
                Description = "把 QQ 新消息排队交给 Brain 逐条处理。",
                Provides = "platform.qq.inbox"
            };

            public bool IsAvailable { get { return owner.connected; } }

            public IEnumerable<PluginEventData> Poll(long nowUnixMs)
            {
                lock (owner.gate)
                {
                    var result = new List<PluginEventData>();
                    while (owner.inbound.Count > 0 && result.Count < 5)
                        result.Add(owner.inbound.Dequeue());
                    return result;
                }
            }

            public void Shutdown() { }
        }

        // ---------- 表达器（薄壳：组装规范表达消息，翻译与收发交给适配器） ----------

        private sealed class OneBotTextEffector : ITraceCallableContribution
        {
            private readonly OneBotPlatformPlugin owner;
            public OneBotTextEffector(OneBotPlatformPlugin owner) { this.owner = owner; }

            public TraceContributionDescriptorData Descriptor { get; } = new TraceContributionDescriptorData
            {
                Id = "qq.text.send",
                Kind = TraceContributionKindValues.Effector,
                DisplayName = "QQ 发文字",
                Description = "把 Brain 的文字回复发到当前 QQ 会话。",
                Provides = "expression.qq.text",
                Boundary = "QQ文字｜自由文本（回复当前QQ会话）",
                BodyId = BodyIds.Qq,
                BodyTier = BodyTierValues.Chat,
                Organ = BodyOrganValues.Text,
                ParametersJsonSchema = "{text:string}",
                HasExternalSideEffect = true
            };

            public bool IsAvailable(TraceTurnContext context) { return context != null && owner.CanOutbound(); }

            public Task<TraceCapabilityResultData> ExecuteAsync(
                BrainCapabilityCallData call, TraceTurnContext context, CancellationToken cancellationToken)
            {
                return owner.adapter.SendAsync(new TraceOutboundMessageData
                {
                    Kind = TraceOutboundKinds.Text,
                    Text = call.GetArgument("text").Trim()
                }, context, cancellationToken);
            }
        }

        private sealed class OneBotImageEffector : ITraceCallableContribution
        {
            private readonly OneBotPlatformPlugin owner;
            public OneBotImageEffector(OneBotPlatformPlugin owner) { this.owner = owner; }

            public TraceContributionDescriptorData Descriptor { get; } = new TraceContributionDescriptorData
            {
                Id = "qq.image.send",
                Kind = TraceContributionKindValues.Effector,
                DisplayName = "QQ 发图片",
                Description = "把一张图片发到当前 QQ 会话（file 为本地路径或 URL）。",
                Provides = "expression.qq.image",
                Boundary = "QQ图片｜发一张图（给 file 路径或 URL）",
                BodyId = BodyIds.Qq,
                BodyTier = BodyTierValues.Chat,
                Organ = BodyOrganValues.Image,
                ParametersJsonSchema = "{file:string}",
                HasExternalSideEffect = true
            };

            public bool IsAvailable(TraceTurnContext context) { return context != null && owner.CanOutbound(); }

            public Task<TraceCapabilityResultData> ExecuteAsync(
                BrainCapabilityCallData call, TraceTurnContext context, CancellationToken cancellationToken)
            {
                return owner.adapter.SendAsync(new TraceOutboundMessageData
                {
                    Kind = TraceOutboundKinds.Image,
                    File = call.GetArgument("file").Trim()
                }, context, cancellationToken);
            }
        }

        // ---------- OneBot API 动作（传输层） ----------

        internal async Task<string> CallActionAsync(string action, Dictionary<string, object> parameters)
        {
            if (IsReverseMode) return await CallActionOverSocketAsync(action, parameters);
            if (!string.IsNullOrWhiteSpace(config.http_url)) return await CallActionOverHttpAsync(action, parameters);
            throw new InvalidOperationException("OneBot 未配置任何动作通道（反向模式需 NapCat 已连接；正向模式需 http_url）。");
        }

        private WebSocket LiveReverseSocket()
        {
            lock (gate)
            {
                if (lastSessionSocket != null && lastSessionSocket.State == WebSocketState.Open)
                    return lastSessionSocket;
            }
            lock (socketGate)
            {
                return reverseSockets.FirstOrDefault(x => x != null && x.State == WebSocketState.Open);
            }
        }

        private string WaitingHint()
        {
            if (connected) return string.Empty;
            var seconds = waitingSinceUnixMs <= 0
                ? 0
                : Math.Max(0, (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - waitingSinceUnixMs) / 1000);
            return "等待 NapCat 重连（已等 " + seconds + " 秒，通常约 30 秒内连上）";
        }

        /// <summary>反向模式：API 动作写回 NapCat 主动连进来的那根连接（aiocqhttp 同款），用 echo 配对回包。</summary>
        private async Task<string> CallActionOverSocketAsync(string action, Dictionary<string, object> parameters)
        {
            var socket = LiveReverseSocket();
            if (socket == null || socket.State != WebSocketState.Open)
                throw new InvalidOperationException("NapCat 反向连接当前不可用。" + WaitingHint());
            var echo = Interlocked.Increment(ref nextEcho).ToString();
            var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gate) pendingActions[echo] = pending;
            var payload = "{\"action\":\"" + Escape(action) + "\",\"params\":{" +
                          string.Join(",", BuildParams(parameters)) + "},\"echo\":\"" + echo + "\"}";
            await SendSocketAsync(socket, payload);
            var finished = await Task.WhenAny(pending.Task, Task.Delay(30000));
            if (finished != pending.Task)
            {
                lock (gate) pendingActions.Remove(echo);
                throw new TimeoutException("OneBot 动作超时（30s）：" + action);
            }
            var response = await pending.Task;
            var status = JsonText.ExtractString(response, "status");
            var retcode = JsonText.ExtractLong(response, "retcode");
            if (status != "ok" || retcode != 0)
            {
                var message = JsonText.ExtractString(response, "msg");
                if (string.IsNullOrWhiteSpace(message)) message = JsonText.ExtractString(response, "message");
                if (string.IsNullOrWhiteSpace(message)) message = JsonText.ExtractString(response, "wording");
                throw new InvalidOperationException("OneBot 动作失败：" + action + " → retcode " + retcode +
                                                    (string.IsNullOrWhiteSpace(message) ? string.Empty : "（" + message + "）"));
            }
            return response;
        }

        private async Task SendSocketAsync(WebSocket socket, string payload)
        {
            var bytes = Encoding.UTF8.GetBytes(payload);
            await sendLock.WaitAsync();
            try
            {
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            finally { sendLock.Release(); }
        }

        /// <summary>正向模式：HTTP 动作（POST {http_url}/{action}），返回原始响应文本。</summary>
        private async Task<string> CallActionOverHttpAsync(string action, Dictionary<string, object> parameters)
        {
            var body = "{\"action\":\"" + action + "\",\"params\":{" +
                       string.Join(",", BuildParams(parameters)) + "}}";
            using (var request = new HttpRequestMessage(HttpMethod.Post, config.http_url.TrimEnd('/') + "/" + action))
            {
                if (!string.IsNullOrWhiteSpace(config.access_token))
                    request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + config.access_token);
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                using (var response = await http.SendAsync(request))
                {
                    if (!response.IsSuccessStatusCode)
                        throw new InvalidOperationException("OneBot 动作失败：" + action + " → HTTP " + (int)response.StatusCode);
                    return await response.Content.ReadAsStringAsync();
                }
            }
        }

        private static IEnumerable<string> BuildParams(Dictionary<string, object> parameters)
        {
            foreach (var pair in parameters ?? new Dictionary<string, object>())
                yield return "\"" + pair.Key + "\":" + ParamValue(pair.Value);
        }

        private static string ParamValue(object value)
        {
            if (value == null) return "null";
            if (value is bool flag) return flag ? "true" : "false";
            if (value is long || value is int || value is short || value is byte) return value.ToString();
            if (value is double || value is float) return Convert.ToDouble(value).ToString(CultureInfo.InvariantCulture);
            return "\"" + Escape(value.ToString()) + "\"";
        }

        private static string Escape(string value)
        {
            return (value ?? string.Empty)
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n");
        }
    }

    [Serializable]
    public sealed class OneBotConfig
    {
        /// <summary>总开关。没有 onebot.json 时默认停用（在控制台保存配置后才启用）。</summary>
        public bool enabled = false;

        /// <summary>reverse：NapCat 主动连我们（AstrBot aiocqhttp 同款，推荐）；forward：我们主动连 NapCat。</summary>
        public string mode = "reverse";

        /// <summary>反向模式监听端口（NapCat websocketClients 里填 ws://127.0.0.1:{listen_port}/ws）。</summary>
        public int listen_port = 9021;

        /// <summary>正向模式：NapCat websocketServers 地址。</summary>
        public string ws_url = "ws://127.0.0.1:3001";

        /// <summary>正向模式：HTTP 动作地址；反向模式留空（动作走同一根 WS 连接）。</summary>
        public string http_url = "http://127.0.0.1:3000";

        /// <summary>Access Token；可填多个（逗号/分号/换行分隔），与 NapCat websocketClients 里的 token 对应。</summary>
        public string access_token = string.Empty;

        /// <summary>只收这个 QQ（self_id）的消息；留空 = 都收。</summary>
        public string self_id = string.Empty;

        /// <summary>回发开关：true=Brain 的文字回复自动发回 QQ；false=只收不回（消息照常进 Brain，回复留在控制台）。</summary>
        public bool reply_enabled = true;

        public static OneBotConfig Load(string dataDirectory)
        {
            var config = new OneBotConfig();
            try
            {
                var path = Path.Combine(dataDirectory ?? string.Empty, "onebot.json");
                if (File.Exists(path))
                {
                    var raw = File.ReadAllText(path, Encoding.UTF8);
                    var loaded = TraceJson.FromJson<OneBotConfig>(raw);
                    if (loaded != null) config = loaded;
                    // 旧配置没有 reply_enabled 字段：反序列化会填默认 false，这里补回 true（历史行为=回发）。
                    if (raw.IndexOf("reply_enabled", StringComparison.Ordinal) < 0)
                        config.reply_enabled = true;
                }
            }
            catch { /* 配置损坏按默认处理 */ }
            if (string.IsNullOrWhiteSpace(config.mode))
            {
                // 旧版配置只有正向 WS 字段：有 ws_url 视为 forward，否则按默认 reverse。
                config.mode = string.IsNullOrWhiteSpace(config.ws_url) ? "reverse" : "forward";
            }
            if (config.listen_port <= 0) config.listen_port = 9021;
            return config;
        }
    }

    [Serializable]
    public sealed class OneBotSessionPayload
    {
        public string session_type;
        public string session_id;
        public string nickname;
    }
}
