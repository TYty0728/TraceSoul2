using System.Text.Json;
using TraceSoul2.Data;
using TraceSoul2.Plugins;

namespace TraceSoul2.ExternalPlugins.RealtimeCall;

public sealed class RealtimeCallPlugin : ITracePlugin
{
    private CallControlEndpoint? endpoint;
    private CallDialogueHub? dialogue;
    private TracePluginServices? services;
    public TracePluginMetadataData Metadata { get; } = new()
    {
        Id = "realtime.call", DisplayName = "实时通话", Version = "0.2.0", Author = "TraceSoul2",
        Role = PluginRoleValues.Platform, PlatformId = "realtime.call",
        Description = "QQ/Unity 共用实时对话桥：外部 ASR 最终转写进入现有心智，回复推送客户端 TTS，并按播放回执入记忆。"
    };

    public void Register(TracePluginContext context)
    {
        services = context.Services;
        var accessToken = "";
        var conversationId = "tracesoul2";
        var idleSeconds = 120;
        foreach (var path in new[]
        {
            context.PackageDirectory == null ? null : Path.Combine(context.PackageDirectory, "plugin.json"),
            context.PluginDataDirectory == null ? null : Path.Combine(context.PluginDataDirectory, "config.json")
        })
        {
            if (path == null || !File.Exists(path)) continue;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.TryGetProperty("access_token", out var t)) accessToken = t.GetString() ?? "";
            if (root.TryGetProperty("conversation_id", out var c)) conversationId = c.GetString() ?? "";
            if (root.TryGetProperty("idle_timeout_seconds", out var i)) idleSeconds = i.GetInt32();
        }
        if (accessToken.Length != 0 && (accessToken.Length < 32 || accessToken.Length > 256 || accessToken.Any(char.IsWhiteSpace)))
            throw new InvalidOperationException("通话令牌必须为 32–256 个非空白字符。");
        if (string.IsNullOrWhiteSpace(conversationId) || conversationId.Length > 128)
            throw new InvalidOperationException("通话所属对话不能为空或超过 128 字符。");
        if (idleSeconds is < 15 or > 3600) throw new InvalidOperationException("通话空闲超时必须为 15–3600 秒。");
        var registry = new RealtimeSessionRegistry();
        dialogue = new CallDialogueHub(registry);
        endpoint = new CallControlEndpoint(registry, conversationId, accessToken, TimeSpan.FromSeconds(idleSeconds), dialogue);
        context.AddWebSocketEndpoint(endpoint);
        context.AddBackgroundService(new CallInboxService(dialogue));
        context.AddCallable(new CallTextEffector(dialogue));
        services.Platforms.Register(new PlatformHandle
        {
            Id = "realtime.call", DisplayName = "实时通话（转写/TTS 桥）",
            IsConnected = () => dialogue?.ActiveSessions > 0,
            Details = () => new
            {
                controlConfigured = endpoint?.Configured ?? false, controlConnections = endpoint?.ActiveConnections ?? 0,
                activeSessions = registry.ActiveCount, audioReady = false, videoReady = false, dialogueReady = true,
                audioMode = "external_asr_tts"
            }
        });
        Metadata.Note = endpoint.Configured
            ? "转写对话桥已就绪；Unity/QQ 网关负责 ASR、TTS 与音频设备。"
            : "未配置通话令牌，拒绝所有连接。";
    }

    public void Shutdown()
    {
        endpoint?.Dispose();
        services?.Platforms.Unregister("realtime.call");
        dialogue = null;
    }

    private sealed class CallInboxService : ITraceBackgroundService
    {
        private readonly CallDialogueHub dialogue;
        public CallInboxService(CallDialogueHub dialogue) => this.dialogue = dialogue;
        public TraceContributionDescriptorData Descriptor { get; } = new()
        {
            Id = "realtime.call.inbox", Kind = TraceContributionKindValues.BackgroundService,
            DisplayName = "实时通话收件箱", Provides = "platform.realtime_call.inbox",
            Description = "把外部 ASR 的最终转写与已播放回复交给现有心智和记忆链路。"
        };
        public bool IsAvailable => dialogue.ActiveSessions > 0 || dialogue.HasPendingInbound;
        public IEnumerable<PluginEventData> Poll(long nowUnixMs) => dialogue.TakeInbound();
        public void Shutdown() => dialogue.Stop();
    }

    private sealed class CallTextEffector : ITraceCallableContribution
    {
        private readonly CallDialogueHub dialogue;
        public CallTextEffector(CallDialogueHub dialogue) => this.dialogue = dialogue;
        public TraceContributionDescriptorData Descriptor { get; } = new()
        {
            Id = "realtime.call.text.send", Kind = TraceContributionKindValues.Effector,
            DisplayName = "实时通话发言", Provides = "expression.realtime_call.text",
            Description = "把当前回复推送给通话客户端合成并播放；只有客户端确认播放的部分才进入语义记忆。",
            Boundary = "实时通话｜自由文本（客户端负责 TTS）",
            BodyId = "realtime.call", BodyTier = BodyTierValues.App, Organ = BodyOrganValues.Text,
            ParametersJsonSchema = "{text:string}", HasExternalSideEffect = true
        };

        public bool IsAvailable(TraceTurnContext context) =>
            context != null && dialogue.HasActive(context.ConversationId);

        public async Task<TraceCapabilityResultData> ExecuteAsync(BrainCapabilityCallData call,
            TraceTurnContext context, CancellationToken cancellationToken)
        {
            var text = call.GetArgument("text").Trim();
            if (text.Length == 0) return new TraceCapabilityResultData { Status = "failed", Summary = "通话发言为空。" };
            try
            {
                var sent = await dialogue.SendAssistantAsync(context.ConversationId, text, cancellationToken);
                return new TraceCapabilityResultData
                {
                    Status = "success", Summary = "回复已送到通话客户端，等待实际播放回执。",
                    Payload = JsonSerializer.Serialize(new
                    {
                        session_id = sent.Session.SessionId, generation = sent.Session.Generation,
                        output_id = sent.OutputId
                    }),
                    // Kernel 要求表达器返回事件；这里只记运行回执，语义文本由 playback 完成/打断时入队。
                    ProducedEvent = new PluginEventData
                    {
                        TraceId = context.TraceId, PluginId = "realtime.call",
                        ConversationId = context.ConversationId, ExternalEventId = sent.OutputId + ":queued",
                        Role = "system_event", Content = "[实时通话回复已送客户端，等待播放]",
                        IsOperational = true, Organ = BodyOrganValues.Voice,
                        OccurredUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    }
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (CallProtocolException error)
            {
                return new TraceCapabilityResultData { Status = "failed", Summary = "实时通话发送失败：" + error.Code };
            }
        }
    }
}
