using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Logic;
using TraceSoul2.Util;

namespace TraceSoul2.Plugins.Builtin
{
    /// <summary>
    /// OneBot v11 / NapCat 平台适配器：
    /// 入站把 QQ 的数组段消息（text/image/face/at/record/video/file/reply…）翻译成规范 Moment；
    /// 出站把规范表达翻译成 send_group_msg / send_private_msg，并回传规范「已发送」事件（保证入库）。
    /// 平台的消息格式差异全部收在适配器里，插件只负责连接与收发。
    /// </summary>
    public sealed class OneBotPlatformAdapter : ITracePlatformAdapter
    {
        private readonly OneBotPlatformPlugin owner;

        public OneBotPlatformAdapter(OneBotPlatformPlugin owner)
        {
            this.owner = owner ?? throw new ArgumentNullException("owner");
        }

        public string PlatformId { get { return "builtin.onebot"; } }

        // ---------- 入站：QQ 消息 → 规范 Moment ----------

        public PluginEventData ConvertInbound(string platformPayload)
        {
            if (string.IsNullOrWhiteSpace(platformPayload)) return null;
            if (JsonText.ExtractString(platformPayload, "post_type") != "message") return null;

            var selfId = JsonText.ExtractLong(platformPayload, "self_id");
            var userId = JsonText.ExtractLong(platformPayload, "user_id");
            // 平台层过滤：只收配置的 self_id；自己发的消息回显不进脑。
            var config = owner.Config;
            if (!string.IsNullOrWhiteSpace(config.self_id) && selfId > 0 &&
                !string.Equals(selfId.ToString(), config.self_id.Trim(), StringComparison.Ordinal)) return null;
            if (selfId > 0 && userId == selfId) return null;

            var messageType = JsonText.ExtractString(platformPayload, "message_type");
            var sessionType = messageType == "group" ? "group" : "private";
            var groupId = JsonText.ExtractLong(platformPayload, "group_id");
            var sessionId = sessionType == "group" ? groupId.ToString() : userId.ToString();
            if (string.IsNullOrWhiteSpace(sessionId) || sessionId == "0") return null;

            var nickname = JsonText.ExtractString(platformPayload, "nickname");
            if (string.IsNullOrWhiteSpace(nickname)) nickname = JsonText.ExtractString(platformPayload, "card");

            var content = BuildMessageText(platformPayload);
            var prefix = sessionType == "group"
                ? "[QQ·群" + sessionId + "] "
                : "[QQ·私聊" + (string.IsNullOrWhiteSpace(nickname) ? string.Empty : " " + nickname) + "] ";

            return new PluginEventData
            {
                PluginId = PlatformId,
                ExternalEventId = JsonText.ExtractLong(platformPayload, "message_id").ToString(),
                Role = "user",
                Content = prefix + content,
                Realm = TraceRealmValues.SharedScene,
                EvidenceType = EvidenceTypeValues.DialogueExplicit,
                Organ = MouthLogic.ClassifyInboundOrgan(content),
                PayloadJson = TraceJson.ToJson(new OneBotSessionPayload
                {
                    session_type = sessionType,
                    session_id = sessionId,
                    nickname = nickname,
                    image_urls = ExtractImageLocations(platformPayload)
                }),
                Breaking = true,
                OccurredUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
        }

        /// <summary>
        /// 保留消息段中的原图位置，供相机/视觉插件做改图和看图。文本仍只显示 [图片]，
        /// 原始 URL 或本地路径只进入本轮结构化载荷，不混进对话正文。
        /// </summary>
        private static List<string> ExtractImageLocations(string json)
        {
            var result = new List<string>();
            try
            {
                using (var document = JsonDocument.Parse(json))
                {
                    if (document.RootElement.TryGetProperty("message", out var message) &&
                        message.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var segment in message.EnumerateArray())
                        {
                            if (segment.ValueKind != JsonValueKind.Object ||
                                !segment.TryGetProperty("type", out var type) ||
                                !string.Equals(type.GetString(), "image", StringComparison.OrdinalIgnoreCase) ||
                                !segment.TryGetProperty("data", out var data) ||
                                data.ValueKind != JsonValueKind.Object) continue;
                            foreach (var key in new[] { "url", "file", "path" })
                            {
                                if (!data.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String)
                                    continue;
                                var location = (value.GetString() ?? string.Empty).Trim();
                                if (location.Length > 0 && !result.Contains(location, StringComparer.OrdinalIgnoreCase))
                                    result.Add(location);
                            }
                        }
                    }
                    else if (document.RootElement.TryGetProperty("raw_message", out var raw) &&
                             raw.ValueKind == JsonValueKind.String)
                    {
                        foreach (Match match in Regex.Matches(raw.GetString() ?? string.Empty,
                                     @"\[CQ:image,[^\]]*(?:url|file)=([^,\]]+)", RegexOptions.IgnoreCase))
                        {
                            var location = Uri.UnescapeDataString(match.Groups[1].Value).Trim();
                            if (location.Length > 0 && !result.Contains(location, StringComparer.OrdinalIgnoreCase))
                                result.Add(location);
                        }
                    }
                }
            }
            catch { /* 入站载荷异常时不影响正常文字消息。 */ }
            return result;
        }

