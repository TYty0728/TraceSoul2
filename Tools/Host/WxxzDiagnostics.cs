using System;
using System.Collections.Generic;
using System.Linq;

namespace TraceSoul2.Host;

/// <summary>Authenticated local diagnostics, bounded in RAM; never a source of Moments.</summary>
public sealed class WxxzDiagnostics
{
    public sealed record Entry(long sequence, long unixMs, string phase, string code, string reason,
        string rawText, string text, string pronunciation, string matchMode, double? voiceScore,
        bool? voiceConfirmed, bool admitted);
    private readonly object sync = new();
    private readonly Queue<Entry> entries = new();
    private long sequence;
    private static string Clip(string text) => text?.Length > 500 ? text[..500] + "…" : text;
    public void Add(string phase, string code, string reason, WxxzAttention.Correction correction = null,
        double? score = null, bool? voiceConfirmed = null, bool admitted = false)
    {
        lock(sync)
        {
            entries.Enqueue(new Entry(++sequence, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), phase, code, reason,
                Clip(correction?.RawText), Clip(correction?.Text), correction?.Pronunciation, correction?.MatchMode,
                score, voiceConfirmed, admitted));
            while(entries.Count > 40) entries.Dequeue();
        }
    }
    public Entry[] Snapshot() { lock(sync) return entries.ToArray(); }
    public void Clear() { lock(sync) entries.Clear(); }
}
