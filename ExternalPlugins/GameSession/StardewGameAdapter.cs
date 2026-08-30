using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Manager;
using TraceSoul2.Plugins;

namespace TraceSoul2.ExternalPlugins.GameSession
{
    internal sealed class StardewStartupResult
    {
        public string Content { get; set; }
        public string StateJson { get; set; }
        public string Companion { get; set; }
        public string DisplayName { get; set; }
        public string ControlMode { get; set; }
        public bool AgentEnabled { get; set; }
    }

    internal sealed class StardewAdapterEvent
    {
        public string SessionId { get; set; }
        public string Kind { get; set; }
        public string Actor { get; set; }
        public string Content { get; set; }
        public string PayloadJson { get; set; }
        public string StateJson { get; set; }
        public long OccurredUnixMs { get; set; }
    }

    /// <summary>Owns MCP processes and the optional local Player-mode decision loop.</summary>
    internal sealed class StardewGameAdapter : IDisposable
    {
        private readonly string dataDirectory;
        private readonly TracePluginServices services;
        private readonly ConcurrentDictionary<string, StardewRuntime> runtimes =
            new ConcurrentDictionary<string, StardewRuntime>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, byte> starting =
            new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, ConcurrentQueue<StardewAdapterEvent>> events =
            new ConcurrentDictionary<string, ConcurrentQueue<StardewAdapterEvent>>(StringComparer.Ordinal);
        private readonly CancellationTokenSource shutdown = new CancellationTokenSource();
        private bool disposed;

