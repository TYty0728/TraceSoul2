using System.Collections.Concurrent;
using System.Text.Json;
using TraceSoul2.Data;

namespace TraceSoul2.ExternalPlugins.RealtimeCall;

/// <summary>
/// 把已完成的外部 ASR 文本送入中枢，并把中枢回复推给客户端 TTS。
/// 音频始终留在 Unity/QQ 网关；服务端只接受明确的最终转写和播放进度。
/// </summary>
public sealed class CallDialogueHub
{
    private sealed class Binding
    {
        public required string Owner;
        public required string SessionId;
        public required string ConversationId;
        public required Func<string, CancellationToken, Task> Send;
        public long Generation;
    }

    private sealed class Output
    {
        public required string SessionId;
        public required string ConversationId;
        public required string OutputId;
        public required string Text;
        public long Generation;
        public bool Archived;
    }

    private readonly object gate = new();
    private readonly RealtimeSessionRegistry registry;
    private readonly ConcurrentQueue<PluginEventData> inbound = new();
    private readonly Dictionary<string, Binding> bindings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Output> outputs = new(StringComparer.Ordinal);
    private bool stopped;

    public CallDialogueHub(RealtimeSessionRegistry registry) => this.registry = registry;
    public int ActiveSessions { get { lock (gate) return stopped ? 0 : bindings.Count; } }
    public bool HasPendingInbound => !inbound.IsEmpty;
    public bool HasActive(string conversationId)
    {
        lock (gate) return !stopped && bindings.Values.Any(x =>
            string.Equals(x.ConversationId, conversationId, StringComparison.Ordinal));
    }

    public void Bind(string owner, RealtimeSessionData session, Func<string, CancellationToken, Task> send)
    {
        lock (gate)
        {
            if (stopped) throw new CallProtocolException("service_stopped");
            bindings[session.SessionId] = new Binding
            {
                Owner = owner, SessionId = session.SessionId, ConversationId = session.ConversationId,
                Generation = session.Generation, Send = send
            };
        }
    }

