using System.Text.Json;

namespace TraceSoul2.ExternalPlugins.RealtimeCall;

/// <summary>每个 WebSocket 独占一个实例；重复请求返回原结果，不重复打断/创建会话。</summary>
public sealed class CallControlConnection : IDisposable
{
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private readonly RealtimeSessionRegistry registry;
    private readonly string owner = Guid.NewGuid().ToString("N");
    private readonly string conversationId;
    private readonly CallDialogueHub? dialogue;
    private readonly Func<string, CancellationToken, Task>? serverPush;
    private readonly Dictionary<long, (string Request, string Response)> replay = new();
    private long lastSequence;
    private bool disposed;

    public CallControlConnection(RealtimeSessionRegistry registry, string conversationId,
        CallDialogueHub? dialogue = null, Func<string, CancellationToken, Task>? serverPush = null)
    {
        this.registry = registry;
        this.conversationId = conversationId;
        this.dialogue = dialogue;
        this.serverPush = serverPush;
    }

    public string Handle(string text)
    {
        if (disposed) throw new ObjectDisposedException(nameof(CallControlConnection));
        long sequence = 0;
        string op = "";
        try
        {
            using var json = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 });
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("seq", out var seq) ||
                seq.ValueKind != JsonValueKind.Number || !seq.TryGetInt64(out sequence) || sequence <= 0)
                throw new CallProtocolException("invalid_sequence");
            if (replay.TryGetValue(sequence, out var cached))
            {
                if (cached.Request != text) throw new CallProtocolException("sequence_conflict");
                return cached.Response;
            }
            if (sequence <= lastSequence) throw new CallProtocolException("stale_sequence");
            lastSequence = sequence;
            string response;
            try
            {
                op = Required(root, "op");
                object data = op switch
                {
                    "hello" => new
                    {
                        protocol = "realtime.call.v1", audio_ready = false, video_ready = false,
                        dialogue_ready = dialogue != null, stage = dialogue == null ? "control_only" : "transcript_bridge",
                        input = dialogue == null ? "none" : "final_transcript",
                        output = dialogue == null ? "none" : "assistant_text_for_client_tts",
                        operations = dialogue == null
                            ? new[] { "hello", "ping", "start", "status", "interrupt", "playback", "end" }
                            : new[] { "hello", "ping", "start", "status", "transcript", "interrupt", "output_ready", "playback", "end" }
                    },
                    "ping" => Ping(),
                    "start" => Start(root),
                    "status" => registry.Get(owner, Required(root, "session_id")),
                    "transcript" => Transcript(root, sequence),
                    "interrupt" => Interrupt(root),
                    "output_ready" => OutputReady(root),
                    "playback" => Playback(root),
                    "end" => End(root),
                    _ => throw new CallProtocolException("unsupported_operation")
                };
                response = JsonSerializer.Serialize(new { ok = true, seq = sequence, op, data }, JsonOptions);
            }
            catch (CallProtocolException e) { response = Error(sequence, op, e.Code); }
            replay.Add(sequence, (text, response));
            if (replay.Count > 128) replay.Remove(replay.Keys.Min());
            return response;
        }
        catch (JsonException) { return Error(sequence, op, "invalid_json"); }
        catch (CallProtocolException e) { return Error(sequence, op, e.Code); }
    }

    private object Start(JsonElement root)
    {
        if (root.TryGetProperty("conversation_id", out _)) throw new CallProtocolException("conversation_bound_by_server");
        var session = registry.Start(owner, conversationId, Required(root, "transport"));
        if (dialogue != null && serverPush != null) dialogue.Bind(owner, session, serverPush);
        return session;
    }

    private object Transcript(JsonElement root, long sequence)
    {
        if (dialogue == null) throw new CallProtocolException("unsupported_operation");
        var item = dialogue.EnqueueTranscript(owner, Required(root, "session_id"), Number(root, "generation"),
            RequiredText(root, "text", 4000), sequence);
        return new { accepted = true, event_id = item.ExternalEventId };
    }

    private object Interrupt(JsonElement root)
    {
        var sessionId = Required(root, "session_id");
        var generation = Number(root, "generation");
        return dialogue == null ? registry.Interrupt(owner, sessionId, generation) : dialogue.Interrupt(owner, sessionId, generation);
    }

    private object OutputReady(JsonElement root)
    {
        if (dialogue == null) throw new CallProtocolException("unsupported_operation");
        return dialogue.ConfirmOutputDuration(owner, Required(root, "session_id"), Number(root, "generation"),
            Required(root, "output_id"), Number(root, "duration_ms"));
    }

    private object Playback(JsonElement root)
    {
        var sessionId = Required(root, "session_id");
        var generation = Number(root, "generation");
        var outputId = Required(root, "output_id");
        var playedMs = Number(root, "played_ms");
        var completed = Boolean(root, "completed");
        return dialogue == null
            ? registry.ReportPlayback(owner, sessionId, generation, outputId, playedMs, completed)
            : dialogue.ReportPlayback(owner, sessionId, generation, outputId, playedMs, completed);
    }

    private object End(JsonElement root)
    {
        var sessionId = Required(root, "session_id");
        return dialogue == null ? registry.End(owner, sessionId) : dialogue.End(owner, sessionId);
    }

    private object Ping() { registry.Heartbeat(owner); return new { alive = true }; }
    private static string Error(long seq, string op, string code) => JsonSerializer.Serialize(new { ok = false, seq, op, error = code });
    private static string Required(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()) || value.GetString()!.Length > 128)
            throw new CallProtocolException("invalid_" + key);
        return value.GetString()!;
    }
    private static string RequiredText(JsonElement root, string key, int maxLength)
    {
        if (!root.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String)
            throw new CallProtocolException("invalid_" + key);
        var text = (value.GetString() ?? string.Empty).Trim();
        if (text.Length == 0 || text.Length > maxLength) throw new CallProtocolException("invalid_" + key);
        return text;
    }
    private static long Number(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt64(out var number) || number < 0)
            throw new CallProtocolException("invalid_" + key);
        return number;
    }
    private static bool Boolean(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new CallProtocolException("invalid_" + key);
        return value.GetBoolean();
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (dialogue == null) registry.Disconnect(owner);
        else dialogue.Disconnect(owner);
        replay.Clear();
    }
}