        public StardewGameAdapter(string dataDirectory, TracePluginServices services)
        {
            this.dataDirectory = dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory));
            this.services = services ?? throw new ArgumentNullException(nameof(services));
        }

        public bool CanHandle(GameSessionRecord session)
        {
            return session != null &&
                   string.Equals(session.ProfileId, "stardew-valley", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Number of live Stardew stdio clients whose SMAPI bridge is still refreshing.
        /// This is the actual game-body connection, unlike the short-lived WebUI WebSocket.
        /// </summary>
        public int ConnectedRuntimeCount
        {
            get
            {
                return runtimes.Values.Count(runtime => runtime != null &&
                    runtime.Client.IsRunning && runtime.BridgeOnline);
            }
        }

        public async Task<StardewStartupResult> StartSessionAsync(
            GameSessionRecord session, CancellationToken token)
        {
            if (!CanHandle(session)) return null;
            if (disposed) throw new ObjectDisposedException(nameof(StardewGameAdapter));
            await StopSessionAsync(session.Id, false, CancellationToken.None);
            var settings = LoadSettings(session);
            ReadFreshBridge(settings.BridgePath);
            var client = new StardewMcpClient(settings.Command, settings.Arguments,
                settings.Environment, message => services.LogTiming(null, message));
            try
            {
                await client.StartAsync(token);
                await client.CallToolAsync("stardew_get_state", new { }, token);
                await client.CallToolAsync("stardew_spawn", new
                {
                    target = settings.Companion,
                    displayName = settings.DisplayName,
                    appearance = settings.NativeAppearance ? new
                    {
                        native = true,
                        gender = settings.Gender,
                        skin = settings.Skin,
                        hair = settings.Hair,
                        shirt = settings.Shirt,
                        pants = settings.Pants,
                        accessory = settings.Accessory
                    } : null
                }, token);
                await WaitForCompanionAsync(settings.BridgePath, settings.Companion,
                    null, TimeSpan.FromSeconds(12), token);
                await client.CallToolAsync("stardew_set_mode", new
                {
                    target = settings.Companion,
                    mode = settings.ControlMode
                }, token);
                var confirmed = await WaitForCompanionAsync(settings.BridgePath, settings.Companion,
                    settings.ControlMode, TimeSpan.FromSeconds(8), token);
                var agent = settings.ControlMode == "player" ? ResolveAgent(settings) : null;
                var runtime = new StardewRuntime(session, settings, client, agent, confirmed,
                    CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token));
                if (!runtimes.TryAdd(session.Id, runtime))
                    throw new InvalidOperationException("这场星露谷会话的适配器已经在运行。");
                runtime.Loop = Task.Run(() => RunLoopAsync(runtime), runtime.Cancellation.Token);
                using var confirmedDocument = JsonDocument.Parse(confirmed);
                var player = ReadString(confirmedDocument.RootElement, "player", "name");
                var content = "游戏侧已确认显示名为“" + settings.DisplayName + "”的角色（内部 " +
                              settings.Companion + "）已生成" +
                              (settings.ControlMode == "player"
                                  ? "并进入 Player 模式；身份提示已绑定到本地游戏 Agent"
                                  : "并进入 Follow 模式；移动由 SMAPI Mod 的跟随状态机执行，不调用人格模型") +
                              (string.IsNullOrWhiteSpace(player) ? "。" : "，现在和玩家“" + player + "”在同一局。");
                services.LogTiming(null, "星露谷游戏 Agent 已连接", detail:
                    "session=" + session.Id.Substring(0, 8) + "｜companion=" + settings.Companion +
                    "｜display=" + settings.DisplayName + "｜mode=" + settings.ControlMode);
                return new StardewStartupResult
                {
                    Content = content.Trim(),
                    StateJson = confirmed,
                    Companion = settings.Companion,
                    DisplayName = settings.DisplayName,
                    ControlMode = settings.ControlMode,
                    AgentEnabled = agent != null
                };
            }
            catch
            {
                if (runtimes.TryRemove(session.Id, out var runtime)) runtime.Dispose();
                client.Dispose();
                throw;
            }
        }

        public void EnsureRunning(GameSessionRecord session)
        {
            if (!CanHandle(session) || disposed || runtimes.ContainsKey(session.Id) ||
                !starting.TryAdd(session.Id, 0)) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await StartSessionAsync(session, shutdown.Token);
                    Enqueue(session.Id, "adapter_reconnected", "system",
                        "Stardew MCP 已恢复连接；" + result.Content, "{}", result.StateJson);
                }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
                catch (Exception exception)
                {
                    Enqueue(session.Id, "adapter_error", "system",
                        "Stardew MCP 重连失败：" + exception.Message, "{}", "{}");
                    services.LogTiming(null, "星露谷游戏 Agent 重连失败", detail: exception.Message);
                }
                finally { starting.TryRemove(session.Id, out _); }
            });
        }

        public object PublicStatus(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId) || !runtimes.TryGetValue(sessionId, out var runtime))
                return null;
            return new
            {
                connected = runtime.Client.IsRunning && runtime.BridgeOnline,
                bridge_online = runtime.BridgeOnline,
                mcp_running = runtime.Client.IsRunning,
                companion = runtime.Settings.Companion,
                display_name = runtime.Settings.DisplayName,
                appearance_mode = runtime.Settings.NativeAppearance ? "native" : "sprite",
                appearance = runtime.Settings.NativeAppearance ? new
                {
                    gender = runtime.Settings.Gender,
                    skin = runtime.Settings.Skin,
                    hair = runtime.Settings.Hair,
                    shirt = runtime.Settings.Shirt,
                    pants = runtime.Settings.Pants,
                    accessory = runtime.Settings.Accessory
                } : null,
                control_mode = runtime.Settings.ControlMode,
                agent_provider_id = runtime.Settings.AgentProviderId,
                agent_model = runtime.Settings.AgentModel,
                agent_enabled = runtime.Agent != null,
                agent_fallback_used = runtime.FallbackAttempted,
                last_bridge_unix_ms = runtime.LastBridgeUnixMs,
                last_decision_unix_ms = runtime.LastDecisionUnixMs,
                error = runtime.LastError ?? string.Empty
            };
        }

        public List<StardewAdapterEvent> DrainEvents(string sessionId)
        {
            var result = new List<StardewAdapterEvent>();
            if (string.IsNullOrWhiteSpace(sessionId) || !events.TryGetValue(sessionId, out var queue))
                return result;
            while (queue.TryDequeue(out var item)) result.Add(item);
            return result;
        }

        public async Task StopSessionAsync(string sessionId, bool leaveIdle, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(sessionId) || !runtimes.TryRemove(sessionId, out var runtime)) return;
            if (leaveIdle && runtime.Client.IsRunning)
            {
                try { await runtime.Client.CallToolAsync("stardew_stay", new { }, token); }
                catch (Exception exception)
                {
                    services.LogTiming(null, "星露谷同伴停止失败", detail: exception.Message);
                }
            }
            runtime.Dispose();
        }

        private async Task RunLoopAsync(StardewRuntime runtime)
        {
            var token = runtime.Cancellation.Token;
            var previous = runtime.LastStateJson;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(runtime.Settings.SensorPollMs, token);
                    if (!runtime.Client.IsRunning)
                        throw new InvalidOperationException("Stardew MCP Server 已退出，等待自动重连。");
                    string current;
                    try
                    {
                        current = ReadFreshBridge(runtime.Settings.BridgePath);
                        runtime.BridgeOnline = true;
                        runtime.LastBridgeUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        runtime.LastError = string.Empty;
                    }
                    catch (Exception exception)
                    {
                        runtime.BridgeOnline = false;
                        if (!string.Equals(runtime.LastError, exception.Message, StringComparison.Ordinal))
                        {
                            runtime.LastError = exception.Message;
                            Enqueue(runtime.Session.Id, "bridge_offline", "system",
                                "星露谷桥接暂时离线：" + exception.Message, "{}", "{}");
                        }
                        continue;
                    }

                    var change = DescribeChange(previous, current, runtime.Settings.Companion,
                        runtime.Settings.DisplayName);
                    if (!string.IsNullOrWhiteSpace(change))
                        Enqueue(runtime.Session.Id, "state_change", "world", change, "{}", current);
                    previous = current;
                    runtime.LastStateJson = current;

                    if (runtime.Agent == null) continue;
                    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    if (now - runtime.LastDecisionUnixMs < runtime.Settings.DecisionIntervalMs) continue;
                    runtime.LastDecisionUnixMs = now;
                    try
                    {
                        using var decisionTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                        decisionTimeout.CancelAfter(runtime.Settings.AgentDecisionTimeoutMs);
                        try
                        {
                            await RunDecisionAsync(runtime, current, decisionTimeout.Token);
                        }
                        catch (OperationCanceledException) when (!token.IsCancellationRequested)
                        {
                            throw new TimeoutException("本地游戏 Agent 单步决策超时。");
                        }
                        runtime.LastError = string.Empty;
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception exception)
                    {
                        if (TryFallbackAgent(runtime, exception))
                        {
                            runtime.LastDecisionUnixMs = 0;
                            runtime.LastError = string.Empty;
                            continue;
                        }
                        runtime.LastError = exception.Message;
                        services.LogTiming(null, "本地游戏 Agent 单步决策失败", detail: exception.Message);
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception exception)
            {
                runtime.LastError = exception.Message;
                Enqueue(runtime.Session.Id, "agent_error", "system",
                    "本地游戏 Agent 已暂停：" + exception.Message, "{}", runtime.LastStateJson);
                services.LogTiming(null, "本地游戏 Agent 循环失败", detail: exception.Message);
            }
            finally
            {
                if (runtimes.TryGetValue(runtime.Session.Id, out var current) &&
                    ReferenceEquals(current, runtime) && runtimes.TryRemove(runtime.Session.Id, out _))
                    runtime.Dispose();
            }
        }

        private bool TryFallbackAgent(StardewRuntime runtime, Exception failure)
        {
            if (runtime.FallbackAttempted || services.Providers == null) return false;
            LlmEndpointData endpoint;
            try { endpoint = services.Providers.Resolve(runtime.Settings.AgentProviderId, runtime.Settings.AgentModel); }
            catch { return false; }
            var localOllama = endpoint != null && ((endpoint.ProviderId ?? string.Empty)
                .IndexOf("ollama", StringComparison.OrdinalIgnoreCase) >= 0 ||
                (endpoint.BaseUrl ?? string.Empty).IndexOf("127.0.0.1:11434", StringComparison.OrdinalIgnoreCase) >= 0 ||
                (endpoint.BaseUrl ?? string.Empty).IndexOf("localhost:11434", StringComparison.OrdinalIgnoreCase) >= 0);
            if (!localOllama) return false;
            runtime.FallbackAttempted = true;
            var provider = services.Providers.ListBrief().FirstOrDefault(item =>
                string.Equals(item.Id, runtime.Settings.AgentProviderId, StringComparison.OrdinalIgnoreCase));
            var models = provider?.Models?.Where(item => item.Enabled && item.Roles != null &&
                    item.Roles.Any(role => string.Equals(role, "chat", StringComparison.OrdinalIgnoreCase)) &&
                    !string.Equals(item.Id, runtime.Settings.AgentModel, StringComparison.OrdinalIgnoreCase))
                .Select(item => item.Id).Where(value => !string.IsNullOrWhiteSpace(value)).ToList()
                ?? new List<string>();
            var fallback = models.FirstOrDefault(value => string.Equals(value, "qwen2.5:3b", StringComparison.OrdinalIgnoreCase))
                           ?? models.FirstOrDefault(value => value.IndexOf("3b", StringComparison.OrdinalIgnoreCase) >= 0)
                           ?? models.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(fallback)) return false;
            var agent = services.Providers.CreateClient(runtime.Settings.AgentProviderId, fallback, false);
            if (agent == null) return false;
            var previousModel = runtime.Settings.AgentModel;
            runtime.Agent = agent;
            runtime.Settings.AgentModel = fallback;
            Enqueue(runtime.Session.Id, "agent_model_fallback", "system",
                "本地模型“" + previousModel + "”无法完成游戏决策，已自动切换为“" + fallback + "”继续控制玩家 2。",
                JsonSerializer.Serialize(new { previous_model = previousModel, fallback_model = fallback,
                    error = failure.Message }), runtime.LastStateJson);
            services.LogTiming(null, "本地游戏 Agent 已自动切换模型", detail:
                previousModel + " -> " + fallback + "｜" + failure.Message);
            return true;
        }

        private async Task RunDecisionAsync(StardewRuntime runtime, string stateJson, CancellationToken token)
        {
            var companionState = await runtime.Client.CallToolAsync("stardew_get_companion_state",
                new { companion = runtime.Settings.Companion }, token);
            if (string.IsNullOrWhiteSpace(companionState) || companionState.StartsWith("Companion \"")) return;
            var system = runtime.Session.IdentityBase + "\n\n" +
                         "你正在《星露谷物语》中直接控制显示名为“" + runtime.Settings.DisplayName +
                         "”的角色（内部工具 ID：" + runtime.Settings.Companion + "）。" +
                         "只根据下面的实际状态选择一个安全、短小、可验证的动作。不要替玩家花钱、送礼、睡觉或作重大选择。" +
                         "只输出 JSON：{\"action\":\"wait|move_to|face_direction|use_tool|interact|attack|cast_fishing_rod|set_auto_combat|eat_item|chat\",\"x\":整数?,\"y\":整数?,\"tool\":\"pickaxe|axe|hoe|watering_can|sword\"?,\"direction\":0到3?,\"enabled\":布尔?,\"slot\":整数?,\"message\":字符串?,\"reason\":简短理由}。";
            var response = await runtime.Agent.CompleteJsonAsync(new List<DeepSeekMessageData>
            {
                new DeepSeekMessageData("system", system),
                new DeepSeekMessageData("user", "当前确认状态：\n" + Limit(companionState, 18000))
            }, token);
            using var document = JsonDocument.Parse(StripFence(response));
            var root = document.RootElement;
            var action = ReadString(root, "action").ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(action) || action == "wait") return;
            var reason = ReadString(root, "reason");
            string tool;
            object arguments;
            switch (action)
            {
                case "move_to":
                    tool = "stardew_move_to";
                    arguments = new { companion = runtime.Settings.Companion, x = RequiredInt(root, "x"), y = RequiredInt(root, "y") };
                    break;
                case "face_direction":
                    tool = "stardew_face_direction";
                    arguments = new { companion = runtime.Settings.Companion, direction = RequiredInt(root, "direction") };
                    break;
                case "use_tool":
                    tool = "stardew_use_tool";
                    arguments = new { companion = runtime.Settings.Companion, tool = ReadString(root, "tool"), x = RequiredInt(root, "x"), y = RequiredInt(root, "y") };
                    break;
                case "interact":
                    tool = "stardew_interact";
                    arguments = new { companion = runtime.Settings.Companion, x = RequiredInt(root, "x"), y = RequiredInt(root, "y") };
                    break;
                case "attack":
                    tool = "stardew_attack";
                    arguments = new { companion = runtime.Settings.Companion };
                    break;
                case "cast_fishing_rod":
                    tool = "stardew_cast_fishing_rod";
                    arguments = new { companion = runtime.Settings.Companion };
                    break;
                case "set_auto_combat":
                    tool = "stardew_set_auto_combat";
                    arguments = new { companion = runtime.Settings.Companion, enabled = RequiredBool(root, "enabled") };
                    break;
                case "eat_item":
                    tool = "stardew_eat_item";
                    arguments = root.TryGetProperty("slot", out var slot) && slot.TryGetInt32(out var slotValue)
                        ? new { companion = runtime.Settings.Companion, slot = (int?)slotValue }
                        : new { companion = runtime.Settings.Companion, slot = (int?)null };
                    break;
                case "chat":
                    tool = "stardew_chat";
                    arguments = new { message = ReadString(root, "message") };
                    break;
                default:
                    throw new InvalidOperationException("游戏 Agent 返回了不支持的动作：" + action);
            }
            await runtime.Client.CallToolAsync(tool, arguments, token);
            Enqueue(runtime.Session.Id, "agent_command", "companion",
                runtime.Settings.DisplayName + " 已请求执行 " + action +
                (string.IsNullOrWhiteSpace(reason) ? "；等待游戏确认。" : "（" + reason + "）；等待游戏确认。"),
                JsonSerializer.Serialize(new { action, reason }), stateJson);
        }

        private ILlmClient ResolveAgent(StardewSettings settings)
        {
            if (services.Providers == null)
                throw new InvalidOperationException("宿主没有提供游戏 Agent 的模型目录。");
            if (string.IsNullOrWhiteSpace(settings.AgentProviderId))
                throw new InvalidOperationException("Player 模式需要选择本地 Ollama 供应商。");
            var client = services.Providers.CreateClient(settings.AgentProviderId,
                settings.AgentModel, false);
            return client ?? throw new InvalidOperationException(
                "无法创建游戏 Agent 模型：" + settings.AgentProviderId + " / " + settings.AgentModel);
        }

        private StardewSettings LoadSettings(GameSessionRecord session)
        {
            var connectionPath = Path.Combine(dataDirectory, "stardew", "mcp-connection.json");
            if (!File.Exists(connectionPath))
                throw new InvalidOperationException("还没有生成 Stardew MCP 连接配置，请先完成一键安装。");
            using var connectionDocument = JsonDocument.Parse(File.ReadAllText(connectionPath));
            using var environmentDocument = JsonDocument.Parse(string.IsNullOrWhiteSpace(session.EnvironmentJson)
                ? "{}" : session.EnvironmentJson);
            var connection = connectionDocument.RootElement;
            var environment = environmentDocument.RootElement;
            if (!string.Equals(ReadString(connection, "tracesoul_patch"), "single-companion-native-v2",
                    StringComparison.Ordinal))
                throw new InvalidOperationException("Stardew MCP 还没有单同伴补丁；请先完全退出游戏，再点一次“一键安装 / 检查并修复”。");
            var command = ReadString(connection, "command");
            var arguments = new List<string>();
            if (connection.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array)
                arguments.AddRange(args.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString()));
            var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (connection.TryGetProperty("env", out var env) && env.ValueKind == JsonValueKind.Object)
                foreach (var item in env.EnumerateObject()) variables[item.Name] = item.Value.GetString() ?? string.Empty;
            var bridgePath = variables.TryGetValue("STARDEW_BRIDGE_PATH", out var bridge) ? bridge : string.Empty;
            if (arguments.Count == 0 || !File.Exists(arguments[0]))
                throw new InvalidOperationException("Stardew MCP Server 尚未构建或入口文件不存在。");
            if (string.IsNullOrWhiteSpace(bridgePath))
                throw new InvalidOperationException("连接配置缺少 STARDEW_BRIDGE_PATH。");
            var mode = ReadString(environment, "control_mode").ToLowerInvariant();
            if (mode != "player") mode = "follow";
            var companion = ReadString(environment, "companion");
            if (companion != "Companion2") companion = "Companion1";
            var displayName = ResolveDisplayName(environment, companion);
            var appearanceMode = ReadString(environment, "appearance_mode").ToLowerInvariant();
            var nativeAppearance = appearanceMode != "sprite";
            var gender = ReadString(environment, "appearance_gender").ToLowerInvariant();
            if (gender != "male") gender = "female";
            return new StardewSettings
            {
                Command = string.IsNullOrWhiteSpace(command) ? "node" : command,
                Arguments = arguments,
                Environment = variables,
                BridgePath = bridgePath,
                Companion = companion,
                DisplayName = displayName,
                NativeAppearance = nativeAppearance,
                Gender = gender,
                Skin = Clamp(ReadInt(environment, "appearance_skin", 0), 0, 255),
                Hair = Clamp(ReadInt(environment, "appearance_hair", 0), 0, 999),
                Shirt = Clamp(ReadInt(environment, "appearance_shirt", 0), 0, 9999),
                Pants = Clamp(ReadInt(environment, "appearance_pants", 0), 0, 9999),
                Accessory = Clamp(ReadInt(environment, "appearance_accessory", -1), -1, 999),
                ControlMode = mode,
                AgentProviderId = ReadString(environment, "agent_provider_id"),
                AgentModel = ReadString(environment, "agent_model"),
                SensorPollMs = Clamp(ReadInt(environment, "sensor_poll_ms", 500), 500, 5000),
                DecisionIntervalMs = Clamp(ReadInt(environment, "decision_interval_ms", 4000), 2000, 60000),
                AgentDecisionTimeoutMs = Clamp(ReadInt(environment, "agent_decision_timeout_ms", 90000), 15000, 180000)
            };
        }

        private string ResolveDisplayName(JsonElement environment, string companion)
        {
            var name = ReadString(environment, "companion_display_name");
            if (string.IsNullOrWhiteSpace(name))
            {
                var pair = services.Storage?.LoadPairIdentity();
                if (pair != null && pair.IsComplete) name = pair.Assname;
            }
            name = new string((name ?? string.Empty).Trim()
                .Where(character => !char.IsControl(character)).Take(24).ToArray());
            return string.IsNullOrWhiteSpace(name) ? companion : name;
        }

        private static async Task<string> WaitForCompanionAsync(string bridgePath, string companion,
            string mode, TimeSpan timeout, CancellationToken token)
        {
            var deadline = DateTimeOffset.UtcNow + timeout;
            while (DateTimeOffset.UtcNow < deadline)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var json = ReadFreshBridge(bridgePath);
                    using var document = JsonDocument.Parse(json);
                    var found = FindCompanion(document.RootElement, companion);
                    if (found.HasValue && (string.IsNullOrWhiteSpace(mode) ||
                        string.Equals(ReadString(found.Value, "mode"), mode, StringComparison.OrdinalIgnoreCase)))
                        return json;
                }
                catch (IOException) { }
                await Task.Delay(250, token);
            }
            throw new TimeoutException(string.IsNullOrWhiteSpace(mode)
                ? "已发送生成命令，但游戏没有在 12 秒内确认同伴出现。"
                : "同伴已经出现，但游戏没有确认进入 " + mode + " 模式。");
        }

        private static string ReadFreshBridge(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new InvalidOperationException("没有检测到 bridge_data.json；请先用 SMAPI 启动游戏并加载存档。");
            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
            if (age > TimeSpan.FromSeconds(5))
                throw new InvalidOperationException("bridge_data.json 已停止刷新；请确认存档已经加载且游戏没有退出。");
            var json = File.ReadAllText(path);
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("player", out _))
                throw new InvalidOperationException("桥接文件还没有玩家状态；请先加载存档。");
            return json;
        }

        private static string DescribeChange(string previousJson, string currentJson, string companion,
            string displayName)
        {
            if (string.IsNullOrWhiteSpace(previousJson)) return string.Empty;
            try
            {
                using var beforeDocument = JsonDocument.Parse(previousJson);
                using var afterDocument = JsonDocument.Parse(currentJson);
                var before = beforeDocument.RootElement;
                var after = afterDocument.RootElement;
                var changes = new List<string>();
                var oldDay = ReadInt(before, "day", 0);
                var newDay = ReadInt(after, "day", 0);
                var oldSeason = ReadString(before, "season");
                var newSeason = ReadString(after, "season");
                if (oldDay != newDay || oldSeason != newSeason)
                    changes.Add("游戏日期从 " + oldSeason + oldDay + " 变为 " + newSeason + newDay);
                var oldLocation = ReadString(before, "location");
                var newLocation = ReadString(after, "location");
                if (!string.Equals(oldLocation, newLocation, StringComparison.Ordinal))
                    changes.Add("玩家从 " + oldLocation + " 来到 " + newLocation);
                var oldHour = ReadInt(before, "time", 0) / 100;
                var newHour = ReadInt(after, "time", 0) / 100;
                if (oldHour != newHour) changes.Add("游戏时间进入 " + newHour + " 点");
                var oldCompanion = FindCompanion(before, companion);
                var newCompanion = FindCompanion(after, companion);
                if (!oldCompanion.HasValue && newCompanion.HasValue)
                    changes.Add(displayName + " 已进入游戏");
                if (oldCompanion.HasValue && newCompanion.HasValue)
                {
                    var oldMode = ReadString(oldCompanion.Value, "mode");
                    var newMode = ReadString(newCompanion.Value, "mode");
                    if (!string.Equals(oldMode, newMode, StringComparison.Ordinal))
                        changes.Add(displayName + " 切换为 " + newMode + " 模式");
                    var oldResult = RawProperty(oldCompanion.Value, "lastCommandResult");
                    var newResult = RawProperty(newCompanion.Value, "lastCommandResult");
                    if (!string.IsNullOrWhiteSpace(newResult) && !string.Equals(oldResult, newResult, StringComparison.Ordinal))
                        changes.Add(displayName + " 的动作由游戏确认：" + newResult);
                }
                return string.Join("；", changes);
            }
            catch { return string.Empty; }
        }

        private void Enqueue(string sessionId, string kind, string actor, string content,
            string payloadJson, string stateJson)
        {
            var queue = events.GetOrAdd(sessionId, _ => new ConcurrentQueue<StardewAdapterEvent>());
            queue.Enqueue(new StardewAdapterEvent
            {
                SessionId = sessionId,
                Kind = kind,
                Actor = actor,
                Content = content,
                PayloadJson = payloadJson ?? "{}",
                StateJson = stateJson ?? "{}",
                OccurredUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
        }

        private static JsonElement? FindCompanion(JsonElement root, string name)
        {
            if (!root.TryGetProperty("companions", out var companions) || companions.ValueKind != JsonValueKind.Array)
                return null;
            foreach (var item in companions.EnumerateArray())
                if (string.Equals(ReadString(item, "name"), name, StringComparison.Ordinal)) return item.Clone();
            return null;
        }

        private static string ReadString(JsonElement root, params string[] path)
        {
            var value = root;
            foreach (var item in path)
                if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(item, out value)) return string.Empty;
            return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ToString();
        }

        private static int ReadInt(JsonElement root, string name, int fallback)
        {
            return root.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : fallback;
        }

        private static int RequiredInt(JsonElement root, string name)
        {
            if (root.TryGetProperty(name, out var value) && value.TryGetInt32(out var result)) return result;
            throw new InvalidOperationException("游戏 Agent 动作缺少整数参数：" + name);
        }

        private static bool RequiredBool(JsonElement root, string name)
        {
            if (root.TryGetProperty(name, out var value) &&
                (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)) return value.GetBoolean();
            throw new InvalidOperationException("游戏 Agent 动作缺少布尔参数：" + name);
        }

        private static string RawProperty(JsonElement root, string name)
        {
            return root.TryGetProperty(name, out var value) ? value.GetRawText() : string.Empty;
        }

        private static string StripFence(string value)
        {
            value = (value ?? string.Empty).Trim();
            if (!value.StartsWith("```", StringComparison.Ordinal)) return value;
            var firstLine = value.IndexOf('\n');
            if (firstLine >= 0) value = value.Substring(firstLine + 1);
            if (value.EndsWith("```", StringComparison.Ordinal)) value = value.Substring(0, value.Length - 3);
            return value.Trim();
        }

        private static string Limit(string value, int max)
        {
            value ??= string.Empty;
            return value.Length <= max ? value : value.Substring(0, max);
        }

        private static int Clamp(int value, int min, int max) { return Math.Max(min, Math.Min(max, value)); }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            shutdown.Cancel();
            foreach (var runtime in runtimes.Values) runtime.Dispose();
            runtimes.Clear();
            shutdown.Dispose();
        }

        private sealed class StardewSettings
        {
            public string Command { get; set; }
            public List<string> Arguments { get; set; }
            public Dictionary<string, string> Environment { get; set; }
            public string BridgePath { get; set; }
            public string Companion { get; set; }
            public string DisplayName { get; set; }
            public bool NativeAppearance { get; set; }
            public string Gender { get; set; }
            public int Skin { get; set; }
            public int Hair { get; set; }
            public int Shirt { get; set; }
            public int Pants { get; set; }
            public int Accessory { get; set; }
            public string ControlMode { get; set; }
            public string AgentProviderId { get; set; }
            public string AgentModel { get; set; }
            public int SensorPollMs { get; set; }
            public int DecisionIntervalMs { get; set; }
            public int AgentDecisionTimeoutMs { get; set; }
        }

        private sealed class StardewRuntime : IDisposable
        {
            public GameSessionRecord Session { get; }
            public StardewSettings Settings { get; }
            public StardewMcpClient Client { get; }
            public ILlmClient Agent { get; set; }
            public CancellationTokenSource Cancellation { get; }
            public Task Loop { get; set; }
            public volatile bool BridgeOnline = true;
            public long LastBridgeUnixMs;
            public long LastDecisionUnixMs;
            public string LastError = string.Empty;
            public string LastStateJson;
            public bool FallbackAttempted;
            private int disposeState;

            public StardewRuntime(GameSessionRecord session, StardewSettings settings,
                StardewMcpClient client, ILlmClient agent, string stateJson,
                CancellationTokenSource cancellation)
            {
                Session = session;
                Settings = settings;
                Client = client;
                Agent = agent;
                LastStateJson = stateJson;
                Cancellation = cancellation;
                LastBridgeUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposeState, 1) != 0) return;
                Cancellation.Cancel();
                Client.Dispose();
                Cancellation.Dispose();
            }
        }
    }
}
