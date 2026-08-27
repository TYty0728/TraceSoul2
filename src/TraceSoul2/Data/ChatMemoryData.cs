using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using SQLite;

namespace TraceSoul2.Data
{
    public static class OperationalEventKindValues
    {
        public const string SchedulerTrigger = "scheduler_trigger";
        public const string OutboundImage = "outbound_image";
        public const string OutboundSticker = "outbound_sticker";
        public const string OutboundVoice = "outbound_voice";
        public const string ActionReceipt = "action_receipt";
        public const string PluginRuntime = "plugin_runtime";
    }

    [Table("moments")]
    public sealed class MomentRecord
    {
        [PrimaryKey]
        public string Id { get; set; }
        [Indexed]
        public string ConversationId { get; set; }
        public string Role { get; set; }
        public string Content { get; set; }
        public string Realm { get; set; }
        public string EvidenceType { get; set; }
        [Indexed]
        public string SourcePluginId { get; set; }
        public string SourceEventId { get; set; }
        public string PayloadJson { get; set; }

        /// <summary>记忆落库标记：live=已保存未构筑；built=已归档进多维索引/条目。</summary>
        public string MemoryStatus { get; set; }
        [Indexed]
        public long CreatedUnixMs { get; set; }
    }

    /// <summary>
    /// 运行层留痕，不是可供记忆构筑的生命事件。
    /// 例如平台发送回执、调度器触发、插件执行结果等都写这里。
    /// </summary>
    [Table("operational_events")]
    public sealed class OperationalEventRecord
    {
        [PrimaryKey]
        public string Id { get; set; }
        [Indexed]
        public string ConversationId { get; set; }
        [Indexed]
        public string Kind { get; set; }
        [Indexed]
        public string SourcePluginId { get; set; }
        [Indexed]
        public string SourceEventId { get; set; }
        public string TraceId { get; set; }
        public string Role { get; set; }
        public string Content { get; set; }
        public string Realm { get; set; }
        public string EvidenceType { get; set; }
        public string PayloadJson { get; set; }
        public long OccurredUnixMs { get; set; }
        public long CreatedUnixMs { get; set; }
    }

    /// <summary>只保存 Brain 中枢的公开运行摘要，不保存领域插件私有结构。</summary>
    [Table("turn_reviews")]
    public sealed class TurnReviewRecord
    {
        [PrimaryKey]
        public string Id { get; set; }
        [Indexed]
        public string ConversationId { get; set; }
        [Indexed]
        public string TriggerMomentId { get; set; }
        public string BrainMode { get; set; }
        public string BrainIntent { get; set; }
        public string DecisionSummary { get; set; }
        public string CapabilitySummary { get; set; }
        public string FacetSummary { get; set; }

        /// <summary>整轮链路快照（挂载块 + 回调结果含召回证据），便于重启后仍可回看。</summary>
        public string PayloadJson { get; set; }
        public long CreatedUnixMs { get; set; }
    }

    /// <summary>
    /// 今天我们的轨迹：当天两人共同经历的滚动实时样本（约500字内），
    /// 实时对话中由 Brain 高频维护；对应日复盘成功后才退出。按记忆日键一行。
    /// </summary>
    [Table("day_trajectory")]
    public sealed class DayTrajectoryRecord
    {
        [PrimaryKey]
        public string DayKey { get; set; }
        public string Text { get; set; }
        public long UpdatedUnixMs { get; set; }
    }

    /// <summary>
    /// 今日新识：实时对话中「今天刚知道的」最小便签（每条一句话、带证据），
    /// 当天每轮注入 Brain 上下文；日复盘（04:00 边界）再把它加工成正式记忆。
    /// </summary>
    [Table("today_new_items")]
    public sealed class TodayNewItemRecord
    {
        public const int MaxContentChars = 80;

        [PrimaryKey]
        public string Id { get; set; }

        [Indexed]
        public string ConversationId { get; set; }

        public string Content { get; set; }

        [Indexed]
        public string SourceMomentId { get; set; }

        /// <summary>所属记忆日键（yyyy-MM-dd，04:00 前归前一天）。</summary>
        [Indexed]
        public string DayKey { get; set; }

