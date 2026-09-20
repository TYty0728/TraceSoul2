using System.Collections.Generic;

namespace TraceSoul2.Data
{
    /// <summary>来源识别不代表已获取媒体，更不代表已看过内容。</summary>
    public sealed class MediaSourceData
    {
        public string Platform { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public string CanonicalUrl { get; set; } = string.Empty;
        public string MediaId { get; set; } = string.Empty;
        public int Part { get; set; } = 1;
        public bool RequiresResolution { get; set; }
    }

    public static class MediaEvidenceKinds
    {
        public const string PageMetadata = "page_metadata";
        public const string Cover = "cover";
        public const string Image = "image";
        public const string VideoFrame = "video_frame";
        public const string Transcript = "transcript";
        public const string Subtitle = "subtitle";
    }

    /// <summary>时间相对于媒体/会话起点。未知用 null，不能用 0 冒充。</summary>
    public sealed class MediaEvidenceData
    {
        public string Kind { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public long? StartMs { get; set; }
        public long? EndMs { get; set; }
        public string EvidenceId { get; set; } = string.Empty;
    }

    public sealed class MediaUnderstandingResultData
    {
        public string Status { get; set; } = "not_analyzed";
        public MediaSourceData Source { get; set; }
        public string Summary { get; set; } = string.Empty;
        public List<MediaEvidenceData> Evidence { get; set; } = new List<MediaEvidenceData>();
        public List<string> Limitations { get; set; } = new List<string>();
    }

    public sealed class RealtimeSessionData
    {
        public string SessionId { get; set; } = string.Empty;
        public string ConversationId { get; set; } = string.Empty;
        public string Transport { get; set; } = string.Empty;
        public string State { get; set; } = "connected";
        /// <summary>每次打断递增；旧代次的模型输出/音频必须丢弃。</summary>
        public long Generation { get; set; }
        public long StartedUnixMs { get; set; }
        public long LastActivityUnixMs { get; set; }
        public string EndReason { get; set; } = string.Empty;
    }

    /// <summary>生成不等于播放。只记录客户端确认的实际进度，不推算未听到的文本。</summary>
    public sealed class RealtimePlaybackReceiptData
    {
        public string SessionId { get; set; } = string.Empty;
        public string OutputId { get; set; } = string.Empty;
        public long Generation { get; set; }
        public long DurationMs { get; set; }
        public long PlayedMs { get; set; }
        public bool Completed { get; set; }
        public bool Interrupted { get; set; }
    }
}