    public PluginEventData EnqueueTranscript(string owner, string sessionId, long generation, string text, long sequence)
    {
        text = CleanText(text, 4000);
        if (text.Length == 0) throw new CallProtocolException("invalid_text");
        var session = registry.Get(owner, sessionId);
        if (session.State != "connected" || session.Generation != generation)
            throw new CallProtocolException("stale_generation");
        lock (gate)
        {
            if (stopped || !bindings.TryGetValue(sessionId, out var binding) || binding.Owner != owner)
                throw new CallProtocolException("session_not_found");
            binding.Generation = generation;
        }
        var item = new PluginEventData
        {
            PluginId = "realtime.call", ConversationId = session.ConversationId,
            ExternalEventId = sessionId + ":transcript:" + sequence,
            Role = "user", Content = "[实时通话] " + text,
            Realm = TraceRealmValues.SharedScene, EvidenceType = EvidenceTypeValues.DialogueExplicit,
            Organ = BodyOrganValues.Voice, Wake = KernelWakeValues.Dialogue, Breaking = true,
            PayloadJson = JsonSerializer.Serialize(new { session_id = sessionId, generation, kind = "final_transcript" }),
            OccurredUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        inbound.Enqueue(item);
        return item;
    }

    public IReadOnlyList<PluginEventData> TakeInbound(int limit = 8)
    {
        var result = new List<PluginEventData>();
        while (result.Count < limit && inbound.TryDequeue(out var item)) result.Add(item);
        return result;
    }

    public async Task<(RealtimeSessionData Session, string OutputId)> SendAssistantAsync(
        string conversationId, string text, CancellationToken cancellationToken)
    {
        text = CleanText(text, 4000);
        if (text.Length == 0) throw new CallProtocolException("invalid_text");
        Binding binding;
        lock (gate)
        {
            if (stopped) throw new CallProtocolException("service_stopped");
            binding = bindings.Values.FirstOrDefault(x =>
                string.Equals(x.ConversationId, conversationId, StringComparison.Ordinal))
                ?? throw new CallProtocolException("session_not_found");
        }
        var session = registry.FindActive(conversationId) ?? throw new CallProtocolException("session_not_found");
        var outputId = Guid.NewGuid().ToString("N");
        if (!registry.TryRegisterOutput(session.SessionId, session.Generation, outputId, 0))
            throw new CallProtocolException("stale_generation");
        lock (gate)
        {
            outputs[outputId] = new Output
            {
                SessionId = session.SessionId, ConversationId = conversationId, OutputId = outputId,
                Generation = session.Generation, Text = text
            };
            binding.Generation = session.Generation;
        }
        var message = JsonSerializer.Serialize(new
        {
            op = "assistant_text", session_id = session.SessionId, generation = session.Generation,
            output_id = outputId, text
        }, CallControlConnection.JsonOptions);
        try { await binding.Send(message, cancellationToken); }
        catch
        {
            lock (gate) outputs.Remove(outputId);
            throw new CallProtocolException("client_disconnected");
        }
        return (session, outputId);
    }

    public RealtimePlaybackReceiptData ConfirmOutputDuration(string owner, string sessionId, long generation,
        string outputId, long durationMs) =>
        registry.ConfirmOutputDuration(owner, sessionId, generation, outputId, durationMs);

    public RealtimePlaybackReceiptData ReportPlayback(string owner, string sessionId, long generation,
        string outputId, long playedMs, bool completed)
    {
        var receipt = registry.ReportPlayback(owner, sessionId, generation, outputId, playedMs, completed);
        if (completed) Archive(outputId, receipt);
        return receipt;
    }

    public RealtimeSessionData Interrupt(string owner, string sessionId, long generation)
    {
        ArchivePartial(owner, sessionId);
        var session = registry.Interrupt(owner, sessionId, generation);
        lock (gate) if (bindings.TryGetValue(sessionId, out var binding)) binding.Generation = session.Generation;
        return session;
    }

    public RealtimeSessionData End(string owner, string sessionId)
    {
        ArchivePartial(owner, sessionId);
        var session = registry.End(owner, sessionId);
        lock (gate) bindings.Remove(sessionId);
        return session;
    }

    public void Disconnect(string owner)
    {
        string[] sessionIds;
        lock (gate) sessionIds = bindings.Values.Where(x => x.Owner == owner).Select(x => x.SessionId).ToArray();
        foreach (var sessionId in sessionIds) ArchivePartial(owner, sessionId);
        lock (gate)
        {
            foreach (var sessionId in sessionIds) bindings.Remove(sessionId);
        }
        registry.Disconnect(owner);
    }

    public void Stop()
    {
        lock (gate)
        {
            stopped = true;
            bindings.Clear();
            outputs.Clear();
            while (inbound.TryDequeue(out _)) { }
        }
    }

    private void ArchivePartial(string owner, string sessionId)
    {
        IReadOnlyList<RealtimePlaybackReceiptData> receipts;
        try { receipts = registry.Receipts(owner, sessionId); }
        catch (CallProtocolException) { return; }
        foreach (var receipt in receipts.Where(x => !x.Completed && x.PlayedMs > 0)) Archive(receipt.OutputId, receipt);
    }

    private void Archive(string outputId, RealtimePlaybackReceiptData receipt)
    {
        Output? output;
        lock (gate)
        {
            if (!outputs.TryGetValue(outputId, out output) || output.Archived) return;
            output.Archived = true;
        }
        var text = output.Text;
        if (!receipt.Completed)
        {
            if (receipt.DurationMs <= 0 || receipt.PlayedMs <= 0) return;
            var chars = Math.Clamp((int)Math.Floor(text.Length * (receipt.PlayedMs / (double)receipt.DurationMs)), 0, text.Length);
            if (chars < text.Length && chars > 0 && char.IsHighSurrogate(text[chars - 1])) chars--;
            text = text[..chars].TrimEnd();
            if (text.Length == 0) return;
        }
        inbound.Enqueue(new PluginEventData
        {
            PluginId = "realtime.call", ConversationId = output.ConversationId,
            ExternalEventId = output.OutputId + ":played", Role = "assistant", Content = text,
            Realm = TraceRealmValues.Unclassified, EvidenceType = EvidenceTypeValues.AssPerformed,
            Organ = BodyOrganValues.Voice,
            PayloadJson = JsonSerializer.Serialize(new
            {
                session_id = output.SessionId, generation = output.Generation, output_id = output.OutputId,
                played_ms = receipt.PlayedMs, duration_ms = receipt.DurationMs, completed = receipt.Completed
            }),
            OccurredUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });
    }

    private static string CleanText(string value, int max)
    {
        value = new string((value ?? string.Empty).Where(x => x is '\n' or '\t' || !char.IsControl(x)).ToArray()).Trim();
        return value.Length <= max ? value : value[..max];
    }
}
