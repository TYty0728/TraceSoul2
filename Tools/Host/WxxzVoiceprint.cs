using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace TraceSoul2.Host;

// The trusted loopback worker extracts features. Only the Soul Host binds an
// enrollment to its owner and compares incoming evidence. Feature vectors never go to the UI.
public sealed class WxxzVoiceprint
{
    public const string Model = "3dspeaker/campplus-zh-cn-16k:f682b514c05d947ee3fa91cd6ec6c5c7543479a128373fa29b1faedccd21fd11";
    public const double Threshold = 0.65; // Initial operating point; requires field calibration.
    private readonly string path;
    private readonly Func<DateTimeOffset> now;
    private long diagnosticUntil;
    public sealed record DiagnosticSettings(double threshold = Threshold, int durationSeconds = 300);
    private bool DiagnosticActive => System.Threading.Interlocked.Read(ref diagnosticUntil) > now().ToUnixTimeMilliseconds();
    public void ConfigureDiagnostic(DiagnosticSettings settings)
    {
        if (settings == null || (settings.threshold != 0 && settings.threshold != Threshold) ||
            settings.durationSeconds is < 1 or > 900)
            throw new ArgumentException("调试阈值只支持0或0.65，有效期为1～900秒。");
        if (settings.threshold == 0 && !IsEnrolled) throw new InvalidOperationException("请先录入本人声纹。");
        System.Threading.Interlocked.Exchange(ref diagnosticUntil, settings.threshold == 0
            ? now().AddSeconds(settings.durationSeconds).ToUnixTimeMilliseconds() : 0);
    }
    private sealed class Sample
    {
        public double[] embedding { get; set; }
        public List<double[]> windows { get; set; }
        public int speech_ms { get; set; }
        public string text { get; set; }
    }
    private Sample[] pending;
    public WxxzVoiceprint(string path, Func<DateTimeOffset> now = null)
    { this.path = path; this.now = now ?? (() => DateTimeOffset.UtcNow); }
    public object Status()
    {
        var saved = LoadSamples();
        var slots = pending ?? saved ?? new Sample[3];
        return new { enrolled = IsEnrolled, samples = slots.Count(s => s != null), requiredSamples = 3,
            replacing = pending != null && pending.Any(s => s == null) && IsEnrolled,
            legacyProfile = IsEnrolled && saved == null,
            slots = slots.Select((s, i) => new { number = i + 1, accepted = s != null,
                speechMs = s?.speech_ms ?? 0, text = s?.text ?? "" }).ToArray(),
            threshold = DiagnosticActive ? 0 : Threshold, diagnosticBypass = DiagnosticActive,
            diagnosticUntilUnixMs = System.Threading.Interlocked.Read(ref diagnosticUntil),
            decisionBasis = "whole_utterance", calibrated = false };
    }
    public void ClearPending() { pending = null; }

