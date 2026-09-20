using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Text.Json;
using TraceSoul2.Data;
using TraceSoul2.Manager;
using TraceSoul2.Plugins;

namespace TraceSoul2.ExternalPlugins.MediaUnderstanding;

public sealed class MediaPipelineException : Exception
{
    public string Code { get; }
    public MediaPipelineException(string code) : base(code) => Code = code;
}

public sealed class AcquiredMediaAsset : IDisposable
{
    public string WorkingDirectory { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;
    public long? DeclaredDurationMs { get; init; }
    public IReadOnlyList<MediaEvidenceData> Subtitles { get; init; } = Array.Empty<MediaEvidenceData>();
    public Action? OnDispose { get; init; }
    public void Dispose()
    {
        try { OnDispose?.Invoke(); }
        finally
        {
            if (!string.IsNullOrWhiteSpace(WorkingDirectory) && Directory.Exists(WorkingDirectory))
            {
                try { Directory.Delete(WorkingDirectory, true); }
                catch { }
            }
        }
    }
}

public sealed class ExtractedVideoFrame
{
    public long TimestampMs { get; init; }
    public byte[] Bytes { get; init; } = Array.Empty<byte>();
}

public interface IMediaVideoAcquirer
{
    Task<AcquiredMediaAsset> AcquireAsync(MediaSourceData source, CancellationToken cancellationToken);
}

public interface IVideoFrameExtractor
{
    Task<(long DurationMs, IReadOnlyList<ExtractedVideoFrame> Frames)> ExtractAsync(
        AcquiredMediaAsset asset, CancellationToken cancellationToken);
}

public interface IMediaFrameAnalyzer
{
    Task<string> AnalyzeAsync(string question, long durationMs,
        IReadOnlyList<ExtractedVideoFrame> frames, IReadOnlyList<MediaEvidenceData> subtitles,
        TraceTurnContext turn, CancellationToken cancellationToken);
}

public sealed class MediaVideoPipeline
{
    private readonly IMediaVideoAcquirer acquirer;
    private readonly IVideoFrameExtractor extractor;
    private readonly IMediaFrameAnalyzer analyzer;

    public MediaVideoPipeline(IMediaVideoAcquirer acquirer, IVideoFrameExtractor extractor, IMediaFrameAnalyzer analyzer)
    {
        this.acquirer = acquirer;
        this.extractor = extractor;
        this.analyzer = analyzer;
    }

    public async Task<MediaUnderstandingResultData> AnalyzeAsync(
        MediaSourceData source, string question, TraceTurnContext turn, CancellationToken cancellationToken)
    {
        using var asset = await acquirer.AcquireAsync(source, cancellationToken);
        var extracted = await extractor.ExtractAsync(asset, cancellationToken);
        if (extracted.Frames.Count == 0) throw new MediaPipelineException("no_frames");
        var summary = await analyzer.AnalyzeAsync(question, extracted.DurationMs, extracted.Frames,
            asset.Subtitles, turn, cancellationToken);
        if (string.IsNullOrWhiteSpace(summary)) throw new MediaPipelineException("model_empty");
        var evidence = new List<MediaEvidenceData>();
        if (!string.IsNullOrWhiteSpace(asset.Title) || !string.IsNullOrWhiteSpace(asset.Author))
        {
            evidence.Add(new MediaEvidenceData
            {
                Kind = MediaEvidenceKinds.PageMetadata,
                Text = string.Join("｜", new[]
                    {
                        string.IsNullOrWhiteSpace(asset.Title) ? null : "标题：" + Limit(asset.Title, 160),
                        string.IsNullOrWhiteSpace(asset.Author) ? null : "作者：" + Limit(asset.Author, 80)
                    }.Where(x => x != null)),
                EvidenceId = "metadata"
            });
        }
        for (var i = 0; i < extracted.Frames.Count; i++)
        {
            var frame = extracted.Frames[i];
            evidence.Add(new MediaEvidenceData
            {
                Kind = MediaEvidenceKinds.VideoFrame,
                Text = "该时间点抽取的画面已送入视觉模型。",
                StartMs = frame.TimestampMs,
                EndMs = frame.TimestampMs,
                EvidenceId = "frame-" + (i + 1).ToString(CultureInfo.InvariantCulture) + "-" +
                             Convert.ToHexString(SHA256.HashData(frame.Bytes)).Substring(0, 12).ToLowerInvariant()
            });
        }
        evidence.AddRange(asset.Subtitles.Take(40));
        return new MediaUnderstandingResultData
        {
            Source = source,
            Status = asset.Subtitles.Count > 0 ? "visual_and_subtitle_analyzed" : "visual_analyzed",
            Summary = Limit(summary.Trim(), 1600),
            Evidence = evidence,
            Limitations = new List<string>
            {
                "本次结论来自 " + extracted.Frames.Count + " 个离散画面，不代表逐帧看完。",
                asset.Subtitles.Count > 0
                    ? "字幕来自平台提供的人工/自动字幕，可能有错字；本阶段不直接识别音轨。"
                    : "该来源没有取得可用字幕；本阶段不直接识别音轨，不能判断对白、音乐和声音细节。"
            }
        };
    }

