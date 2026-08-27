using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Plugins;

namespace TraceSoul2.ExternalPlugins
{
    /// <summary>
    /// QQ 空间说说器官：发布一条说说，或读取最近说说。
    /// Cookie 不手动配置——向 NapCat 要 get_cookies（多域名，含 get_credentials），
    /// p_skey 计算 g_tk。发布接口常假失败，只发一次绝不重试。
    /// </summary>
    public sealed class QqQzonePlugin : ITracePlugin
    {
        private const string PluginId = "qq.qzone";
        private static readonly Regex JsonpPrefix = new Regex(
            @"^[a-zA-Z_]\w*\(", RegexOptions.Compiled);
        private string herUin = string.Empty;
        private int publishDailyCap = 1;
        private int readDailyCap = 2;

        public TracePluginMetadataData Metadata { get; } = new TracePluginMetadataData
        {
            Id = PluginId,
            DisplayName = "QQ 空间说说",
            Version = "1.3.0",
            Author = "TraceSoul2",
            Role = PluginRoleValues.Organ,
            PlatformId = BodyIds.Qq,
            Description = "QQ 空间感官：发一条说说，或读取她/自己最近的说说。"
        };

        public void Register(TracePluginContext context)
        {
            LoadConfig(context.PackageDirectory, context.PluginDataDirectory);
            context.AddMountedFacet(new UsageFacet());
            context.AddCallable(new QzonePublishEffector(this));
            context.AddCallable(new QzoneReadNerve(this));
        }

        public void Shutdown() { }

        private void LoadConfig(string packageDirectory, string pluginDataDirectory)
        {
            ApplyConfig(Path.Combine(packageDirectory ?? string.Empty, "plugin.json"));
            ApplyConfig(Path.Combine(pluginDataDirectory ?? string.Empty, "config.json"));
        }

        private void ApplyConfig(string path)
        {
            if (!File.Exists(path)) return;
            using (var doc = JsonDocument.Parse(File.ReadAllText(path)))
            {
                var value = ReadString(doc.RootElement, "her_uin");
                if (!string.IsNullOrWhiteSpace(value)) herUin = value.Trim();
                publishDailyCap = ReadCap(doc.RootElement, "publish_daily_cap", publishDailyCap);
                readDailyCap = ReadCap(doc.RootElement, "read_daily_cap", readDailyCap);
            }
        }

        internal async Task<TraceCapabilityResultData> PublishFromCallAsync(
            BrainCapabilityCallData call, TraceTurnContext context, CancellationToken cancellationToken)
        {
            var idle = IsIdleCall(call);
            var content = call == null ? string.Empty : call.GetArgument("content");
            if (string.IsNullOrWhiteSpace(content) && call != null) content = call.GetArgument("text");
            if (string.IsNullOrWhiteSpace(content) && idle)
                content = await ComposePublishAsync(call == null ? string.Empty : call.GetArgument("seed"),
                    context, cancellationToken);
            if (LooksLikeNone(content))
            {
                return new TraceCapabilityResultData
                {
                    Status = "skipped",
                    Summary = idle ? "空闲抽到发说说，没有想发的。" : "说说正文是空的。",
                    Payload = string.Empty,
                    EvidenceRefs = new List<string>()
                };
            }
            return await PublishAsync(content, context, cancellationToken);
        }

