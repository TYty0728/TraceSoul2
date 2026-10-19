using System.Text.Json;

namespace TraceSoul2.ExternalPlugins.RealtimeCall;

/// <summary>受信任音频网关提取特征，服务端比对本地录入的声纹；不以客户端的 verified=true 放行。</summary>
public sealed class VoiceprintGate
{
    public const string Model = "speechbrain/spkrec-ecapa-voxceleb";
    public const int Dimensions = 192;
    private readonly string? profilePath;
    private readonly double threshold;
    public bool Required { get; }

    public VoiceprintGate(string? profilePath, bool required = true, double threshold = 0.65)
    {
        if (!double.IsFinite(threshold) || threshold is < 0.4 or > 0.95)
            throw new ArgumentOutOfRangeException(nameof(threshold));
        this.profilePath = profilePath;
        this.threshold = threshold;
        Required = required;
    }

    public string Status => !Required ? "disabled" : LoadProfile() == null ? "enrollment_required" : "ready";

    // 返回稳定原因，不返回声纹、输入文本或供应商错误。失败不产生用户事件。
    public string? RejectReason(JsonElement request)
    {
        if (!Required) return null;
        var profile = LoadProfile();
        if (profile == null) return "voiceprint_not_enrolled";
        if (!request.TryGetProperty("voice", out var voice) || voice.ValueKind != JsonValueKind.Object)
            return "voice_evidence_required";
        if (!voice.TryGetProperty("model", out var model) || model.ValueKind != JsonValueKind.String || model.GetString() != Model)
            return "voice_model_mismatch";
        if (!voice.TryGetProperty("playback_active", out var playback) || playback.ValueKind != JsonValueKind.False)
            return "playback_echo_guard";
        if (!voice.TryGetProperty("windows", out var windows) || windows.ValueKind != JsonValueKind.Array ||
            windows.GetArrayLength() is < 1 or > 4) return "invalid_voice_evidence";
        foreach (var window in windows.EnumerateArray())
        {
            if (window.ValueKind != JsonValueKind.Object || !window.TryGetProperty("speech_ms", out var ms) ||
                ms.ValueKind != JsonValueKind.Number || !ms.TryGetInt32(out var duration) || duration is < 800 or > 2500)
                return "insufficient_speech";
            if (!window.TryGetProperty("embedding", out var embedding) || Vector(embedding) is not { } vector)
                return "invalid_voice_evidence";
            if (profile.Zip(vector, (a, b) => a * b).Sum() < threshold)
                return "speaker_mismatch";
        }
        return null;
    }

    private double[]? LoadProfile()
    {
        try
        {
            if (profilePath == null || !File.Exists(profilePath) || new FileInfo(profilePath).Length > 32 * 1024) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(profilePath), new JsonDocumentOptions { MaxDepth = 8 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("model", out var model) ||
                model.ValueKind != JsonValueKind.String || model.GetString() != Model ||
                !root.TryGetProperty("sample_count", out var samples) || samples.ValueKind != JsonValueKind.Number ||
                !samples.TryGetInt32(out var count) || count < 3 ||
                !root.TryGetProperty("speech_ms", out var duration) || duration.ValueKind != JsonValueKind.Number ||
                !duration.TryGetInt32(out var ms) || ms < 15000 ||
                !root.TryGetProperty("embedding", out var embedding)) return null;
            return Vector(embedding);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static double[]? Vector(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != Dimensions) return null;
        var vector = new double[Dimensions];
        var i = 0;
        foreach (var value in element.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var n) ||
                !double.IsFinite(n) || Math.Abs(n) > 10000) return null;
            vector[i++] = n;
        }
        var norm = Math.Sqrt(vector.Sum(x => x * x));
        return norm < 1e-8 ? null : vector.Select(x => x / norm).ToArray();
    }
}