    private static string Limit(string value, int length) => value.Length <= length ? value : value.Substring(0, length);
}

public sealed class YtDlpMediaAcquirer : IMediaVideoAcquirer
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".webm", ".mkv", ".flv", ".mov", ".m4v", ".ts" };
    private readonly MediaUnderstandingConfig config;
    private readonly ExternalCommandRunner runner;
    private readonly string workRoot;

    public YtDlpMediaAcquirer(MediaUnderstandingConfig config, ExternalCommandRunner runner, string dataDirectory)
    {
        this.config = config;
        this.runner = runner;
        workRoot = Path.GetFullPath(Path.Combine(dataDirectory, "work"));
        Directory.CreateDirectory(workRoot);
    }

    public async Task<AcquiredMediaAsset> AcquireAsync(MediaSourceData source, CancellationToken cancellationToken)
    {
        if (source == null || string.IsNullOrWhiteSpace(source.CanonicalUrl))
            throw new MediaPipelineException("unsupported_source");
        if (source.Kind == "image_post") throw new MediaPipelineException("image_post_not_implemented");
        if (!ExternalCommandRunner.CanResolve(config.YtDlpPath)) throw new MediaPipelineException("yt_dlp_unavailable");
        var directory = Path.Combine(workRoot, "job-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var result = await runner.RunAsync(config.YtDlpPath, new[]
            {
                "--no-config", "--no-playlist", "--max-downloads", "1",
                "--no-cache-dir", "--no-progress", "--no-warnings", "--no-part",
                "--socket-timeout", "20", "--retries", "2", "--fragment-retries", "2",
                "--max-filesize", config.MaxDownloadMb.ToString(CultureInfo.InvariantCulture) + "M",
                "--match-filter", "duration <= " + config.MaxDurationSeconds.ToString(CultureInfo.InvariantCulture),
                "--write-info-json", "--clean-info-json", "--write-subs", "--write-auto-subs",
                "--sub-langs", "zh.*,en.*", "--sub-format", "vtt/srt/best",
                "-f", "best[height<=720]/best",
                "-P", directory, "-o", "media.%(ext)s", source.CanonicalUrl
            }, directory, TimeSpan.FromSeconds(config.ProcessTimeoutSeconds), cancellationToken, 16_384);
            if (result.ExitCode != 0) throw new MediaPipelineException("source_unavailable");
            var files = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly).ToArray();
            if (files.Length > 32) throw new MediaPipelineException("unexpected_download_output");
            var media = files.Where(x => VideoExtensions.Contains(Path.GetExtension(x)))
                .Where(IsRegularFile)
                .OrderByDescending(x => new FileInfo(x).Length)
                .FirstOrDefault();
            if (media == null) throw new MediaPipelineException("video_not_downloaded");
            var size = new FileInfo(media).Length;
            if (size <= 0 || size > config.MaxDownloadMb * 1024L * 1024L)
                throw new MediaPipelineException("video_too_large");
            var metadata = ReadMetadata(files.FirstOrDefault(x => x.EndsWith(".info.json", StringComparison.OrdinalIgnoreCase)));
            return new AcquiredMediaAsset
            {
                WorkingDirectory = directory,
                FilePath = Path.GetFullPath(media),
                Title = metadata.Title,
                Author = metadata.Author,
                DeclaredDurationMs = metadata.DurationMs,
                Subtitles = SubtitleParser.ParseFiles(files.Where(x =>
                    x.EndsWith(".vtt", StringComparison.OrdinalIgnoreCase) ||
                    x.EndsWith(".srt", StringComparison.OrdinalIgnoreCase)))
            };
        }
        catch
        {
            try { Directory.Delete(directory, true); }
            catch { }
            throw;
        }
    }

    private (string Title, string Author, long? DurationMs) ReadMetadata(string? path)
    {
        if (path == null || !IsRegularFile(path) || new FileInfo(path).Length > 2 * 1024 * 1024) return ("", "", null);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 64 });
            var root = document.RootElement;
            var title = root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
            var author = root.TryGetProperty("uploader", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() ?? "" : "";
            long? duration = null;
            if (root.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetDouble(out var seconds) &&
                double.IsFinite(seconds) && seconds >= 0 && seconds <= config.MaxDurationSeconds)
                duration = checked((long)Math.Round(seconds * 1000));
            return (Clean(title, 160), Clean(author, 80), duration);
        }
        catch { return ("", "", null); }
    }

    private static bool IsRegularFile(string path)
    {
        try { return File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0; }
        catch { return false; }
    }

    private static string Clean(string value, int max)
    {
        value = new string((value ?? string.Empty).Where(x => !char.IsControl(x)).ToArray()).Trim();
        return value.Length <= max ? value : value.Substring(0, max);
    }
}