        internal async Task<TraceCapabilityResultData> PublishAsync(
            string content, TraceTurnContext context, CancellationToken cancellationToken)
        {
            content = (content ?? string.Empty).Trim();
            if (content.Length == 0) throw new InvalidOperationException("QQ 说说需要 content（全文）。");
            var session = await LoginAsync(context, cancellationToken);

            var form = new Dictionary<string, string>
            {
                { "syn_tweet_verson", "1" },
                { "paramstr", "1" },
                { "who", "1" },
                { "con", content },
                { "feedversion", "1" },
                { "ver", "1" },
                { "ugc_right", "1" },
                { "to_sign", "0" },
                { "hostuin", session.Uin.ToString() },
                { "code_version", "1" },
                { "format", "json" },
                { "qzreferrer", "https://user.qzone.qq.com/" + session.Uin }
            };
            var url = "https://h5.qzone.qq.com/proxy/domain/taotao.qzone.qq.com/cgi-bin/emotion_cgi_publish_v6" +
                      "?g_tk=" + session.Gtk + "&uin=" + session.Uin;

            using (var http = NewClient())
            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Content = new FormUrlEncodedContent(form);
                ApplyBrowserHeaders(request, session);
                using (var response = await http.SendAsync(request, cancellationToken))
                {
                    var body = await response.Content.ReadAsStringAsync();
                    long code = long.MinValue;
                    string message = string.Empty;
                    TryReadCode(StripEnvelope(body), out code, out message);
                    // 老插件踩过：接口常返回非 0，但说说其实已经发出。只发一次，假失败也当已投递。
                    return FinishPublish(content, context, code, message, (int)response.StatusCode);
                }
            }
        }

        internal async Task<TraceCapabilityResultData> ReadFromCallAsync(
            BrainCapabilityCallData call, TraceTurnContext context, CancellationToken cancellationToken)
        {
            var result = await ReadAsync(call, context, cancellationToken);
            if (result != null && IsIdleCall(call) && result.ProducedEvent == null)
            {
                result.ProducedEvent = new PluginEventData
                {
                    PluginId = PluginId,
                    ExternalEventId = Guid.NewGuid().ToString("N"),
                    Role = "system_event",
                    Content = result.Summary ?? "看了说说。",
                    Realm = TraceRealmValues.ExternalWorld,
                    EvidenceType = EvidenceTypeValues.PluginObserved,
                    PayloadJson = string.Empty,
                    // 她真的去看了：入 Moment（物理痕迹），不只留运行回执。
                    IsOperational = false,
                    OccurredUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
            }
            return result;
        }

        internal async Task<TraceCapabilityResultData> ReadAsync(
            BrainCapabilityCallData call, TraceTurnContext context, CancellationToken cancellationToken)
        {
            var session = await LoginAsync(context, cancellationToken);
            var idle = IsIdleCall(call);
            var target = ResolveTargetUin(call, context, session.Uin, idle);
            var pos = ReadInt(call.GetArgument("pos"), 0, 0, 50);
            var num = ReadInt(call.GetArgument("num"), 3, 1, 5);

            var url = "https://h5.qzone.qq.com/proxy/domain/taotao.qq.com/cgi-bin/emotion_cgi_msglist_v6" +
                      "?g_tk=" + session.Gtk +
                      "&uin=" + target +
                      "&ftype=0&sort=0&pos=" + pos +
                      "&num=" + num +
                      "&replynum=50&callback=_preloadCallback&code_version=1&format=json" +
                      "&need_comment=1&need_private_comment=1";

            using (var http = NewClient())
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                ApplyBrowserHeaders(request, session);
                using (var response = await http.SendAsync(request, cancellationToken))
                {
                    var body = StripEnvelope(await response.Content.ReadAsStringAsync());
                    var feeds = ParseFeeds(body, num);
                    var text = FormatFeeds(target, feeds);
                    return new TraceCapabilityResultData
                    {
                        Status = "success",
                        Summary = feeds.Count == 0
                            ? "没有读到 QQ " + target + " 的说说。"
                            : "读到 QQ " + target + " 最近 " + feeds.Count + " 条说说。",
                        Payload = text,
                        EvidenceRefs = new List<string>()
                    };
                }
            }
        }

