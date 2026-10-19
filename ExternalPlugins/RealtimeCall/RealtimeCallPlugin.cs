using System.Text.Json;
using TraceSoul2.Data;
using TraceSoul2.Plugins;

namespace TraceSoul2.ExternalPlugins.RealtimeCall;

public sealed class RealtimeCallPlugin : ITracePlugin
{
    private CallControlEndpoint? endpoint;
    private CallDialogueHub? dialogue;
    private TracePluginServices? services;
    private Func<TraceTurnContext, string>? callContext;
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
        var requireVoiceprint = true;
        var voiceprintThreshold = 0.65;
        var speakerId = "voice-owner";
        var privateCall = false;
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
            if (root.TryGetProperty("require_voiceprint", out var v)) requireVoiceprint = v.GetBoolean();
            if (root.TryGetProperty("voiceprint_threshold", out var s)) voiceprintThreshold = s.GetDouble();
            if (root.TryGetProperty("speaker_id", out var p)) speakerId = p.GetString() ?? "";
            if (root.TryGetProperty("private_call", out var a)) privateCall = a.GetBoolean();
        }
        if (accessToken.Length != 0 && (accessToken.Length < 32 || accessToken.Length > 256 || accessToken.Any(char.IsWhiteSpace)))
            throw new InvalidOperationException("通话令牌必须为 32–256 个非空白字符。");
        if (string.IsNullOrWhiteSpace(conversationId) || conversationId.Length > 128)
            throw new InvalidOperationException("通话所属对话不能为空或超过 128 字符。");
        if (idleSeconds is < 15 or > 3600) throw new InvalidOperationException("通话空闲超时必须为 15–3600 秒。");
        if (string.IsNullOrWhiteSpace(speakerId) || speakerId.Length > 128)
            throw new InvalidOperationException("通话发言者标识须为 1–128 字符。");
        var registry = new RealtimeSessionRegistry();
        dialogue = new CallDialogueHub(registry, speakerId, privateCall);
        var voiceprint = new VoiceprintGate(context.PluginDataDirectory == null ? null :
            Path.Combine(context.PluginDataDirectory, "voiceprint.json"), requireVoiceprint, voiceprintThreshold);
        endpoint = new CallControlEndpoint(registry, conversationId, accessToken, TimeSpan.FromSeconds(idleSeconds), dialogue, voiceprint);
        callContext = turn => dialogue?.CanSpeak(turn) == true
            ? "【持续通话】电话仍接通。安静不代表对方说了话，无需填补沉默。沿用已有主动联系安排，想开口时可通过通话自然问候或问对方在做什么；没有回应时不要连续追问。不能把背景声音推测成对方的活动。"
            : "";
        services.MindTurnPromptAppends.Add(callContext);
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
                audioMode = "external_asr_tts", voiceprintRequired = voiceprint.Required,
                voiceprintStatus = voiceprint.Status
            }
        });
        Metadata.Note = endpoint.Configured
            ? "转写对话桥已就绪；Unity/QQ 网关负责 ASR、TTS 与音频设备。"
            : "未配置通话令牌，拒绝所有连接。";
    }

    public void Shutdown()
    {
        endpoint?.Dispose();
        if (callContext != null) services?.MindTurnPromptAppends.Remove(callContext);
        callContext = null;
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
            WhenToUse = "当前电话接通时，可回应对方，也可在已有主动联系唤醒时自然开口。",
            WhenNotToUse = "安静不需要填补，不从电视、杂音或被拒绝的语音推测对方说了什么。",
            Boundary = "实时通话｜自由文本（客户端负责 TTS）",
            BodyId = "realtime.call", BodyTier = BodyTierValues.App, Organ = BodyOrganValues.Text,
            ParametersJsonSchema = "{text:string}", HasExternalSideEffect = true
        };

        public bool IsAvailable(TraceTurnContext context) =>
            context != null && dialogue.CanSpeak(context);

        public async Task<TraceCapabilityResultData> ExecuteAsync(BrainCapabilityCallData call,
            TraceTurnContext context, CancellationToken cancellationToken)
        {
            if (!dialogue.CanSpeak(context)) return new TraceCapabilityResultData
                { Status = "failed", Summary = "当前通话目标或在场范围不匹配。" };
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