public sealed class FfmpegVideoFrameExtractor : IVideoFrameExtractor
{
    private readonly MediaUnderstandingConfig config;
    private readonly ExternalCommandRunner runner;

    public FfmpegVideoFrameExtractor(MediaUnderstandingConfig config, ExternalCommandRunner runner)
    {
        this.config = config;
        this.runner = runner;
    }

    public async Task<(long DurationMs, IReadOnlyList<ExtractedVideoFrame> Frames)> ExtractAsync(
        AcquiredMediaAsset asset, CancellationToken cancellationToken)
    {
        if (!ExternalCommandRunner.CanResolve(config.FfprobePath) || !ExternalCommandRunner.CanResolve(config.FfmpegPath))
            throw new MediaPipelineException("ffmpeg_unavailable");
        var probe = await runner.RunAsync(config.FfprobePath, new[]
        {
            "-v", "error", "-show_entries", "format=duration", "-of", "json", asset.FilePath
        }, asset.WorkingDirectory, TimeSpan.FromSeconds(Math.Min(60, config.ProcessTimeoutSeconds)), cancellationToken, 65_536);
        if (probe.ExitCode != 0 || probe.OutputTruncated) throw new MediaPipelineException("invalid_video");
        var durationMs = ParseDuration(probe.StandardOutput);
        if (durationMs <= 0) throw new MediaPipelineException("invalid_duration");
        if (durationMs > config.MaxDurationSeconds * 1000L) throw new MediaPipelineException("video_too_long");
        var timestamps = SamplingPoints(durationMs, config.MaxFrames);
        var frames = new List<ExtractedVideoFrame>();
        for (var i = 0; i < timestamps.Count; i++)
        {
            var destination = Path.Combine(asset.WorkingDirectory, "frame-" + i.ToString("D2", CultureInfo.InvariantCulture) + ".jpg");
            var seconds = (timestamps[i] / 1000d).ToString("0.###", CultureInfo.InvariantCulture);
            var result = await runner.RunAsync(config.FfmpegPath, new[]
            {
                "-nostdin", "-hide_banner", "-loglevel", "error", "-ss", seconds,
                "-i", asset.FilePath, "-frames:v", "1", "-vf", "scale=960:-2:force_original_aspect_ratio=decrease",
                "-q:v", "4", "-y", destination
            }, asset.WorkingDirectory, TimeSpan.FromSeconds(Math.Min(60, config.ProcessTimeoutSeconds)), cancellationToken, 4096);
            if (result.ExitCode != 0 || !File.Exists(destination)) continue;
            var info = new FileInfo(destination);
            if (info.Length is < 1024 or > 8 * 1024 * 1024) continue;
            var bytes = await File.ReadAllBytesAsync(destination, cancellationToken);
            if (LlmImagePartData.GuessMime(bytes) != "image/jpeg") continue;
            frames.Add(new ExtractedVideoFrame { TimestampMs = timestamps[i], Bytes = bytes });
        }
        if (frames.Count < 2) throw new MediaPipelineException("no_frames");
        return (durationMs, frames);
    }