        private static TraceCapabilityResultData FinishPublish(
            string content, TraceTurnContext context, long code, string message, int httpStatus)
        {
            var pair = context.Services.Storage.LoadPairIdentity();
            var ok = code == 0;
            var summary = ok
                ? "已发布 QQ 空间说说。"
                : "说说已投递一次（接口 code=" + code +
                  (string.IsNullOrWhiteSpace(message) ? string.Empty : "，" + message) +
                  "，HTTP " + httpStatus + "）。空间接口常假失败，请到空间确认，不要再发。";
            return new TraceCapabilityResultData
            {
                Status = "success",
                Summary = summary,
                Payload = content,
                ProducedEvent = new PluginEventData
                {
                    PluginId = PluginId,
                    ExternalEventId = Guid.NewGuid().ToString("N"),
                    Role = pair.IsComplete ? pair.Assname : "assistant",
                    Content = "[QQ 空间说说] " + content,
                    Realm = TraceRealmValues.ExternalWorld,
                    EvidenceType = EvidenceTypeValues.AssPerformed,
                    PayloadJson = string.Empty,
                    OccurredUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                },
                EvidenceRefs = new List<string>()
            };
        }

        private async Task<QzoneSession> LoginAsync(TraceTurnContext context, CancellationToken cancellationToken)
        {
            var adapter = context.Services.PlatformAdapters
                .FirstOrDefault(x => x != null && x.PlatformId == "builtin.onebot");
            if (adapter == null) throw new InvalidOperationException("QQ 平台适配器不可用。");

            var cookies = await FetchCookiesAsync(adapter, cancellationToken);
            if (string.IsNullOrWhiteSpace(cookies))
                throw new InvalidOperationException("从 NapCat 获取 QQ 空间 Cookie 失败（get_cookies 无返回）。");

            var parsed = ParseCookies(cookies);
            long uin;
            if (!long.TryParse(parsed.TryGetValue("uin", out var uinRaw) ? uinRaw.TrimStart('o') : "0", out uin) || uin <= 0)
                throw new InvalidOperationException("Cookie 里缺少合法的 uin。");
            var pSkey = parsed.TryGetValue("p_skey", out var p) ? p : string.Empty;
            var skey = parsed.TryGetValue("skey", out var s) ? s : string.Empty;
            if (pSkey.Length == 0 && skey.Length == 0)
                throw new InvalidOperationException("Cookie 里缺少 p_skey/skey（可能未登录或已过期）。");
            return new QzoneSession
            {
                Uin = uin,
                Cookies = cookies,
                Gtk = CalcGtk(pSkey.Length > 0 ? pSkey : skey)
            };
        }

        private long ResolveTargetUin(
            BrainCapabilityCallData call, TraceTurnContext context, long selfUin, bool idle)
        {
            var raw = call == null ? string.Empty : call.GetArgument("uin");
            if (string.IsNullOrWhiteSpace(raw)) raw = call == null ? string.Empty : call.GetArgument("qq");
            raw = (raw ?? string.Empty).Trim();
            if (IsSelfToken(raw)) return selfUin;
            long parsed;
            if (long.TryParse(raw, out parsed) && parsed > 0) return parsed;

            if (!string.IsNullOrWhiteSpace(herUin) &&
                long.TryParse(herUin.Trim(), out parsed) && parsed > 0)
                return parsed;

            var sessionUin = ReadSessionUin(context);
            if (sessionUin > 0) return sessionUin;

            var lastPrivate = ReadLastPrivateUin(context);
            if (lastPrivate > 0) return lastPrivate;

            if (idle && selfUin > 0) return selfUin;
            throw new InvalidOperationException(
                "看说说需要 uin。填对方 QQ 号，看我自己填 self，或在插件配置 her_uin。");
        }