        /// <summary>
        /// OneBot v11 消息文本翻译：message 是数组段（NapCat messagePostFormat=array）时逐段翻译，
        /// 每种消息段各有自己的占位写法；数组里没有文本时退回 raw_message（string 格式）。
        /// </summary>
        private static string BuildMessageText(string json)
        {
            if (json.IndexOf("\"type\"", StringComparison.Ordinal) < 0)
            {
                var raw = JsonText.ExtractString(json, "raw_message");
                if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
                return raw.Replace("[CQ:image", "[图片][CQ:image").Replace("[CQ:face", "[表情][CQ:face");
            }
            var builder = new StringBuilder();
            var cursor = 0;
            var guard = 0;
            while (guard++ < 512)
            {
                var typeStart = json.IndexOf("\"type\":", cursor, StringComparison.Ordinal);
                if (typeStart < 0) break;
                cursor = typeStart + 7;
                while (cursor < json.Length && (json[cursor] == ' ' || json[cursor] == '"')) cursor++;
                var nameStart = cursor;
                while (cursor < json.Length && json[cursor] != '"') cursor++;
                if (cursor >= json.Length) break;
                var typeName = json.Substring(nameStart, cursor - nameStart);
                cursor++;
                builder.Append(TranslateSegment(json, typeName, cursor));
            }
            return builder.ToString();
        }

        /// <summary>单段翻译：QQ 每种消息段 → 我们文字结构里的占位写法。</summary>
        private static string TranslateSegment(string json, string typeName, int afterType)
        {
            switch (typeName)
            {
                case "text":
                    var textKey = json.IndexOf("\"text\":", afterType, StringComparison.Ordinal);
                    var nextType = json.IndexOf("\"type\":", afterType, StringComparison.Ordinal);
                    if (textKey >= 0 && (nextType < 0 || textKey < nextType))
                    {
                        var valueStart = textKey + 7;
                        while (valueStart < json.Length && (json[valueStart] == ' ' || json[valueStart] == '"')) valueStart++;
                        var valueEnd = JsonText.FindStringEnd(json, valueStart);
                        if (valueEnd > valueStart)
                            return JsonText.Unescape(json.Substring(valueStart, valueEnd - valueStart));
                    }
                    return string.Empty;
                case "image": return "[图片]";
                case "face": return "[表情]";
                case "at": return "[@]";
                case "record": return "[语音]";
                case "video": return "[视频]";
                case "file": return "[文件]";
                case "reply": return "[回复]";
                case "forward": return "[合并转发]";
                case "markdown": return "[卡片消息]";
                default: return string.Empty;
            }
        }