    public static IReadOnlyList<long> SamplingPoints(long durationMs, int count)
    {
        if (durationMs <= 0 || count < 1) return Array.Empty<long>();
        if (durationMs <= 1000) return new[] { 0L };
        var usableEnd = Math.Max(0, durationMs - 250);
        if (count == 1) return new[] { usableEnd / 2 };
        var start = Math.Min(500L, usableEnd / 10);
        if (usableEnd <= start) return new[] { 0L };
        var points = new List<long>(count);
        for (var i = 0; i < count; i++)
            points.Add(start + (long)Math.Round((usableEnd - start) * (i / (double)(count - 1))));
        return points.Distinct().ToArray();
    }

    private static long ParseDuration(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var duration = document.RootElement.GetProperty("format").GetProperty("duration");
            double seconds;
            if (duration.ValueKind == JsonValueKind.String)
            {
                if (!double.TryParse(duration.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out seconds)) return 0;
            }
            else if (duration.ValueKind == JsonValueKind.Number && duration.TryGetDouble(out seconds)) { }
            else return 0;
            if (!double.IsFinite(seconds) || seconds <= 0 || seconds > 86_400) return 0;
            return checked((long)Math.Round(seconds * 1000));
        }
        catch { return 0; }
    }
}

public sealed class MultimodalFrameAnalyzer : IMediaFrameAnalyzer
{
    public async Task<string> AnalyzeAsync(string question, long durationMs,
        IReadOnlyList<ExtractedVideoFrame> frames, IReadOnlyList<MediaEvidenceData> subtitles,
        TraceTurnContext turn, CancellationToken cancellationToken)
    {
        var providers = turn?.Services?.Providers;
        var endpoint = providers?.ResolveExplicitSlot(LlmSlotNames.Multimodal);
        if (endpoint == null || string.IsNullOrWhiteSpace(endpoint.ApiKey))
            throw new MediaPipelineException("multimodal_unconfigured");
        var client = providers!.CreateClient(endpoint.ProviderId, endpoint.Model, false);
        if (client == null) throw new MediaPipelineException("multimodal_unconfigured");
        var timeline = string.Join("、", frames.Select((x, i) =>
            "图" + (i + 1) + "=" + FormatTime(x.TimestampMs)));
        var ask = "问题：" + (string.IsNullOrWhiteSpace(question) ? "请概括视频画面内容与变化。" : Limit(question.Trim(), 500)) +
                  "\n视频时长：" + FormatTime(durationMs) + "。抽样画面顺序与时间：" + timeline +
                  "。请仅依据这些画面" + (subtitles.Count > 0 ? "与下列字幕" : string.Empty) +
                  "作答，指出关键内容发生的时间；没有证据的声音和未抽到的过程要明确说不知道。" +
                  (subtitles.Count == 0 ? string.Empty : "\n字幕摘录：\n" + Limit(string.Join("\n", subtitles.Select(x =>
                      "[" + FormatTime(x.StartMs ?? 0) + "] " + x.Text)), 12_000));
        var messages = new List<DeepSeekMessageData>
        {
            new("system", "你负责分析视频抽样画面。不要把标题、封面或推测当成视频事实；不要声称逐帧看完整段视频。输出中文，先直接回答，再简述时间线和证据限制。"),
            new("user", ask)
            {
                images = frames.Select(x => new LlmImagePartData { bytes = x.Bytes, mime = "image/jpeg" }).ToList()
            }
        };
        try
        {
            var raw = await client.CompleteTextAsync(messages, cancellationToken);
            return Limit((raw ?? string.Empty).Trim(), 1600);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { throw new MediaPipelineException("model_failed"); }
    }

    private static string FormatTime(long ms) => TimeSpan.FromMilliseconds(ms).ToString(ms >= 3_600_000 ? @"h\:mm\:ss\.fff" : @"m\:ss\.fff");
    private static string Limit(string value, int length) => value.Length <= length ? value : value.Substring(0, length);
}

public static class SubtitleParser
{
    private static readonly Regex TimeLine = new(
        @"(?<start>\d{1,2}:\d{2}(?::\d{2})?[\.,]\d{3})\s*-->\s*(?<end>\d{1,2}:\d{2}(?::\d{2})?[\.,]\d{3})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Tags = new(@"<[^>]{0,200}>", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlyList<MediaEvidenceData> ParseFiles(IEnumerable<string> paths)
    {
        var result = new List<MediaEvidenceData>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long totalBytes = 0;
        var totalChars = 0;
        foreach (var path in paths.Take(8))
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length is <= 0 or > 1024 * 1024 ||
                    (info.Attributes & FileAttributes.ReparsePoint) != 0 || totalBytes + info.Length > 2 * 1024 * 1024) continue;
                totalBytes += info.Length;
                foreach (var cue in Parse(File.ReadAllText(path)))
                {
                    var key = cue.StartMs + "\n" + cue.EndMs + "\n" + cue.Text;
                    if (seen.Add(key))
                    {
                        if (totalChars + cue.Text.Length > 12_000) return result;
                        result.Add(cue);
                        totalChars += cue.Text.Length;
                    }
                    if (result.Count >= 40) return result;
                }
            }
            catch { }
        }
        return result.OrderBy(x => x.StartMs).ToArray();
    }

    public static IReadOnlyList<MediaEvidenceData> Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 2_000_000) return Array.Empty<MediaEvidenceData>();
        var lines = text.Replace("\r", string.Empty).Split('\n');
        var result = new List<MediaEvidenceData>();
        for (var i = 0; i < lines.Length && result.Count < 80; i++)
        {
            var match = TimeLine.Match(lines[i]);
            if (!match.Success || !TryTime(match.Groups["start"].Value, out var start) ||
                !TryTime(match.Groups["end"].Value, out var end) || end < start) continue;
            var words = new List<string>();
            for (i++; i < lines.Length && lines[i].Trim().Length > 0; i++)
            {
                var clean = WebUtility.HtmlDecode(Tags.Replace(lines[i], string.Empty)).Trim();
                if (clean.Length > 0 && !words.Contains(clean, StringComparer.Ordinal)) words.Add(clean);
            }
            var value = string.Join(" ", words);
            value = new string(value.Where(x => x is '\t' || !char.IsControl(x)).ToArray()).Trim();
            if (value.Length == 0) continue;
            if (value.Length > 500) value = value[..500];
            result.Add(new MediaEvidenceData
            {
                Kind = MediaEvidenceKinds.Subtitle, Text = value, StartMs = start, EndMs = end,
                EvidenceId = "subtitle-" + result.Count.ToString(CultureInfo.InvariantCulture) + "-" + start
            });
        }
        return result;
    }

    private static bool TryTime(string value, out long milliseconds)
    {
        milliseconds = 0;
        var parts = value.Replace(',', '.').Split(':');
        if (parts.Length is < 2 or > 3) return false;
        if (!double.TryParse(parts[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ||
            !int.TryParse(parts[^2], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)) return false;
        var hours = 0;
        if (parts.Length == 3 && !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out hours)) return false;
        if (hours < 0 || minutes is < 0 or > 59 || seconds is < 0 or >= 60) return false;
        milliseconds = checked((long)Math.Round(((hours * 60L + minutes) * 60 + seconds) * 1000));
        return true;
    }
}
