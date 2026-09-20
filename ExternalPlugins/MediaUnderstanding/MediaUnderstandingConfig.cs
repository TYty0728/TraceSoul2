using System.Text.Json;

namespace TraceSoul2.ExternalPlugins.MediaUnderstanding;

public sealed class MediaUnderstandingConfig
{
    public bool Enabled { get; private set; } = true;
    public string YtDlpPath { get; private set; } = "yt-dlp";
    public string FfmpegPath { get; private set; } = "ffmpeg";
    public string FfprobePath { get; private set; } = "ffprobe";
    public int MaxDownloadMb { get; private set; } = 150;
    public int MaxDurationSeconds { get; private set; } = 1200;
    public int MaxFrames { get; private set; } = 8;
    public int ProcessTimeoutSeconds { get; private set; } = 180;
    public int MaxConcurrentAnalyses { get; private set; } = 1;

    public static MediaUnderstandingConfig Load(string? packageDirectory, string? dataDirectory)
    {
        var value = new MediaUnderstandingConfig();
        value.Apply(Path.Combine(packageDirectory ?? string.Empty, "plugin.json"));
        value.Apply(Path.Combine(dataDirectory ?? string.Empty, "config.json"));
        value.Validate();
        return value;
    }

    private void Apply(string path)
    {
        if (!File.Exists(path)) return;
        using var document = JsonDocument.Parse(File.ReadAllText(path),
            new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        Enabled = ReadBool(root, "enabled", Enabled);
        YtDlpPath = ReadText(root, "yt_dlp_path", YtDlpPath);
        FfmpegPath = ReadText(root, "ffmpeg_path", FfmpegPath);
        FfprobePath = ReadText(root, "ffprobe_path", FfprobePath);
        MaxDownloadMb = ReadInt(root, "max_download_mb", MaxDownloadMb);
        MaxDurationSeconds = ReadInt(root, "max_duration_seconds", MaxDurationSeconds);
        MaxFrames = ReadInt(root, "max_frames", MaxFrames);
        ProcessTimeoutSeconds = ReadInt(root, "process_timeout_seconds", ProcessTimeoutSeconds);
        MaxConcurrentAnalyses = ReadInt(root, "max_concurrent_analyses", MaxConcurrentAnalyses);
    }

    private void Validate()
    {
        YtDlpPath = ValidateExecutable(YtDlpPath, "yt-dlp");
        FfmpegPath = ValidateExecutable(FfmpegPath, "ffmpeg");
        FfprobePath = ValidateExecutable(FfprobePath, "ffprobe");
        if (MaxDownloadMb is < 10 or > 1024) throw new InvalidOperationException("媒体最大下载量必须为 10–1024 MB。");
        if (MaxDurationSeconds is < 10 or > 14_400) throw new InvalidOperationException("视频最大时长必须为 10–14400 秒。");
        if (MaxFrames is < 2 or > 16) throw new InvalidOperationException("视频抽帧数必须为 2–16。");
        if (ProcessTimeoutSeconds is < 30 or > 1800) throw new InvalidOperationException("媒体处理超时必须为 30–1800 秒。");
        if (MaxConcurrentAnalyses is < 1 or > 4) throw new InvalidOperationException("媒体并发数必须为 1–4。");
    }

    private static string ValidateExecutable(string value, string name)
    {
        value = (value ?? string.Empty).Trim();
        if (value.Length == 0 || value.Length > 1024 || value.Contains('\0') || value.Contains('\r') || value.Contains('\n'))
            throw new InvalidOperationException(name + " 路径无效。");
        return value;
    }

    private static string ReadText(JsonElement root, string name, string fallback)
    {
        return root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback : fallback;
    }

    private static int ReadInt(JsonElement root, string name, int fallback)
    {
        return root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number : fallback;
    }

    private static bool ReadBool(JsonElement root, string name, bool fallback)
    {
        return root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean() : fallback;
    }
}
