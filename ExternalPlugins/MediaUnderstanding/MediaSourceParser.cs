using System.Text.RegularExpressions;
using TraceSoul2.Data;

namespace TraceSoul2.ExternalPlugins.MediaUnderstanding;

/// <summary>只做本地解析；网络获取器必须另行检查 DNS、重定向及内容大小。</summary>
public static class MediaSourceParser
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly Regex Links = new(@"https?://[^\s<>""'，。！；、]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);
    private static readonly Regex BiliVideo = new(@"^/video/(?<id>BV[0-9A-Za-z]{10}|av[0-9]+)/?$",
        RegexOptions.CultureInvariant, MatchTimeout);
    private static readonly Regex DouyinVideo = new(@"^/(?<kind>video|note)/(?<id>[0-9]+)/?$",
        RegexOptions.CultureInvariant, MatchTimeout);
    private static readonly Regex ShortPath = new(@"^/[0-9A-Za-z_-]{1,100}/?$",
        RegexOptions.CultureInvariant, MatchTimeout);

    public static IReadOnlyList<MediaSourceData> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<MediaSourceData>();
        if (text.Length > 16_384) throw new ArgumentException("分享文字超过 16384 字符。");
        var results = new List<MediaSourceData>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in Links.Matches(text))
        {
            var candidate = match.Value.TrimEnd(')', ']', '}', '）', '】', '.', ',', ';', '!', '？', '?');
            var parsed = ParseUrl(candidate);
            if (parsed != null && seen.Add(parsed.CanonicalUrl)) results.Add(parsed);
            if (results.Count >= 8) break;
        }
        return results;
    }

    public static MediaSourceData? ParseUrl(string? value)
    {
        if (value == null || value.Length > 4096 || value.Contains('\\') ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "https" && uri.Scheme != "http") ||
            !uri.IsDefaultPort || uri.UserInfo.Length > 0) return null;
        var host = uri.IdnHost.ToLowerInvariant();
        var path = uri.AbsolutePath;
        if ((host == "b23.tv" || host == "v.douyin.com" || host == "jx.douyin.com") && ShortPath.IsMatch(path))
            return new MediaSourceData
            {
                Platform = host == "b23.tv" ? "bilibili" : "douyin", Kind = "share_link",
                CanonicalUrl = "https://" + host + path, RequiresResolution = true
            };

        if (host is "bilibili.com" or "www.bilibili.com" or "m.bilibili.com")
        {
            var match = BiliVideo.Match(path);
            if (!match.Success) return null;
            var part = 1;
            foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var bits = pair.Split('=', 2);
                if (bits[0] != "p") continue;
                if (bits.Length != 2 || !int.TryParse(bits[1], out part) || part < 1 || part > 10000) return null;
            }
            var id = match.Groups["id"].Value;
            return new MediaSourceData
            {
                Platform = "bilibili", Kind = "video", MediaId = id, Part = part,
                CanonicalUrl = "https://www.bilibili.com/video/" + id + (part == 1 ? "" : "?p=" + part)
            };
        }
        if (host is "douyin.com" or "www.douyin.com")
        {
            var match = DouyinVideo.Match(path);
            if (!match.Success) return null;
            var id = match.Groups["id"].Value;
            var kind = match.Groups["kind"].Value;
            return new MediaSourceData
            {
                Platform = "douyin", Kind = kind == "note" ? "image_post" : "video", MediaId = id,
                CanonicalUrl = "https://www.douyin.com/" + kind + "/" + id
            };
        }
        return null;
    }
}