        private static bool IsSelfToken(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return false;
            return string.Equals(raw, "self", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(raw, "me", StringComparison.OrdinalIgnoreCase) ||
                   raw == "我" || raw == "自己";
        }

        private static long ReadSessionUin(TraceTurnContext context)
        {
            var json = context == null || context.Moment == null
                ? string.Empty : context.Moment.PayloadJson ?? string.Empty;
            if (json.Length == 0) return 0;
            try
            {
                using (var doc = JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;
                    string sessionType = null;
                    if (root.TryGetProperty("session_type", out var typeEl) &&
                        typeEl.ValueKind == JsonValueKind.String)
                        sessionType = typeEl.GetString();
                    if (!string.IsNullOrWhiteSpace(sessionType) &&
                        !string.Equals(sessionType, "private", StringComparison.OrdinalIgnoreCase))
                        return 0;
                    if (root.TryGetProperty("session_id", out var idEl))
                    {
                        long uin;
                        if (idEl.ValueKind == JsonValueKind.Number && idEl.TryGetInt64(out uin) && uin > 0)
                            return uin;
                        if (idEl.ValueKind == JsonValueKind.String &&
                            long.TryParse(idEl.GetString(), out uin) && uin > 0)
                            return uin;
                    }
                }
            }
            catch
            {
                /* 不是会话载荷 */
            }
            return 0;
        }

        private static async Task<string> FetchCookiesAsync(
            ITracePlatformAdapter adapter, CancellationToken cancellationToken)
        {
            var domains = new[] { "user.qzone.qq.com", "qzone.qq.com", ".qq.com", "qq.com", "" };
            var actions = new[] { "get_cookies", "get_credentials" };
            foreach (var action in actions)
            {
                foreach (var domain in domains)
                {
                    try
                    {
                        var args = string.IsNullOrEmpty(domain)
                            ? new Dictionary<string, object>()
                            : new Dictionary<string, object> { { "domain", domain } };
                        var json = await adapter.CallActionAsync(action, args, cancellationToken);
                        var cookies = ExtractCookies(json);
                        if (!string.IsNullOrWhiteSpace(cookies)) return cookies;
                    }
                    catch
                    {
                        /* 换域名或动作再试 */
                    }
                }
            }
            return string.Empty;
        }

        private static string ExtractCookies(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return string.Empty;
            try
            {
                using (var doc = JsonDocument.Parse(json))
                {
                    if (doc.RootElement.TryGetProperty("cookies", out var c) && c.ValueKind == JsonValueKind.String)
                        return c.GetString() ?? string.Empty;
                    if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object &&
                        data.TryGetProperty("cookies", out var c2) && c2.ValueKind == JsonValueKind.String)
                        return c2.GetString() ?? string.Empty;
                }
            }
            catch { /* 非 JSON */ }
            return string.Empty;
        }