        // ---------- 平台通用动作（供感官插件调平台特有接口，如 get_cookies） ----------

        public Task<string> CallActionAsync(
            string action,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return owner.CallActionAsync(action, parameters);
        }

        // ---------- 出站：规范表达 → QQ 动作 + 规范已发送事件 ----------

        public async Task<TraceCapabilityResultData> SendAsync(
            TraceOutboundMessageData message,
            TraceTurnContext context,
            CancellationToken cancellationToken)
        {
            if (message == null) throw new ArgumentNullException("message");
            var timer = Stopwatch.StartNew();
            context?.Services?.LogTiming(context.TraceId, "QQ 出站适配开始",
                detail: "kind=" + (message.Kind ?? string.Empty));
            var sessionType = message.SessionType;
            var sessionId = message.SessionId;
            if (string.IsNullOrWhiteSpace(sessionId))
                owner.TryResolveSession(context, out sessionType, out sessionId);
            if (string.IsNullOrWhiteSpace(sessionId))
                throw new InvalidOperationException("还没有记住 QQ 会话。先从 QQ 发一条过来，之后心跳也会发回那里。");

            string payload = null;
            string canonicalContent;
            string summary;
            var deferred = false;
            switch (message.Kind)
            {
                case TraceOutboundKinds.Text:
                    var text = (message.Text ?? string.Empty).Trim();
                    if (text.Length == 0) throw new InvalidOperationException("QQ 文字表达需要 text。");
                    payload = text;
                    canonicalContent = text;
                    summary = "QQ 文字已暂存（本轮结束时与结尾表情合并成一条发出）。";
                    owner.StageText(text, sessionType, sessionId);
                    deferred = true;
                    break;
                case TraceOutboundKinds.Image:
                    var file = (message.File ?? string.Empty).Trim();
                    if (file.Length == 0) throw new InvalidOperationException("QQ 图片表达需要 file。");
                    payload = "[CQ:image,file=" + file.Replace(",", "%2C") + "]";
                    // 自定义表情库里的 PNG/GIF 通过平台协议仍是 image 段，语义上却是 sticker。
                    // 文字已经暂存时，把它接到同一条消息结尾；普通图片和生图仍单独发送。
                    if (IsStickerAsset(file) && owner.TryAppendSegment(payload))
                    {
                        canonicalContent = OneBotPlatformPrompts.SendStickerMoment;
                        summary = "已把图片表情追加到 QQ 文字消息结尾。";
                        deferred = true;
                        break;
                    }
                    canonicalContent = OneBotPlatformPrompts.SendImageMoment;
                    summary = "已通过 QQ 发送图片。";
                    break;
                case TraceOutboundKinds.Sticker:
                    var faceId = (message.File ?? string.Empty).Trim();
                    if (faceId.Length == 0) throw new InvalidOperationException("QQ 表情需要 face id。");
                    payload = "[CQ:face,id=" + faceId + "]";
                    if (owner.TryAppendSegment(payload))
                    {
                        canonicalContent = OneBotPlatformPrompts.SendStickerMoment;
                        summary = "已把表情追加到 QQ 文字消息结尾。";
                        deferred = true;
                        break;
                    }
                    canonicalContent = "[QQ 表情：face#" + faceId + "]";
                    summary = "已通过 QQ 发送表情（face#" + faceId + "）。";
                    break;
                case TraceOutboundKinds.Voice:
                    var voiceFile = (message.File ?? string.Empty).Trim();
                    if (voiceFile.Length == 0) throw new InvalidOperationException("QQ 语音需要音频文件路径。");
                    payload = "[CQ:record,file=" + voiceFile.Replace(",", "%2C") + "]";
                    canonicalContent = OneBotPlatformPrompts.SendVoiceMoment;
                    summary = "已通过 QQ 发送语音。";
                    break;
                default:
                    throw new InvalidOperationException("OneBot 适配器暂不支持这种表达：" + message.Kind);
            }

            if (!deferred)
            {
                await owner.CallActionAsync(sessionType == "group" ? "send_group_msg" : "send_private_msg",
                    new Dictionary<string, object>
                    {
                        { sessionType == "group" ? "group_id" : "user_id", long.Parse(sessionId) },
                        { "message", payload }
                    });
            }

            context?.Services?.LogTiming(context.TraceId,
                deferred ? "QQ 出站已暂存" : "QQ 出站动作完成",
                timer.ElapsedMilliseconds, "kind=" + message.Kind);

            // 规范「已发送」事件：平台适配器契约，中枢据此把回复 Moment 完整入库。
            var pair = context.Services.Storage.LoadPairIdentity();
            var canonical = new PluginEventData
            {
                PluginId = PlatformId,
                ExternalEventId = Guid.NewGuid().ToString("N"),
                Role = pair.IsComplete ? pair.Assname : "assistant",
                Content = canonicalContent,
                Realm = TraceRealmValues.Unclassified,
                EvidenceType = EvidenceTypeValues.AssPerformed,
                PayloadJson = string.Empty,
                OccurredUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            return new TraceCapabilityResultData
            {
                Status = "success",
                Summary = summary,
                Payload = canonicalContent,
                ProducedEvent = canonical,
                EvidenceRefs = new List<string>()
            };
        }

        internal static bool IsStickerAsset(string file)
        {
            var path = (file ?? string.Empty).Trim().Replace('\\', '/');
            return path.IndexOf("/qq-sticker/", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    /// <summary>平台适配器用的轻量 JSON 文本工具（无第三方依赖，Unity 可用）。</summary>
    public static class JsonText
    {
        public static string ExtractString(string json, string key)
        {
            var index = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (index < 0) return string.Empty;
            var valueStart = json.IndexOf(':', index) + 1;
            while (valueStart < json.Length && (json[valueStart] == ' ' || json[valueStart] == '"')) valueStart++;
            if (valueStart >= json.Length || json[valueStart - 1] != '"') return string.Empty;
            var end = FindStringEnd(json, valueStart);
            if (end < 0) return string.Empty;
            return json.Substring(valueStart, end - valueStart);
        }

        public static long ExtractLong(string json, string key)
        {
            var index = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (index < 0) return 0;
            var cursor = json.IndexOf(':', index);
            if (cursor < 0) return 0;
            cursor++;
            while (cursor < json.Length && (json[cursor] == ' ' || json[cursor] == '"')) cursor++;
            var start = cursor;
            while (cursor < json.Length && (char.IsDigit(json[cursor]) || json[cursor] == '-')) cursor++;
            if (cursor <= start) return 0;
            long value;
            return long.TryParse(json.Substring(start, cursor - start), out value) ? value : 0;
        }

        public static int FindStringEnd(string json, int start)
        {
            var i = start;
            while (i < json.Length)
            {
                if (json[i] == '\\') { i += 2; continue; }
                if (json[i] == '"') return i;
                i++;
            }
            return -1;
        }

        public static string Unescape(string value)
        {
            if (value.IndexOf('\\') < 0) return value;
            var builder = new StringBuilder(value.Length);
            for (var i = 0; i < value.Length; i++)
            {
                if (value[i] != '\\' || i + 1 >= value.Length) { builder.Append(value[i]); continue; }
                var next = value[++i];
                switch (next)
                {
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case '/': builder.Append('/'); break;
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'u':
                        if (i + 4 < value.Length)
                        {
                            int code;
                            if (int.TryParse(value.Substring(i + 1, 4), NumberStyles.HexNumber,
                                    CultureInfo.InvariantCulture, out code))
                            {
                                builder.Append((char)code);
                                i += 4;
                                break;
                            }
                        }
                        builder.Append('u');
                        break;
                    default: builder.Append(next); break;
                }
            }
            return builder.ToString();
        }
    }
}
