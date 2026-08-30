using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Plugins;

namespace TraceSoul2.ExternalPlugins.GameSession
{
    /// <summary>
    /// 游戏会话平台：自研 WS 桥身体。连接桥 + 翻译，不做决策。
    /// 星露谷 / 通用游戏等 profile 概念上是其下器官（当前随包携带，物理拆包渐进）。
    /// </summary>
    public sealed class GameSessionPlugin : ITracePlugin
    {
        private const string PluginId = "game.session";
        private GameSessionStore store;
        private GameSessionController controller;
        private StardewInstaller stardewInstaller;
        private StardewGameAdapter stardewAdapter;
        private GameSessionWebSocketEndpoint endpoint;

        public TracePluginMetadataData Metadata { get; } = new TracePluginMetadataData
        {
            Id = PluginId,
            DisplayName = "游戏会话",
            Version = "0.4.3",
            Author = "TraceSoul2",
            Role = PluginRoleValues.Platform,
            PlatformId = BodyIds.Game,
            Description = "游戏身体：平台无关的游戏临时会话，等游戏 mod 经 WS 桥连进来。私有事件流、阶段摘要、当前游戏上下文、同步与结束记忆。"
        };

        public void Register(TracePluginContext context)
        {
            var dataDirectory = context.PluginDataDirectory ??
                                Path.Combine(context.Services.DataDirectory ?? string.Empty, "game-session");
            Directory.CreateDirectory(dataDirectory);
            var config = GameSessionConfigLoader.Load(context.PackageDirectory, dataDirectory);
            store = new GameSessionStore(Path.Combine(dataDirectory, "game-session.sqlite3"));
            stardewInstaller = new StardewInstaller(dataDirectory);
            stardewAdapter = new StardewGameAdapter(dataDirectory, context.Services);
            controller = new GameSessionController(config, store, context.Services, stardewAdapter);
            endpoint = new GameSessionWebSocketEndpoint(config, controller, stardewInstaller);
            context.Services.Platforms.Register(new PlatformHandle
            {
                Id = BodyIds.Game,
                DisplayName = "游戏会话（自研 WS 桥）",
                IsConnected = () => (endpoint != null && endpoint.ActiveConnections > 0) ||
                                    (stardewAdapter != null && stardewAdapter.ConnectedRuntimeCount > 0),
                Details = () => new
                {
                    connections = endpoint == null ? 0 : endpoint.ActiveConnections,
                    activeSessions = store == null ? 0 : store.GetActiveSessions().Count,
                    connectedGameRuntimes = stardewAdapter == null ? 0 : stardewAdapter.ConnectedRuntimeCount
                }
            });
            context.AddMountedFacet(new CurrentGameFacet(controller, config.facet_max_chars));
            context.AddCallable(new StartCallable(controller));
            context.AddCallable(new StatusCallable(controller));
            context.AddCallable(new EventCallable(controller));
            context.AddCallable(new EndCallable(controller));
            context.AddBackgroundService(new GameSessionBackgroundService(controller));
            context.AddWebSocketEndpoint(endpoint);
            Metadata.Note = string.IsNullOrWhiteSpace(config.access_token)
                ? "桥接 Token 为空；仅建议在受信任的本机环境使用。" : string.Empty;
            context.Services.LogTiming(null, "游戏会话插件已加载", detail:
                "version=" + Metadata.Version + "｜ws=" + config.websocket_path +
                "｜profiles=" + config.profiles.Count + "｜db=" + store.DatabasePath);
        }

        public void Shutdown()
        {
            try { controller?.Dispose(); } catch { }
            try { stardewInstaller?.Dispose(); } catch { }
            controller = null;
            stardewInstaller = null;
            stardewAdapter = null;
            endpoint = null;
            store = null;
        }