        private static Dictionary<string, string> ParseCookies(string cookies)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in cookies.Split(';'))
            {
                var eq = part.IndexOf('=');
                if (eq <= 0) continue;
                var key = part.Substring(0, eq).Trim();
                var value = part.Substring(eq + 1).Trim();
                if (key.Length > 0) result[key] = value;
            }
            return result;
        }

        private static long CalcGtk(string skey)
        {
            var h = 5381L;
            foreach (var ch in skey)
                h += (h << 5) + ch;
            return h & 0x7FFFFFFF;
        }

        private static HttpClient NewClient()
        {
            return new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        }

        private static void ApplyBrowserHeaders(HttpRequestMessage request, QzoneSession session)
        {
            request.Headers.TryAddWithoutValidation("Cookie", session.Cookies);
            request.Headers.TryAddWithoutValidation("Referer", "https://user.qzone.qq.com/" + session.Uin + "/main");
            request.Headers.TryAddWithoutValidation("Origin", "https://user.qzone.qq.com");
            request.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        }

        private static string StripEnvelope(string text)
        {
            text = (text ?? string.Empty).Trim().TrimStart('\uFEFF');
            var htmlCb = Regex.Match(text, @"frameElement\.callback\((\{.*\})\)", RegexOptions.Singleline);
            if (htmlCb.Success) return htmlCb.Groups[1].Value;
            if (JsonpPrefix.IsMatch(text))
            {
                text = JsonpPrefix.Replace(text, string.Empty, 1);
                text = Regex.Replace(text, @"\);?\s*$", string.Empty);
            }
            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            if (start >= 0 && end > start) return text.Substring(start, end - start + 1);
            return text;
        }

        private static void TryReadCode(string json, out long code, out string message)
        {
            code = long.MinValue;
            message = string.Empty;
            if (string.IsNullOrWhiteSpace(json)) return;
            try
            {
                using (var doc = JsonDocument.Parse(json))
                {
                    if (doc.RootElement.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number)
                        code = c.GetInt64();
                    if (doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                        message = m.GetString();
                }
            }
            catch { /* 非 JSON 按已投递处理 */ }
        }

        internal static List<QzoneFeed> ParseFeeds(string json, int take)
        {
            var result = new List<QzoneFeed>();
            if (string.IsNullOrWhiteSpace(json)) return result;
            try
            {
                using (var doc = JsonDocument.Parse(json))
                {
                    JsonElement msglist;
                    if (!TryFindMsgList(doc.RootElement, out msglist) ||
                        msglist.ValueKind != JsonValueKind.Array)
                        return result;
                    foreach (var item in msglist.EnumerateArray())
                    {
                        var feed = ParseFeed(item);
                        if (feed == null || string.IsNullOrWhiteSpace(feed.Tid)) continue;
                        result.Add(feed);
                        if (result.Count >= take) break;
                    }
                }
            }
            catch
            {
                return result;
            }
            return result;
        }

        private static bool TryFindMsgList(JsonElement root, out JsonElement msglist)
        {
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("msglist", out msglist) &&
                msglist.ValueKind == JsonValueKind.Array)
                return true;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("data", out var data))
            {
                if (data.ValueKind == JsonValueKind.Object &&
                    data.TryGetProperty("msglist", out msglist) &&
                    msglist.ValueKind == JsonValueKind.Array)
                    return true;
                if (data.ValueKind == JsonValueKind.Object &&
                    data.TryGetProperty("data", out var inner) &&
                    inner.ValueKind == JsonValueKind.Object &&
                    inner.TryGetProperty("msglist", out msglist) &&
                    msglist.ValueKind == JsonValueKind.Array)
                    return true;
            }
            msglist = default(JsonElement);
            return false;
        }

        private static QzoneFeed ParseFeed(JsonElement raw)
        {
            if (raw.ValueKind != JsonValueKind.Object) return null;
            var feed = new QzoneFeed
            {
                Tid = ReadAnyString(raw, "tid", "key"),
                Name = ReadAnyString(raw, "name", "nickname"),
                Content = ReadAnyString(raw, "content", "text", "summary"),
                CreateUnix = ReadUnix(raw, "created_time", "create_time", "time")
            };
            if (raw.TryGetProperty("rt_con", out var rt))
            {
                if (rt.ValueKind == JsonValueKind.String) feed.Repost = rt.GetString() ?? string.Empty;
                else if (rt.ValueKind == JsonValueKind.Object)
                    feed.Repost = ReadAnyString(rt, "content", "text", "summary");
            }
            JsonElement pics;
            if (TryGetFirst(raw, out pics, "pic", "pics", "image") && pics.ValueKind == JsonValueKind.Array)
            {
                foreach (var pic in pics.EnumerateArray())
                {
                    var url = pic.ValueKind == JsonValueKind.String
                        ? pic.GetString()
                        : ReadAnyString(pic, "url3", "url2", "url1", "url");
                    if (!string.IsNullOrWhiteSpace(url)) feed.Images.Add(url);
                }
            }
            JsonElement comments;
            if (TryGetFirst(raw, out comments, "commentlist", "comments") &&
                comments.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in comments.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var name = ReadAnyString(item, "name", "nickname");
                    if (item.TryGetProperty("poster", out var poster) && poster.ValueKind == JsonValueKind.Object &&
                        string.IsNullOrWhiteSpace(name))
                        name = ReadAnyString(poster, "name", "nickname");
                    var content = ReadAnyString(item, "content");
                    if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(content)) continue;
                    feed.Comments.Add((string.IsNullOrWhiteSpace(name) ? "路人" : name) + "：" + content);
                    if (feed.Comments.Count >= 8) break;
                }
            }
            return feed;
        }

        internal static string FormatFeeds(long target, List<QzoneFeed> feeds)
        {
            var builder = new StringBuilder();
            builder.Append("【QQ说说 · ").Append(target).Append(" · 共")
                .Append(feeds == null ? 0 : feeds.Count).AppendLine("条】");
            if (feeds == null || feeds.Count == 0)
            {
                builder.Append("没有读到说说。空间可能关闭了权限，或 Cookie 不够看这条主页。");
                return builder.ToString();
            }
            for (var i = 0; i < feeds.Count; i++)
            {
                var feed = feeds[i];
                builder.Append(i + 1).Append(". (").Append(FormatTime(feed.CreateUnix)).Append(") ");
                if (!string.IsNullOrWhiteSpace(feed.Name)) builder.Append(feed.Name).Append("：");
                builder.AppendLine(string.IsNullOrWhiteSpace(feed.Content) ? "（无正文）" : OneLine(feed.Content));
                if (!string.IsNullOrWhiteSpace(feed.Repost))
                    builder.Append("   转发：").AppendLine(OneLine(feed.Repost));
                if (feed.Images.Count > 0)
                    builder.Append("   图：").Append(feed.Images.Count).AppendLine("张");
                if (feed.Comments.Count > 0)
                {
                    builder.AppendLine("   评论：");
                    foreach (var comment in feed.Comments.Take(6))
                        builder.Append("   - ").AppendLine(OneLine(comment));
                }
            }
            return builder.ToString().TrimEnd();
        }

        private static string FormatTime(long unixSeconds)
        {
            if (unixSeconds <= 0) return "时间未知";
            if (unixSeconds > 1000000000000L) unixSeconds = unixSeconds / 1000L;
            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(unixSeconds)
                    .ToOffset(TimeSpan.FromHours(8))
                    .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            }
            catch
            {
                return "时间未知";
            }
        }

        private static bool TryGetFirst(JsonElement root, out JsonElement value, params string[] names)
        {
            foreach (var name in names)
            {
                if (root.TryGetProperty(name, out value)) return true;
            }
            value = default(JsonElement);
            return false;
        }

        private static string ReadAnyString(JsonElement root, params string[] names)
        {
            if (root.ValueKind != JsonValueKind.Object) return string.Empty;
            foreach (var name in names)
            {
                if (!root.TryGetProperty(name, out var value)) continue;
                if (value.ValueKind == JsonValueKind.String) return value.GetString() ?? string.Empty;
                if (value.ValueKind == JsonValueKind.Number) return value.ToString();
            }
            return string.Empty;
        }

        private static long ReadUnix(JsonElement root, params string[] names)
        {
            foreach (var name in names)
            {
                if (!root.TryGetProperty(name, out var value)) continue;
                if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n)) return n;
                long parsed;
                if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out parsed))
                    return parsed;
            }
            return 0;
        }

        private static int ReadInt(string raw, int fallback, int min, int max)
        {
            int value;
            if (!int.TryParse((raw ?? string.Empty).Trim(), out value)) value = fallback;
            if (value < min) value = min;
            if (value > max) value = max;
            return value;
        }

        private static string ReadString(JsonElement root, string name)
        {
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var value) ||
                value.ValueKind != JsonValueKind.String)
                return null;
            return value.GetString();
        }

        private static int ReadCap(JsonElement root, string name, int fallback)
        {
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var value))
                return fallback;
            int parsed;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out parsed))
                return ClampCap(parsed);
            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out parsed))
                return ClampCap(parsed);
            return fallback;
        }

        private static int ClampCap(int value)
        {
            if (value < 0) return 0;
            return value > 20 ? 20 : value;
        }

        private static bool IsIdleCall(BrainCapabilityCallData call)
        {
            var raw = call == null ? string.Empty : (call.GetArgument("idle") ?? string.Empty).Trim();
            return string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase) ||
                   raw == "1" ||
                   string.Equals(raw, "yes", StringComparison.OrdinalIgnoreCase);
        }

        private static bool LooksLikeNone(string value)
        {
            var text = (value ?? string.Empty).Trim().Trim('。', '.', '！', '!', '～', '~', ' ');
            if (text.Length == 0) return true;
            return text == "无" || text == "没有" || text == "不发" || text == "不想发" ||
                   text == "（无）" || text == "(无)" ||
                   string.Equals(text, "none", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(text, "skip", StringComparison.OrdinalIgnoreCase);
        }

        private static long ReadLastPrivateUin(TraceTurnContext context)
        {
            if (context == null || context.Services == null || context.Services.Storage == null)
                return 0;
            try
            {
                var json = context.Services.Storage.LoadPluginDocument("builtin.onebot", "last_session");
                if (string.IsNullOrWhiteSpace(json)) return 0;
                using (var doc = JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;
                    string sessionType = null;
                    if (root.TryGetProperty("session_type", out var typeEl) &&
                        typeEl.ValueKind == JsonValueKind.String)
                        sessionType = typeEl.GetString();
                    if (!string.IsNullOrWhiteSpace(sessionType) &&
                        !string.Equals(sessionType, "private", StringComparison.OrdinalIgnoreCase))
                        return 0;
                    if (!root.TryGetProperty("session_id", out var idEl)) return 0;
                    long uin;
                    if (idEl.ValueKind == JsonValueKind.Number && idEl.TryGetInt64(out uin) && uin > 0)
                        return uin;
                    if (idEl.ValueKind == JsonValueKind.String &&
                        long.TryParse(idEl.GetString(), out uin) && uin > 0)
                        return uin;
                }
            }
            catch
            {
                /* 会话记忆损坏时忽略 */
            }
            return 0;
        }

        private async Task<string> ComposePublishAsync(
            string seed, TraceTurnContext context, CancellationToken cancellationToken)
        {
            if (context == null || context.Services == null || context.Services.Llm == null)
                return string.Empty;
            var llm = context.Services.Llm;
            var packer = context.Services.ContextPack;
            var user = (seed ?? string.Empty).Trim();
            if (user.Length == 0) user = "（没有更多此刻材料）";
            List<DeepSeekMessageData> messages;
            string cacheKey = null;
            if (packer != null)
            {
                var memory = context.Workspace == null ? string.Empty : context.Workspace.SharedMemory;
                messages = packer.Assemble(
                    llm, context, memory ?? string.Empty, user,
                    QqQzonePrompts.IdlePublishRoleHeader, QqQzonePrompts.IdlePublishInstructions);
                cacheKey = packer.BuildPromptCacheKey(llm, context.ConversationId);
            }
            else
            {
                messages = new List<DeepSeekMessageData>
                {
                    new DeepSeekMessageData("system", QqQzonePrompts.IdlePublishInstructions),
                    new DeepSeekMessageData("user", user)
                };
            }
            var raw = await llm.CompleteTextAsync(messages, cancellationToken, cacheKey);
            return StripCompose(raw);
        }

        private static string StripCompose(string raw)
        {
            var text = (raw ?? string.Empty).Trim();
            if (text.StartsWith("```", StringComparison.Ordinal))
            {
                var start = text.IndexOf('\n');
                var end = text.LastIndexOf("```", StringComparison.Ordinal);
                if (start >= 0 && end > start) text = text.Substring(start + 1, end - start - 1).Trim();
            }
            return text.Trim().Trim('"');
        }

        private static string OneLine(string value)
        {
            return string.Join(" ", (value ?? string.Empty)
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
        }

        private sealed class QzoneSession
        {
            public long Uin;
            public string Cookies;
            public long Gtk;
        }

        internal sealed class QzoneFeed
        {
            public string Tid = string.Empty;
            public string Name = string.Empty;
            public string Content = string.Empty;
            public string Repost = string.Empty;
            public long CreateUnix;
            public List<string> Images = new List<string>();
            public List<string> Comments = new List<string>();
        }

        private sealed class UsageFacet : ITraceMountedFacet
        {
            public TraceContributionDescriptorData Descriptor { get; } = new TraceContributionDescriptorData
            {
                Id = "qq.qzone.usage",
                Kind = TraceContributionKindValues.MountedFacet,
                DisplayName = "QQ 说说用法",
                Description = "QQ 说说插件注入：什么时候该发、什么时候该看。",
                Provides = "platform.qq.qzone_usage",
                RefreshMode = TraceFacetRefreshValues.OncePerTurn,
                Priority = 90,
                MaxContextChars = 420
            };

            public bool IsAvailable(TraceTurnContext context) { return context != null; }

            public Task<TraceContextBlockData> BuildContextAsync(
                TraceTurnContext context, CancellationToken cancellationToken)
            {
                return Task.FromResult(new TraceContextBlockData
                {
                    Title = "QQ 说说用法",
                    Content = QqQzonePrompts.Usage
                });
            }

            public Task<TraceCapabilityResultData> ApplyOutputAsync(
                BrainFacetOutputData output, TraceTurnContext context, CancellationToken cancellationToken)
            {
                return Task.FromResult<TraceCapabilityResultData>(null);
            }
        }

        private sealed class QzonePublishEffector : ITraceCallableContribution
        {
            private readonly QqQzonePlugin owner;
            public QzonePublishEffector(QqQzonePlugin owner)
            {
                this.owner = owner;
                Descriptor = new TraceContributionDescriptorData
                {
                    Id = "qq.qzone.publish",
                    Kind = TraceContributionKindValues.Effector,
                    DisplayName = "QQ 空间发说说",
                    Description = QqQzonePrompts.PublishDescription,
                    Provides = "expression.qq.qzone",
                    Boundary = QqQzonePrompts.PublishBoundary,
                    BodyId = BodyIds.Qq,
                    BodyTier = BodyTierValues.Chat,
                    Organ = BodyOrganValues.Qzone,
                    ParametersJsonSchema = "{content:string}",
                    HasExternalSideEffect = true,
                    IdleDailyCap = owner.publishDailyCap
                };
            }

            public TraceContributionDescriptorData Descriptor { get; }

            public bool IsAvailable(TraceTurnContext context) { return context != null; }

            public Task<TraceCapabilityResultData> ExecuteAsync(
                BrainCapabilityCallData call, TraceTurnContext context, CancellationToken cancellationToken)
            {
                return owner.PublishFromCallAsync(call, context, cancellationToken);
            }
        }

        private sealed class QzoneReadNerve : ITraceCallableContribution
        {
            private readonly QqQzonePlugin owner;
            public QzoneReadNerve(QqQzonePlugin owner)
            {
                this.owner = owner;
                Descriptor = new TraceContributionDescriptorData
                {
                    Id = "qq.qzone.read",
                    Kind = TraceContributionKindValues.CallableNerve,
                    DisplayName = "QQ 空间看说说",
                    Description = QqQzonePrompts.ReadDescription,
                    Provides = "sense.qq.qzone",
                    WhenToUse = QqQzonePrompts.ReadWhenToUse,
                    WhenNotToUse = QqQzonePrompts.ReadWhenNotToUse,
                    Boundary = QqQzonePrompts.ReadBoundary,
                    BodyId = BodyIds.Qq,
                    BodyTier = BodyTierValues.Chat,
                    ParametersJsonSchema = "{uin:string,pos:number,num:number}",
                    HasExternalSideEffect = false,
                    IdleDailyCap = owner.readDailyCap
                };
            }

            public TraceContributionDescriptorData Descriptor { get; }

            public bool IsAvailable(TraceTurnContext context) { return context != null; }

            public Task<TraceCapabilityResultData> ExecuteAsync(
                BrainCapabilityCallData call, TraceTurnContext context, CancellationToken cancellationToken)
            {
                return owner.ReadFromCallAsync(call, context, cancellationToken);
            }
        }
    }
}