        public long CreatedUnixMs { get; set; }
    }

    public sealed class ChatTurnResultData
    {
        public string Reply { get; private set; }
        public string BrainMode { get; private set; }
        public string BrainIntent { get; private set; }
        public string DecisionSummary { get; private set; }
        /// <summary>本轮心智组织卡；只供 Host/WebUI 观察，不参与表达协议。</summary>
        public MindDecisionData MindDecision { get; private set; }
        public IReadOnlyList<TraceContextBlockData> ContextBlocks { get; private set; }
        public IReadOnlyList<BrainFacetOutputData> FacetOutputs { get; private set; }
        public IReadOnlyList<TraceCapabilityResultData> ContributionResults { get; private set; }

        public ChatTurnResultData(
            string reply,
            string brainMode,
            string brainIntent,
            string decisionSummary,
            List<TraceContextBlockData> contextBlocks,
            List<BrainFacetOutputData> facetOutputs,
            List<TraceCapabilityResultData> contributionResults,
            MindDecisionData mindDecision = null)
        {
            Reply = reply ?? string.Empty;
            BrainMode = brainMode ?? string.Empty;
            BrainIntent = brainIntent ?? string.Empty;
            DecisionSummary = decisionSummary ?? string.Empty;
            MindDecision = mindDecision;
            ContextBlocks = contextBlocks ?? new List<TraceContextBlockData>();
            FacetOutputs = facetOutputs ?? new List<BrainFacetOutputData>();
            ContributionResults = contributionResults ?? new List<TraceCapabilityResultData>();
        }
    }

    [Serializable]
    public sealed class LlmImagePartData
    {
        public string url;
        public string mime;
        public byte[] bytes;

        public bool HasPixels()
        {
            return (bytes != null && bytes.Length > 0) ||
                   (!string.IsNullOrWhiteSpace(url) &&
                    (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
                     url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                     url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)));
        }