        private sealed class CurrentGameFacet : ITraceMountedFacet
        {
            private readonly GameSessionController controller;
            private readonly int maxChars;
            public CurrentGameFacet(GameSessionController controller, int maxChars)
            {
                this.controller = controller;
                this.maxChars = maxChars;
                Descriptor.MaxContextChars = maxChars;
            }
            public TraceContributionDescriptorData Descriptor { get; } = new TraceContributionDescriptorData
            {
                Id = "game.session.current",
                Kind = TraceContributionKindValues.MountedFacet,
                DisplayName = "当前游戏",
                Description = "有进行中的游戏时，提供有上限的阶段摘要与当前目标。",
                RefreshMode = TraceFacetRefreshValues.OncePerTurn,
                Priority = 72,
                MaxContextChars = 1200
            };
            public bool IsAvailable(TraceTurnContext context)
            {
                return context != null && controller.Status(context.ConversationId) != null;
            }
            public Task<TraceContextBlockData> BuildContextAsync(TraceTurnContext context,
                CancellationToken cancellationToken)
            {
                var content = controller.BuildFacet(context.ConversationId, maxChars);
                return Task.FromResult(string.IsNullOrWhiteSpace(content) ? null : new TraceContextBlockData
                {
                    Title = "当前游戏工作台",
                    Content = content
                });
            }
            public Task<TraceCapabilityResultData> ApplyOutputAsync(BrainFacetOutputData output,
                TraceTurnContext context, CancellationToken cancellationToken)
            {
                return Task.FromResult<TraceCapabilityResultData>(null);
            }
        }

        private sealed class StartCallable : ITraceCallableContribution
        {
            private readonly GameSessionController controller;
            public StartCallable(GameSessionController controller) { this.controller = controller; }
            public TraceContributionDescriptorData Descriptor { get; } = DescriptorFor(
                "game.session.start", "开始游戏会话", "game.session.start",
                "建立一场平台无关的游戏临时会话并返回一次性身份基底。",
                "{profile_id?:string,game_id?:string,title:string,adapter_id?:string,role_instruction?:string,environment_json?:string}");
            public bool IsAvailable(TraceTurnContext context)
            {
                return context != null && controller.Status(context.ConversationId) == null;
            }
            public async Task<TraceCapabilityResultData> ExecuteAsync(BrainCapabilityCallData call,
                TraceTurnContext context, CancellationToken cancellationToken)
            {
                var started = await controller.StartAsync(context.ConversationId,
                    call.GetArgument("profile_id", "generic"), call.GetArgument("game_id"),
                    call.GetArgument("title"), call.GetArgument("adapter_id"),
                    call.GetArgument("role_instruction"), call.GetArgument("environment_json", "{}"),
                    false, cancellationToken);
                return Success("已开始《" + started.Item1.Title + "》游戏会话。",
                    JsonSerializer.Serialize(controller.PublicSession(started.Item1, true)), started.Item2);
            }
        }

        private sealed class StatusCallable : ITraceCallableContribution
        {
            private readonly GameSessionController controller;
            public StatusCallable(GameSessionController controller) { this.controller = controller; }
            public TraceContributionDescriptorData Descriptor { get; } = DescriptorFor(
                "game.session.status", "查看游戏进度", "game.session.status",
                "读取当前游戏会话的阶段摘要与目标。", "{}");
            public bool IsAvailable(TraceTurnContext context)
            {
                return context != null && controller.Status(context.ConversationId) != null;
            }
            public Task<TraceCapabilityResultData> ExecuteAsync(BrainCapabilityCallData call,
                TraceTurnContext context, CancellationToken cancellationToken)
            {
                var session = controller.Status(context.ConversationId);
                return Task.FromResult(Success("当前正在一起玩《" + session.Title + "》。",
                    JsonSerializer.Serialize(controller.PublicSession(session, false)), null));
            }
        }

