using TraceSoul2.Data;

namespace TraceSoul2.ExternalPlugins.RealtimeCall;

public sealed class CallProtocolException : Exception
{
    public string Code { get; }
    public CallProtocolException(string code) : base(code) => Code = code;
}

/// <summary>会话与连接绑定；所有检查/变更在同一锁内，快照不暴露可变内部对象。</summary>
public sealed class RealtimeSessionRegistry
{
    private readonly object gate = new();
    private readonly Dictionary<string, Session> sessions = new(StringComparer.Ordinal);
    private readonly TimeProvider clock;
    private readonly int capacity;
    private bool stopped;

    private sealed class Session
    {
        public required string Owner;
        public required RealtimeSessionData Data;
        public Dictionary<string, RealtimePlaybackReceiptData> Outputs { get; } = new(StringComparer.Ordinal);
    }

    public RealtimeSessionRegistry(TimeProvider? clock = null, int capacity = 8)
    {
        this.clock = clock ?? TimeProvider.System;
        this.capacity = Math.Clamp(capacity, 1, 128);
    }

    public int ActiveCount { get { lock (gate) return sessions.Values.Count(x => x.Data.State == "connected"); } }

    public RealtimeSessionData Start(string owner, string conversationId, string transport)
    {
        lock (gate)
        {
            if (stopped) throw new CallProtocolException("service_stopped");
            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(conversationId) ||
                conversationId.Length > 128) throw new CallProtocolException("invalid_identity");
            if (transport is not ("unity" or "qq_av_bridge" or "debug"))
                throw new CallProtocolException("unsupported_transport");
            if (sessions.Values.Any(x => x.Owner == owner)) throw new CallProtocolException("connection_already_bound");
            if (sessions.Values.Any(x => x.Data.ConversationId == conversationId && x.Data.State == "connected"))
                throw new CallProtocolException("conversation_busy");
            if (sessions.Count >= capacity) throw new CallProtocolException("capacity_reached");
            var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
            var session = new Session
            {
                Owner = owner,
                Data = new RealtimeSessionData
                {
                    SessionId = Guid.NewGuid().ToString("N"), ConversationId = conversationId, Transport = transport,
                    StartedUnixMs = now, LastActivityUnixMs = now
                }
            };
            sessions.Add(session.Data.SessionId, session);
            return Snapshot(session.Data);
        }
    }

    public RealtimeSessionData Get(string owner, string sessionId)
    {
        lock (gate) return Snapshot(Owned(owner, sessionId).Data);
    }

    public RealtimeSessionData Interrupt(string owner, string sessionId, long expectedGeneration)
    {
        lock (gate)
        {
            var session = Owned(owner, sessionId);
            Current(session, expectedGeneration);
            Invalidate(session);
            Touch(session);
            return Snapshot(session.Data);
        }
    }

    public RealtimeSessionData End(string owner, string sessionId)
    {
        lock (gate)
        {
            var session = Owned(owner, sessionId);
            if (session.Data.State == "connected")
            {
                Invalidate(session);
                session.Data.State = "ended";
                session.Data.EndReason = "client_hangup";
                Touch(session);
            }
            return Snapshot(session.Data);
        }
    }

    // 仅供后端使用；客户端不能注册自己的输出来伪造播放证据。
    public bool TryRegisterOutput(string sessionId, long generation, string outputId, long durationMs)
    {
        lock (gate)
        {
            if (!sessions.TryGetValue(sessionId, out var session) || session.Data.State != "connected" ||
                session.Data.Generation != generation || stopped) return false;
            if (string.IsNullOrWhiteSpace(outputId) || outputId.Length > 128 || durationMs is < 0 or > 600_000)
                throw new CallProtocolException("invalid_output");
            if (session.Outputs.ContainsKey(outputId)) return false;
            if (session.Outputs.Count >= 256) throw new CallProtocolException("output_capacity_reached");
            session.Outputs.Add(outputId, new RealtimePlaybackReceiptData
            {
                SessionId = sessionId, OutputId = outputId, Generation = generation, DurationMs = durationMs
            });
            return true;
        }
    }

    /// <summary>客户端只能为服务端已经产生的输出补充 TTS 实际时长，不能凭空创建输出。</summary>
    public RealtimePlaybackReceiptData ConfirmOutputDuration(string owner, string sessionId, long generation,
        string outputId, long durationMs)
    {
        lock (gate)
        {
            var session = Owned(owner, sessionId);
            Current(session, generation);
            if (!session.Outputs.TryGetValue(outputId, out var output) || output.Generation != generation)
                throw new CallProtocolException("unknown_output");
            if (durationMs is < 1 or > 600_000 || output.PlayedMs > 0 || output.Completed ||
                (output.DurationMs > 0 && output.DurationMs != durationMs))
                throw new CallProtocolException("invalid_output_duration");
            output.DurationMs = durationMs;
            Touch(session);
            return Snapshot(output);
        }
    }

    public RealtimePlaybackReceiptData ReportPlayback(string owner, string sessionId, long generation,
        string outputId, long playedMs, bool completed)
    {
        lock (gate)
        {
            var session = Owned(owner, sessionId);
            Current(session, generation);
            if (!session.Outputs.TryGetValue(outputId, out var output) || output.Generation != generation)
                throw new CallProtocolException("unknown_output");
            if (output.DurationMs <= 0) throw new CallProtocolException("output_not_ready");
            if (playedMs < output.PlayedMs || playedMs > output.DurationMs || playedMs < 0 ||
                (completed && playedMs != output.DurationMs) || (output.Completed && !completed))
                throw new CallProtocolException("invalid_playback_progress");
            output.PlayedMs = playedMs;
            output.Completed = completed;
            Touch(session);
            return Snapshot(output);
        }
    }

    public IReadOnlyList<RealtimePlaybackReceiptData> Receipts(string owner, string sessionId)
    {
        lock (gate) return Owned(owner, sessionId).Outputs.Values.Select(Snapshot).ToArray();
    }

    public RealtimeSessionData? FindActive(string conversationId)
    {
        lock (gate)
        {
            if (stopped) return null;
            var session = sessions.Values.FirstOrDefault(x => x.Data.State == "connected" &&
                string.Equals(x.Data.ConversationId, conversationId, StringComparison.Ordinal));
            return session == null ? null : Snapshot(session.Data);
        }
    }

    public void Heartbeat(string owner)
    {
        lock (gate)
            foreach (var session in sessions.Values.Where(x => x.Owner == owner && x.Data.State == "connected")) Touch(session);
    }

    public void Disconnect(string owner)
    {
        lock (gate)
        {
            foreach (var session in sessions.Values.Where(x => x.Owner == owner).ToArray())
            {
                Invalidate(session);
                sessions.Remove(session.Data.SessionId);
            }
        }
    }

    public void Stop()
    {
        lock (gate)
        {
            stopped = true;
            foreach (var session in sessions.Values) Invalidate(session);
            sessions.Clear();
        }
    }

    private Session Owned(string owner, string sessionId)
    {
        if (stopped || !sessions.TryGetValue(sessionId, out var session) || session.Owner != owner)
            throw new CallProtocolException("session_not_found");
        return session;
    }

    private static void Current(Session session, long generation)
    {
        if (session.Data.State != "connected") throw new CallProtocolException("session_ended");
        if (session.Data.Generation != generation) throw new CallProtocolException("stale_generation");
    }

    private static void Invalidate(Session session)
    {
        foreach (var output in session.Outputs.Values.Where(x => !x.Completed)) output.Interrupted = true;
        session.Data.Generation++;
    }

    private void Touch(Session session) => session.Data.LastActivityUnixMs = clock.GetUtcNow().ToUnixTimeMilliseconds();
    private static RealtimeSessionData Snapshot(RealtimeSessionData d) => new()
    {
        SessionId = d.SessionId, ConversationId = d.ConversationId, Transport = d.Transport, State = d.State,
        Generation = d.Generation, StartedUnixMs = d.StartedUnixMs, LastActivityUnixMs = d.LastActivityUnixMs,
        EndReason = d.EndReason
    };
    private static RealtimePlaybackReceiptData Snapshot(RealtimePlaybackReceiptData d) => new()
    {
        SessionId = d.SessionId, OutputId = d.OutputId, Generation = d.Generation,
        DurationMs = d.DurationMs, PlayedMs = d.PlayedMs, Completed = d.Completed, Interrupted = d.Interrupted
    };
}