        public string ToDataUri()
        {
            if (!string.IsNullOrWhiteSpace(url) &&
                url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return url;
            if (bytes != null && bytes.Length > 0)
            {
                var type = string.IsNullOrWhiteSpace(mime) ? GuessMime(bytes) : mime.Trim();
                return "data:" + type + ";base64," + Convert.ToBase64String(bytes);
            }
            return (url ?? string.Empty).Trim();
        }

        public byte[] ResolveBytes()
        {
            if (bytes != null && bytes.Length > 0) return bytes;
            var data = (url ?? string.Empty).Trim();
            var marker = ";base64,";
            var at = data.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return null;
            try { return Convert.FromBase64String(data.Substring(at + marker.Length).Trim()); }
            catch { return null; }
        }

        public string ResolveMime()
        {
            if (!string.IsNullOrWhiteSpace(mime)) return mime.Trim();
            var data = (url ?? string.Empty).Trim();
            if (data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var end = data.IndexOf(';', 5);
                if (end > 5) return data.Substring(5, end - 5);
            }
            return GuessMime(ResolveBytes());
        }

        public static string GuessMime(byte[] data)
        {
            if (data == null || data.Length < 4) return "image/jpeg";
            if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47) return "image/png";
            if (data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46) return "image/gif";
            if (data.Length >= 12 && data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46 &&
                data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50)
                return "image/webp";
            return "image/jpeg";
        }
    }

    /// <summary>
    /// 有图时把 content 写成 OpenAI 兼容的 text + image_url 数组；没图时仍是字符串。
    /// dump 不要打印 images 原始字节。
    /// </summary>
    public sealed class DeepSeekMessageJsonConverter : JsonConverter<DeepSeekMessageData>
    {
        public override DeepSeekMessageData Read(
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using (var document = JsonDocument.ParseValue(ref reader))
            {
                var root = document.RootElement;
                var message = new DeepSeekMessageData();
                if (root.TryGetProperty("role", out var role))
                    message.role = role.GetString();
                if (root.TryGetProperty("reasoning_content", out var reasoning) &&
                    reasoning.ValueKind == JsonValueKind.String)
                    message.reasoning_content = reasoning.GetString();
                if (!root.TryGetProperty("content", out var content)) return message;
                if (content.ValueKind == JsonValueKind.String)
                {
                    message.content = content.GetString();
                    return message;
                }
                if (content.ValueKind != JsonValueKind.Array) return message;
                var texts = new List<string>();
                var images = new List<LlmImagePartData>();
                foreach (var part in content.EnumerateArray())
                {
                    if (part.ValueKind != JsonValueKind.Object) continue;
                    if (part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    {
                        var value = text.GetString();
                        if (!string.IsNullOrWhiteSpace(value)) texts.Add(value);
                        continue;
                    }
                    if (part.TryGetProperty("image_url", out var imageUrl))
                    {
                        var url = imageUrl.ValueKind == JsonValueKind.String
                            ? imageUrl.GetString()
                            : (imageUrl.ValueKind == JsonValueKind.Object &&
                               imageUrl.TryGetProperty("url", out var nested) &&
                               nested.ValueKind == JsonValueKind.String
                                ? nested.GetString()
                                : null);
                        if (!string.IsNullOrWhiteSpace(url))
                            images.Add(new LlmImagePartData { url = url });
                    }
                }
                message.content = string.Join("\n", texts);
                if (images.Count > 0) message.images = images;
                return message;
            }
        }

        public override void Write(
            Utf8JsonWriter writer, DeepSeekMessageData value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("role", value == null ? string.Empty : (value.role ?? string.Empty));
            var images = value == null ? null : value.images;
            if (images != null && images.Count > 0)
            {
                writer.WritePropertyName("content");
                writer.WriteStartArray();
                var text = value.content ?? string.Empty;
                if (text.Trim().Length == 0) text = "（附图）";
                writer.WriteStartObject();
                writer.WriteString("type", "text");
                writer.WriteString("text", text);
                writer.WriteEndObject();
                foreach (var image in images)
                {
                    if (image == null || !image.HasPixels()) continue;
                    writer.WriteStartObject();
                    writer.WriteString("type", "image_url");
                    writer.WritePropertyName("image_url");
                    writer.WriteStartObject();
                    writer.WriteString("url", image.ToDataUri());
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            else
            {
                writer.WriteString("content", value == null ? string.Empty : (value.content ?? string.Empty));
            }
            if (value != null && !string.IsNullOrWhiteSpace(value.reasoning_content))
                writer.WriteString("reasoning_content", value.reasoning_content);
            writer.WriteEndObject();
        }
    }

    [Serializable]
    [JsonConverter(typeof(DeepSeekMessageJsonConverter))]
    public sealed class DeepSeekMessageData
    {
        public string role;
        public string content;
        /// <summary>Kimi K3 多轮需原样回传；DeepSeek 无工具时可省略。</summary>
        public string reasoning_content;
        /// <summary>入站识图用的附图。只在请求装配时展开成 image_url，不进对话正文。</summary>
        [JsonIgnore]
        public List<LlmImagePartData> images;
        public DeepSeekMessageData() { }
        public DeepSeekMessageData(string role, string content)
        {
            this.role = role;
            this.content = content;
        }

        public bool HasImages()
        {
            if (images == null || images.Count == 0) return false;
            foreach (var image in images)
                if (image != null && image.HasPixels()) return true;
            return false;
        }
    }

    [Serializable]
    public sealed class DeepSeekResponseMessageData
    {
        public string role;
        public string content;
        public string reasoning_content;
    }

    [Serializable]
    public sealed class DeepSeekResponseFormatData
    {
        public string type = "json_object";
    }

    [Serializable]
    public sealed class DeepSeekThinkingData
    {
        public string type = "disabled";
    }

    [Serializable]
    public sealed class DeepSeekChatRequestData
    {
        public string model;
        public List<DeepSeekMessageData> messages;
        public DeepSeekResponseFormatData response_format;
        public DeepSeekThinkingData thinking;
        public string reasoning_effort;
        public float temperature;
        public float top_p;
        public int max_tokens;
    }

    /// <summary>
    /// 非 DeepSeek 的 OpenAI 兼容口：不带 thinking / reasoning_effort，避免 Gemini 等中转站拒收。
    /// response_format 仍要保留：心智是 JSON 口，缺 json_object 时 K3 会把身份卡当成对白直接说完。
    /// </summary>
    [Serializable]
    public sealed class OpenAiChatRequestData
    {
        public string model;
        public List<DeepSeekMessageData> messages;
        public DeepSeekResponseFormatData response_format;
        public float temperature;
        public float top_p;
        public int max_tokens;
    }

    /// <summary>
    /// Kimi 开放平台（api.moonshot.cn / api.moonshot.ai）。
    /// K3 用顶层 reasoning_effort；K2.x 用 thinking；temperature / top_p 为固定值，不要显式传入。
    /// </summary>
    [Serializable]
    public sealed class KimiChatRequestData
    {
        public string model;
        public List<DeepSeekMessageData> messages;
        public DeepSeekResponseFormatData response_format;
        public DeepSeekThinkingData thinking;
        public string reasoning_effort;
        public int? max_completion_tokens;
        public string prompt_cache_key;
    }

    /// <summary>
    /// GLM 的 OpenAI 兼容请求仍需要显式 thinking；省略时 GLM-5.x 默认开启深度思考。
    /// 与普通兼容口分开，避免向不认识该字段的供应商发送扩展参数。
    /// </summary>
    [Serializable]
    public sealed class GlmChatRequestData
    {
        public string model;
        public List<DeepSeekMessageData> messages;
        public DeepSeekThinkingData thinking;
        public string reasoning_effort;
        public float temperature;
        public float top_p;
        public int max_tokens;
    }

    [Serializable]
    public sealed class DeepSeekChoiceData
    {
        public DeepSeekResponseMessageData message;
        public string finish_reason;
    }

    [Serializable]
    public sealed class DeepSeekErrorData
    {
        public string message;
        public string type;
    }

    [Serializable]
    public sealed class DeepSeekUsageData
    {
        public int prompt_tokens;
        public int completion_tokens;
        public int total_tokens;
        public int prompt_cache_hit_tokens;
        public int prompt_cache_miss_tokens;
    }

    [Serializable]
    public sealed class DeepSeekChatResponseData
    {
        public List<DeepSeekChoiceData> choices;
        public DeepSeekErrorData error;
        public DeepSeekUsageData usage;
    }

    [Serializable]
    public sealed class OpenAiModelListData
    {
        public List<OpenAiModelItemData> data = new List<OpenAiModelItemData>();
    }

    [Serializable]
    public sealed class OpenAiModelItemData
    {
        public string id;
    }

    /// <summary>一轮链路的持久化快照（写入 turn_reviews.PayloadJson，重启后可回看）。</summary>
    [Serializable]
    public sealed class TurnPayloadSnapshotData
    {
        public MindDecisionData mind_decision;
        public List<TurnBlockSnapshotData> blocks = new List<TurnBlockSnapshotData>();
        public List<TurnResultSnapshotData> results = new List<TurnResultSnapshotData>();
    }

    [Serializable]
    public sealed class TurnBlockSnapshotData
    {
        public string facet_id;
        public string title;
        public string content;
    }

    [Serializable]
    public sealed class TurnResultSnapshotData
    {
        public string capability_id;
        public string status;
        public string summary;
        public string payload;
    }

    public sealed class DeepSeekConfigData
    {
        public string ProviderId { get; set; } = "default";
        public string Type { get; set; } = "openai_chat_completion";
        public string ApiKey { get; set; }
        public string BaseUrl { get; set; } = "https://api.deepseek.com";
        public string Model { get; set; } = "deepseek-v4-flash";
        public float Temperature { get; set; } = 0.3f;
        public float TopP { get; set; } = 1f;
        public int MaxTokens { get; set; } = 8192;
        public int TimeoutSeconds { get; set; } = 120;
        public bool ThinkingEnabled { get; set; }
        public string ReasoningEffort { get; set; } = "high";
        public int EmptyContentRetries { get; set; } = 1;
        /// <summary>429、5xx、超时等瞬时故障的额外退避重试次数；0 表示关闭。</summary>
        public int TransientErrorRetries { get; set; }
    }
}