        private sealed class EventCallable : ITraceCallableContribution
        {
            private readonly GameSessionController controller;
            public EventCallable(GameSessionController controller) { this.controller = controller; }
            public TraceContributionDescriptorData Descriptor { get; } = DescriptorFor(
                "game.session.event", "记录游戏事件", "game.session.event",
                "v0 手动桥接入口：把一条已收敛的结构化游戏事件写入插件私库。",
                "{session_id?:string,kind:string,actor:user|companion|world,content:string,payload_json?:string,state_json?:string}");
            public bool IsAvailable(TraceTurnContext context)
            {
                return context != null && controller.Status(context.ConversationId) != null;
            }
            public async Task<TraceCapabilityResultData> ExecuteAsync(BrainCapabilityCallData call,
                TraceTurnContext context, CancellationToken cancellationToken)
            {
                var active = controller.Status(context.ConversationId);
                var record = await controller.AppendEventAsync(new GameEventInput
                {
                    session_id = call.GetArgument("session_id", active.Id),
                    kind = call.GetArgument("kind", "system"),
                    actor = call.GetArgument("actor", "world"),
                    content = call.GetArgument("content"),
                    payload = call.GetArgument("payload_json", "{}"),
                    state = call.GetArgument("state_json", "{}")
                }, cancellationToken);
                return Success("已记录第 " + record.Seq + " 条游戏事件。",
                    JsonSerializer.Serialize(new { session_id = record.SessionId, seq = record.Seq }), null);
            }
        }

        private sealed class EndCallable : ITraceCallableContribution
        {
            private readonly GameSessionController controller;
            public EndCallable(GameSessionController controller) { this.controller = controller; }
            public TraceContributionDescriptorData Descriptor { get; } = DescriptorFor(
                "game.session.end", "结束游戏会话", "game.session.end",
                "收束剩余事件；正常结束产生一条共同经历，abort 只保留插件工作台记录。",
                "{session_id?:string,mode?:finish|abort}");
            public bool IsAvailable(TraceTurnContext context)
            {
                return context != null && controller.Status(context.ConversationId) != null;
            }
            public async Task<TraceCapabilityResultData> ExecuteAsync(BrainCapabilityCallData call,
                TraceTurnContext context, CancellationToken cancellationToken)
            {
                var ended = await controller.EndAsync(call.GetArgument("session_id"), context.ConversationId,
                    string.Equals(call.GetArgument("mode"), "abort", StringComparison.OrdinalIgnoreCase),
                    false, cancellationToken);
                return Success(ended.Session.Status == GameSessionStatusValues.Aborted
                        ? "这次游戏会话已中止。" : "《" + ended.Session.Title + "》游戏会话已结束并收束。",
                    JsonSerializer.Serialize(controller.PublicSession(ended.Session, false)), ended.Event);
            }
        }

        private sealed class GameSessionBackgroundService : ITraceBackgroundService
        {
            private readonly GameSessionController controller;
            private bool stopped;
            public GameSessionBackgroundService(GameSessionController controller) { this.controller = controller; }
            public TraceContributionDescriptorData Descriptor { get; } = new TraceContributionDescriptorData
            {
                Id = "game.session.service",
                Kind = TraceContributionKindValues.BackgroundService,
                DisplayName = "游戏会话后台",
                Description = "检查尾段摘要、定时同步和无事件超时收尾。",
                Provides = "game.session.poll"
            };
            public bool IsAvailable { get { return !stopped; } }
            public IEnumerable<PluginEventData> Poll(long nowUnixMs)
            {
                controller.Tick(nowUnixMs);
                return controller.DrainOutgoing();
            }
            public void Shutdown() { stopped = true; }
        }

        private static TraceContributionDescriptorData DescriptorFor(string id, string name,
            string provides, string description, string schema)
        {
            return new TraceContributionDescriptorData
            {
                Id = id,
                Kind = TraceContributionKindValues.CallableNerve,
                DisplayName = name,
                Description = description,
                Provides = provides,
                ParametersJsonSchema = schema,
                HasInternalMutation = id != "game.session.status"
            };
        }

        private static TraceCapabilityResultData Success(string summary, string payload,
            PluginEventData produced)
        {
            return new TraceCapabilityResultData
            {
                Status = "success",
                Summary = summary,
                Payload = payload ?? string.Empty,
                ProducedEvent = produced
            };
        }
    }
}