    private static double[] Vector(JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Array || json.GetArrayLength() != 192)
            throw new InvalidOperationException("无效的声纹特征。");
        var vector = json.EnumerateArray().Select(x => x.GetDouble()).ToArray();
        if (vector.Any(x => !double.IsFinite(x) || Math.Abs(x) > 10000))
            throw new InvalidOperationException("无效的声纹特征。");
        return Normalize(vector);
    }
    private static double[] Normalize(double[] values)
    {
        var norm = Math.Sqrt(values.Sum(x => x * x));
        if (!double.IsFinite(norm) || norm < 1e-8) throw new InvalidOperationException("无效的声纹特征。");
        return values.Select(x => x / norm).ToArray();
    }
    private static List<double[]> Evidence(JsonElement voice, out int speechMs)
    {
        if (voice.ValueKind != JsonValueKind.Object || voice.GetProperty("model").GetString() != Model)
            throw new InvalidOperationException("声纹模型不匹配或语音过短。");
        speechMs = voice.GetProperty("speech_ms").GetInt32();
        var windows = voice.GetProperty("windows");
        if (speechMs is < 1200 or > 20000 || windows.GetArrayLength() is < 1 or > 10)
            throw new InvalidOperationException("声纹语音时长无效。");
        var vectors = new List<double[]>();
        foreach (var window in windows.EnumerateArray())
        {
            var ms = window.GetProperty("speech_ms").GetInt32();
            if (ms is < 1200 or > 2500) throw new InvalidOperationException("声纹窗口过短。");
            vectors.Add(Vector(window.GetProperty("embedding")));
        }
        if (windows.GetArrayLength() * 2500 < speechMs)
            throw new InvalidOperationException("声纹窗口未覆盖整句。");
        return vectors;
    }
    private static double Cosine(double[] a, double[] b) => a.Zip(b, (x, y) => x * y).Sum();
    private double[] Load()
    {
        try
        {
            if (path == null || !File.Exists(path) || new FileInfo(path).Length > 262144) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.GetProperty("model").GetString() != Model || root.GetProperty("sample_count").GetInt32() < 3 ||
                root.GetProperty("speech_ms").GetInt32() < 12000) return null;
            return Vector(root.GetProperty("embedding"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or
            InvalidOperationException or KeyNotFoundException or FormatException) { return null; }
    }
    private Sample[] LoadSamples()
    {
        if (!IsEnrolled) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("samples", out var items) || items.GetArrayLength() != 3) return null;
            return items.EnumerateArray().Select(s => new Sample {
                embedding = Vector(s.GetProperty("embedding")),
                windows = s.GetProperty("windows").EnumerateArray().Select(Vector).ToList(),
                speech_ms = s.GetProperty("speech_ms").GetInt32(), text = s.GetProperty("text").GetString() ?? ""
            }).Select(s => s.speech_ms is >= 4000 and <= 20000 && s.text.Length <= 4000 && s.windows.Count is >= 1 and <= 10
                ? s : throw new InvalidOperationException("无效的分段声纹。")).ToArray();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or
            InvalidOperationException or KeyNotFoundException or FormatException) { return null; }
    }
    public bool IsEnrolled => Load() != null;
    public object Add(JsonElement voice, bool restart, int? sampleNumber = null, string text = "")
    {
        if (path == null) throw new InvalidOperationException("声纹保存目录未配置。");
        if (sampleNumber is < 1 or > 3) throw new InvalidOperationException("请选择第1～3段声纹。");
        if (text == null || text.Length > 4000) throw new InvalidOperationException("听写结果无效。");
        var all = (Sample[])(restart ? new Sample[3] : pending ?? LoadSamples() ?? new Sample[3]).Clone();
        var index = sampleNumber.HasValue ? sampleNumber.Value - 1 : Array.FindIndex(all, s => s == null);
        if (index < 0) throw new InvalidOperationException("声纹已完成，请选择单段重录或明确重新录入。");
        var vectors = Evidence(voice, out var ms);
        if (ms < 4000) throw new InvalidOperationException($"本段有效语音{ms / 1000.0:0.00}秒，不足4秒；请看到录音提示后连续说话。");
        // Enrollment is explicitly initiated by the owner. Compare complete
        // utterances; short-window scores are diagnostics, not speaker diarization.
        var clip = Vector(voice.GetProperty("embedding"));
        var others = all.Where((s, i) => i != index && s != null).ToArray();
        var clipMinimum = others.Length == 0 ? 1 : others.Min(s => Cosine(clip, s.embedding));
        if (clipMinimum < Threshold)
            throw new InvalidOperationException($"本段与已录声音不一致（最低相似度{clipMinimum:0.00}）；本段未加入，请用平时音量重录。");
        all[index] = new Sample { embedding = clip, windows = vectors, speech_ms = ms, text = text };
        var accepted = all.Where(s => s != null).ToArray();
        var centroid = Normalize(Enumerable.Range(0, 192).Select(i => accepted.Sum(s => s.embedding[i])).ToArray());
        var nextSamples = accepted.Length;
        var nextDuration = accepted.Sum(s => s.speech_ms);
        if (nextSamples == 3)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(new { model = Model, sample_count = nextSamples,
                    speech_ms = nextDuration, embedding = centroid, samples = all, threshold = Threshold,
                    decision_basis = "whole_utterance", calibrated = false }));
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        pending = all;
        return Status();
    }
    public (bool matched, double? score) Match(JsonElement voice)
    {
        var profile = Load();
        if (profile == null) return (false, null);
        var bypass = DiagnosticActive;
        try
        {
            Evidence(voice, out _);
            var score = Cosine(profile, Vector(voice.GetProperty("embedding")));
            return (bypass || score >= Threshold, Math.Round(score, 4));
        }
        catch (Exception e) when (e is InvalidOperationException or KeyNotFoundException or FormatException) { return (bypass, null); }
    }

    public object Inspect(JsonElement voice)
    {
        var profile = Load();
        if (profile == null) return new { profileReady = false, reason = "请先录入本人声纹。" };
        try
        {
            var vectors = Evidence(voice, out var speechMs);
            var windowScores = vectors.Select(v => Math.Round(Cosine(profile, v), 4)).ToArray();
            var rawWholeScore = Cosine(profile, Vector(voice.GetProperty("embedding")));
            var wholeScore = Math.Round(rawWholeScore, 4);
            return new { profileReady = true, speechMs, wholeScore, windowScores,
                windowMs = voice.GetProperty("windows").EnumerateArray().Select(w => w.GetProperty("speech_ms").GetInt32()).ToArray(),
                score = wholeScore, minimumWindowScore = windowScores.Min(), decisionBasis = "whole_utterance",
                threshold = Threshold, matched = rawWholeScore >= Threshold };
        }
        catch (Exception e) when (e is InvalidOperationException or KeyNotFoundException or FormatException)
        { return new { profileReady = true, reason = "本次有效语音不足或声纹特征不可用，请自然连续说话后再试。" }; }
    }
}
