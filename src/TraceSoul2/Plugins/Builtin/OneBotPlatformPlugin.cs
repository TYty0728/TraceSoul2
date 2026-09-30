using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Logic;
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
        private const string LastSessionDocumentKey = "last_session";
        private const int InputStatusRefreshMilliseconds = 5000;
        // 生图插件允许的最长超时为 30 分钟；异常路径会由中枢 finally 提前停止刷新。
        private const int InputStatusMaximumMilliseconds = 1800000;

        private readonly object gate = new object();
        private readonly object socketGate = new object();
        private readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);
        private readonly Queue<PluginEventData> inbound = new Queue<PluginEventData>();
        private readonly Dictionary<string, long> recentInbound = new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly Queue<KeyValuePair<string, long>> recentInboundOrder = new Queue<KeyValuePair<string, long>>();
        private const int RecentInboundLimit = 4096;
        private const long RecentInboundLifetimeMs = 30 * 60 * 1000;
        private readonly List<WebSocket> reverseSockets = new List<WebSocket>();
        private readonly HashSet<string> learnedSelfIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, TaskCompletionSource<string>> pendingActions =
            new Dictionary<string, TaskCompletionSource<string>>(StringComparer.Ordinal);
        private readonly HashSet<TypingTurnState> activeTypingStates = new HashSet<TypingTurnState>();
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
        private readonly Dictionary<string, WebSocket> accountSockets = new(StringComparer.Ordinal);
        // 暂存消息：文字先暂存，表情追加到结尾，整轮收尾钩子里合并成一条发出。
        // 图片不往文字上拼，生图成功后单独发。
        private sealed class StagedMessage
        {
            public string Text;
            public string SessionType;
            public string SessionId;
        }

        private static StagedMessage Staging(TraceTurnContext turn) =>
            (turn ?? throw new ArgumentNullException(nameof(turn))).Workspace
                .GetOrCreateState(PluginId + ".outbound", () => new StagedMessage());
        private TracePluginServices services;
        private long nextEcho;
        private volatile string lastError = string.Empty;

        public TracePluginMetadataData Metadata { get; } = new TracePluginMetadataData
        {
            Id = PluginId,
            DisplayName = "QQ 平台（OneBot v11 / NapCat）",
            Version = "1.5.1",
            Author = "TraceSoul2",
            Role = PluginRoleValues.Platform,
            PlatformId = BodyIds.Qq,
            Description = "OneBot v11 连接桥（反向 WS 为主，与 AstrBot aiocqhttp 同款）。QQ 消息与规范结构的互译由平台适配器完成；出站由身体路由收口（可关回发）。只做连接与翻译，不做决策。"
        };

        // ---------- 适配器/表达器可见的运行态 ----------

        internal OneBotConfig Config { get { return config; } }
        internal bool TryResolveSession(TraceTurnContext context, out string sessionType, out string sessionId)
        {
            if (context?.Environment != null)
            {
                var environment = context.Environment;
                sessionType = environment.SessionType;
                sessionId = environment.SessionId;
                return EnvironmentLogic.CanDeliver(context.Services.Storage, environment) &&
                    environment.PlatformId == PluginId && sessionType is "private" or "group" &&
                    long.TryParse(sessionId, out var parsed) && parsed > 0 &&
                    (string.IsNullOrWhiteSpace(config.self_id) || config.self_id.Trim() == environment.AccountId);
            }
            // 旧插件/离线检查兼容路径；生产轮次总是提供环境快照。
            if (context?.Moment?.SourcePluginId == PluginId)
            {
                try
                {
                    var source = TraceJson.FromJson<OneBotSessionPayload>(context.Moment.PayloadJson ?? string.Empty);
                    if (source != null && !string.IsNullOrWhiteSpace(source.session_id))
                    {
                        sessionType = source.session_type;
                        sessionId = source.session_id;
                        return true;
                    }
                }
                catch { /* 非会话载荷沿用最后会话兜底 */ }
            }
            lock (gate)
            {
                sessionType = lastSessionType;
                sessionId = lastSessionId;
            }
            if (!string.IsNullOrWhiteSpace(sessionId)) return true;
            LoadLastSession();
            lock (gate)
            {
                sessionType = lastSessionType;
                sessionId = lastSessionId;
            }
            if (!string.IsNullOrWhiteSpace(sessionId)) return true;
            var moments = context == null || context.Services == null || context.Services.Storage == null
                ? null
                : context.Services.Storage.GetRecentMoments(context.ConversationId, 80);
            if (!OneBotSessionMemory.TryFind(moments, out sessionType, out sessionId))
                return false;
            lock (gate)
            {
                lastSessionType = sessionType;
                lastSessionId = sessionId;
            }
            SaveLastSession();
            return true;
        }

        private void LoadLastSession()
        {
            try
            {
                var json = services == null || services.Storage == null
                    ? null
                    : services.Storage.LoadPluginDocument(PluginId, LastSessionDocumentKey);
                if (string.IsNullOrWhiteSpace(json)) return;
                var saved = TraceJson.FromJson<OneBotSessionPayload>(json);
                if (saved == null || string.IsNullOrWhiteSpace(saved.session_id)) return;
                lock (gate)
                {
                    if (!string.IsNullOrWhiteSpace(lastSessionId)) return;
                    lastSessionType = saved.session_type ?? string.Empty;
                    lastSessionId = saved.session_id;
                }
            }
            catch { /* 会话记忆损坏时等下一条入站再记 */ }
        }

        private void SaveLastSession()
        {
            string type;
            string id;
            lock (gate)
            {
                type = lastSessionType;
                id = lastSessionId;
            }
            if (string.IsNullOrWhiteSpace(id) || services == null || services.Storage == null) return;
            try
            {
                services.Storage.SavePluginDocument(
                    PluginId,
                    LastSessionDocumentKey,
                    TraceJson.ToJson(new OneBotSessionPayload
                    {
                        session_type = type,
                        session_id = id
                    }));
            }
            catch { /* 会话记忆写失败不阻断收发 */ }
        }

        internal string LastSessionType { get { lock (gate) return lastSessionType; } }
        internal string LastSessionId { get { lock (gate) return lastSessionId; } }

        private bool IsReverseMode { get { return !string.Equals(config.mode ?? "reverse", "forward", StringComparison.OrdinalIgnoreCase); } }

        public void Register(TracePluginContext context)
        {
            services = context.Services;
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
            // 入站在收件时开启；主动表达从输出开始。整轮文字/表情/图片结束后停止刷新。
            context.Services.ExpressionStartingHooks.Add(StartTypingAsync);
            context.Services.ExpressionCompletedHooks.Add(StopTypingAsync);
            context.Services.EventCompletedHooks.Add(source => StopTypingAsync(source.TraceId));
            LoadLastSession();
            if (!config.enabled) return;
            waitingSinceUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (IsReverseMode)
            {
                context.AddWebSocketEndpoint(new ReverseWsEndpoint(this));
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

        internal void StageText(string text, string sessionType, string sessionId, TraceTurnContext turn)
        {
            var staged = Staging(turn);
            lock (gate)
            {
                staged.Text = text;
                staged.SessionType = sessionType;
                staged.SessionId = sessionId;
            }
        }

        /// <summary>只追加到同轮文字，防止并发环境互相拼接。图片仍单独发送。</summary>
        internal bool TryAppendSegment(string segment, TraceTurnContext turn)
        {
            if (turn == null) return false;
            var staged = Staging(turn);
            lock (gate)
            {
                if (string.IsNullOrWhiteSpace(staged.Text)) return false;
                staged.Text += segment;
                return true;
            }
        }

        private async Task FlushStagedAsync(TraceTurnContext turn)
        {
            if (turn == null) return;
            var staged = Staging(turn);
            string text, sessionType, sessionId;
            lock (gate)
            {
                if (string.IsNullOrWhiteSpace(staged.Text)) return;
                text = staged.Text;
                sessionType = staged.SessionType;
                sessionId = staged.SessionId;
                staged.Text = null;
            }
            if (string.IsNullOrWhiteSpace(sessionId)) return;
            if (turn?.Environment != null && (!TryResolveSession(turn, out var currentType, out var currentId) ||
                currentType != sessionType || currentId != sessionId))
                throw new InvalidOperationException("环境目标已失效，未发送暂存文字。");
            var timer = Stopwatch.StartNew();
            services?.LogTiming(turn == null ? null : turn.TraceId, "QQ 合并文字发送开始",
                detail: "session=" + sessionType);
            await CallActionAsync(sessionType == "group" ? "send_group_msg" : "send_private_msg",
                new Dictionary<string, object>
                {
                    { sessionType == "group" ? "group_id" : "user_id", long.Parse(sessionId) },
                    { "message", text }
                }, turn?.Environment?.AccountId);
            services?.LogTiming(turn == null ? null : turn.TraceId, "QQ 合并文字发送完成",
                timer.ElapsedMilliseconds);
        }

        // ---------- NapCat 好友输入状态（从收件到本轮所有发送结束） ----------

        private Task StartTypingAsync(TraceTurnContext turn)
        {
            if (turn != null && CanOutbound() &&
                TryResolveSession(turn, out var type, out var id) && type == "private")
                StartTyping(turn.TraceId, id, turn.Environment?.AccountId);
            return Task.CompletedTask;
        }

        // 在入队的同一临界区登记，避免处理线程已经收尾才开始刷新；不阻塞 WS 回包线程。
        private void StartTyping(string traceId, string userId, string accountId)
        {
            if (string.IsNullOrWhiteSpace(userId)) return;
            lock (gate)
            {
                if (activeTypingStates.Any(x => x.TraceId == traceId)) return;
                var state = new TypingTurnState
                {
                    TraceId = traceId, UserId = userId, AccountId = accountId,
                    Cancellation = new CancellationTokenSource()
                };
                activeTypingStates.Add(state);
                state.RefreshTask = RefreshTypingAsync(state, traceId);
            }
        }

        private async Task RefreshTypingAsync(TypingTurnState state, string traceId)
        {
            var token = state.Cancellation.Token;
            var startedAt = Stopwatch.StartNew();
            try
            {
                while (!token.IsCancellationRequested && CanOutbound() &&
                       startedAt.ElapsedMilliseconds < InputStatusMaximumMilliseconds)
                {
                    if (!await TrySetInputStatusAsync(state.UserId, 1, traceId, state.AccountId, token)) break;
                    await Task.Delay(InputStatusRefreshMilliseconds, token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            finally
            {
                Interlocked.Exchange(ref state.Stopped, 1);
                RemoveTypingState(state);
                state.Cancellation.Dispose();
                // event_type=0 是「正在说话」，不是取消。正常末条消息清除状态；异常由 QQ 超时恢复。
                services?.LogTiming(traceId, "QQ 正在输入已停止刷新", 0);
            }
        }

        private Task StopTypingAsync(TraceTurnContext turn) => StopTypingAsync(turn?.TraceId);

        private async Task StopTypingAsync(string traceId)
        {
            TypingTurnState state;
            lock (gate) state = activeTypingStates.FirstOrDefault(x => x.TraceId == traceId);
            if (state == null || Interlocked.Exchange(ref state.Stopped, 1) != 0) return;
            try { state.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
            if (state.RefreshTask != null)
                await Task.WhenAny(state.RefreshTask, Task.Delay(1500));
            RemoveTypingState(state);
        }

        private void RemoveTypingState(TypingTurnState state)
        {
            lock (gate) activeTypingStates.Remove(state);
        }

        private async Task<bool> TrySetInputStatusAsync(string userId, int eventType, string traceId, string accountId, CancellationToken token)
        {
            try
            {
                await CallActionAsync("set_input_status", new Dictionary<string, object>
                {
                    { "user_id", userId },
                    { "event_type", eventType }
                }, accountId, token);
                return true;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return false; }
            catch (Exception exception)
            {
                services?.LogTiming(traceId, "QQ 输入状态更新失败", 0,
                    exception.GetType().Name + ": " + exception.Message);
                return false;
            }
        }

        private sealed class TypingTurnState
        {
            public string TraceId;
            public int Stopped;
            public string UserId = string.Empty;
            public string AccountId;
            public CancellationTokenSource Cancellation;
            public Task RefreshTask;
        }

        public void Shutdown()
        {
            stopped = true;
            TypingTurnState[] typingStates;
            lock (gate) typingStates = activeTypingStates.ToArray();
            foreach (var state in typingStates)
            {
                try { state.Cancellation?.Cancel(); } catch { /* ignored */ }
            }
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
            var selfIdMismatch = !string.IsNullOrWhiteSpace(config.self_id) && learned.Length > 0 &&
                !learned.Any(x => string.Equals(x, config.self_id.Trim(), StringComparison.Ordinal));
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
                hint = selfIdMismatch
                    ? "self_id 配置为 " + config.self_id.Trim() +
                      "，但 NapCat 上报的机器人 QQ 是 " + string.Join(", ", learned) +
                      "。请填机器人自身 QQ 或留空，否则入站消息会被过滤。"
                    : waiting
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
                    lock (gate)
                    {
                        learnedSelfIds.Add(selfId.ToString());
                        if (socket != null) accountSockets[selfId.ToString()] = socket;
                    }
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

        internal void HandleInbound(string json, WebSocket sourceSocket)
        {
            var timer = Stopwatch.StartNew();
            var moment = adapter.ConvertInbound(json);
            if (moment == null) return;
            if (string.IsNullOrWhiteSpace(moment.TraceId))
                moment.TraceId = Guid.NewGuid().ToString("N").Substring(0, 8);
            services?.LogTiming(moment.TraceId, "QQ WebSocket 消息解析完成",
                timer.ElapsedMilliseconds,
                "event=" + (moment.ExternalEventId ?? string.Empty) + "｜organ=" + (moment.Organ ?? string.Empty));
            OneBotSessionPayload session = null;
            try { session = TraceJson.FromJson<OneBotSessionPayload>(moment.PayloadJson ?? string.Empty); }
            catch { session = null; }
            var remembered = false;
            lock (gate)
            {
                // 多条 WS 连接及重连重放都可能交付同一个 message_id。判重与入队必须原子执行。
                var now = Environment.TickCount64;
                while (recentInboundOrder.Count > 0 &&
                       now - recentInboundOrder.Peek().Value >= RecentInboundLifetimeMs)
                    recentInbound.Remove(recentInboundOrder.Dequeue().Key);
                var eventId = moment.ExternalEventId;
                if (!string.IsNullOrWhiteSpace(eventId) && eventId != "0")
                {
                    var key = JsonText.ExtractLong(json, "self_id") + ":" +
                              session?.session_type + ":" + session?.session_id + ":" + eventId;
                    if (recentInbound.ContainsKey(key))
                    {
                        services?.LogTiming(moment.TraceId, "QQ 重复消息已忽略", detail: "event=" + eventId);
                        return;
                    }
                    if (recentInboundOrder.Count >= RecentInboundLimit)
                        recentInbound.Remove(recentInboundOrder.Dequeue().Key);
                    recentInbound.Add(key, now);
                    recentInboundOrder.Enqueue(new KeyValuePair<string, long>(key, now));
                }
                if (session != null && !string.IsNullOrWhiteSpace(session.session_id))
                {
                    lastSessionType = session.session_type ?? string.Empty;
                    lastSessionId = session.session_id;
                    remembered = true;
                }
                lastSessionSocket = sourceSocket;
                if (sourceSocket != null && !string.IsNullOrWhiteSpace(moment.Environment?.AccountId))
                    accountSockets[moment.Environment.AccountId] = sourceSocket;
                if (CanOutbound() && session?.session_type == "private")
                    StartTyping(moment.TraceId, session.session_id, moment.Environment?.AccountId);
                inbound.Enqueue(moment);
            }
            if (remembered) SaveLastSession();
            services?.LogTiming(moment.TraceId, "QQ 消息已入后台收件箱");
        }

        // ---------- 收件箱（后台服务 → Brain） ----------

        internal List<PluginEventData> TakeInbound()
        {
            lock (gate)
            {
                var result = new List<PluginEventData>();
                while (inbound.Count > 0 && result.Count < 5) result.Add(inbound.Dequeue());
                return result;
            }
        }

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
                return owner.TakeInbound();
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
                SupportsPublicEnvironment = true,
                Kind = TraceContributionKindValues.Effector,
                DisplayName = "QQ 发文字",
                Description = OneBotPlatformPrompts.TextEffectorDescription,
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
                Description = OneBotPlatformPrompts.ImageEffectorDescription,
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

        internal async Task<string> CallActionAsync(string action, Dictionary<string, object> parameters, string accountId = null, CancellationToken cancellationToken = default)
        {
            if (IsReverseMode) return await CallActionOverSocketAsync(action, parameters, accountId, cancellationToken);
            if (!string.IsNullOrWhiteSpace(config.http_url)) return await CallActionOverHttpAsync(action, parameters, cancellationToken);
            throw new InvalidOperationException("OneBot 未配置任何动作通道（反向模式需 NapCat 已连接；正向模式需 http_url）。");
        }

        private WebSocket LiveReverseSocket(string accountId = null)
        {
            lock (gate)
            {
                if (!string.IsNullOrWhiteSpace(accountId))
                    return accountSockets.TryGetValue(accountId, out var socket) && socket.State == WebSocketState.Open ? socket : null;
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
        private async Task<string> CallActionOverSocketAsync(string action, Dictionary<string, object> parameters, string accountId, CancellationToken cancellationToken)
        {
            var socket = LiveReverseSocket(accountId);
            if (socket == null || socket.State != WebSocketState.Open)
                throw new InvalidOperationException("NapCat 反向连接当前不可用。" + WaitingHint());
            var echo = Interlocked.Increment(ref nextEcho).ToString();
            var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gate) pendingActions[echo] = pending;
            var payload = SerializeActionRequest(action, parameters, echo);
            string response;
            try
            {
                await SendSocketAsync(socket, payload, cancellationToken);
                response = await pending.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }
            finally { lock (gate) pendingActions.Remove(echo); }
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

        private async Task SendSocketAsync(WebSocket socket, string payload, CancellationToken cancellationToken = default)
        {
            var bytes = Encoding.UTF8.GetBytes(payload);
            await sendLock.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                // 已开始写入的帧必须完整发送，避免取消输入状态时中断共享 QQ 连接。
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            finally { sendLock.Release(); }
        }

        /// <summary>正向模式：HTTP 动作（POST {http_url}/{action}），返回原始响应文本。</summary>
        private async Task<string> CallActionOverHttpAsync(string action, Dictionary<string, object> parameters, CancellationToken cancellationToken)
        {
            var body = SerializeActionRequest(action, parameters);
            using (var request = new HttpRequestMessage(HttpMethod.Post, config.http_url.TrimEnd('/') + "/" + action))
            {
                if (!string.IsNullOrWhiteSpace(config.access_token))
                    request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + config.access_token);
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                using (var response = await http.SendAsync(request, cancellationToken))
                {
                    if (!response.IsSuccessStatusCode)
                        throw new InvalidOperationException("OneBot 动作失败：" + action + " → HTTP " + (int)response.StatusCode);
                    return await response.Content.ReadAsStringAsync(cancellationToken);
                }
            }
        }

        /// <summary>动作参数可含消息段数组和嵌套对象，不能用 ToString 把它们变成 CLR 类型名。</summary>
        internal static string SerializeActionRequest(string action, Dictionary<string, object> parameters, string echo = null)
        {
            var request = new Dictionary<string, object>
            {
                ["action"] = action ?? string.Empty,
                ["params"] = parameters ?? new Dictionary<string, object>()
            };
            if (echo != null) request["echo"] = echo;
            return JsonSerializer.Serialize(request);
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

    /// <summary>从已入库的 QQ Moment 找回上次会话，Host 重启后心跳仍能发回原处。</summary>
    public static class OneBotSessionMemory
    {
        public static bool TryFind(IEnumerable<MomentRecord> moments, out string sessionType, out string sessionId)
        {
            sessionType = string.Empty;
            sessionId = string.Empty;
            if (moments == null) return false;
            foreach (var moment in moments.Reverse())
            {
                if (moment == null || string.IsNullOrWhiteSpace(moment.PayloadJson)) continue;
                OneBotSessionPayload session;
                try { session = TraceJson.FromJson<OneBotSessionPayload>(moment.PayloadJson); }
                catch { continue; }
                if (session == null || string.IsNullOrWhiteSpace(session.session_id) ||
                    session.session_id == "0") continue;
                sessionType = string.IsNullOrWhiteSpace(session.session_type) ? "private" : session.session_type;
                sessionId = session.session_id;
                return true;
            }
            return false;
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

        /// <summary>只收这个机器人账号（事件 self_id）的消息；不是对方 user_id；留空 = 都收。</summary>
        public string self_id = string.Empty;

        /// <summary>回发开关：true=Brain 的文字回复自动发回 QQ；false=只收不回（消息照常进 Brain，回复留在控制台）。</summary>
        public bool reply_enabled = true;

        /// <summary>本机 NapCat 启动文件或包含启动文件的目录；仅供 Host WebUI 手动拉起。</summary>
        public string napcat_path = string.Empty;

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
        public List<string> image_urls = new List<string>();
    }
}
