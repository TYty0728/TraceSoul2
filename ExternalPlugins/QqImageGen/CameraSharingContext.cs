using System;
using System.Linq;
using System.Text.Json;
using TraceSoul2.Data;
using TraceSoul2.Plugins;

namespace TraceSoul2.ExternalPlugins
{
    /// <summary>程序根据真实记录判断分享门槛，模型只收到结合当下的照片邀请。</summary>
    internal static class CameraSharingContext
    {
        internal const int DefaultMinReplies = 4, DefaultMaxReplies = 8;
        internal sealed class SharingCycle
        {
            public long Anchor { get; set; }
            public long CountedThrough { get; set; }
            public int Count { get; set; }
            public int Minimum { get; set; }
            public int Maximum { get; set; }
            public int Threshold { get; set; }
        }

        internal static string Build(TraceTurnContext turn, int minimum = DefaultMinReplies, int maximum = DefaultMaxReplies)
        {
            if (turn?.Services?.Storage == null) return string.Empty;
            if (turn.Workspace.Results.Any(x => x?.CapabilityId == "qq.imagegen.generate")) return string.Empty;
            var cycle = GetCycle(turn, minimum, maximum);
            if (cycle.Count < cycle.Threshold) return string.Empty;
            return "【相机此刻】\n结合当下的表达和场景，拍一张适合这一刻的照片，让她看看此刻的自己或眼前。分享贴合当前话题，并照顾她明确的收图与安静约定。";
        }

        internal static bool TryReserve(TraceTurnContext turn, int minimum = DefaultMinReplies, int maximum = DefaultMaxReplies)
        {
            if (Build(turn, minimum, maximum).Length == 0) return false;
            var cycle = GetCycle(turn, minimum, maximum);
            turn.Services.Storage.SavePluginDocument("qq.imagegen", "sharing-check:" + turn.ConversationId + ":" + Session(turn),
                JsonSerializer.Serialize(cycle.CountedThrough));
            // 判断无论分享、略过或失败均消费机会；下一轮门槛立即抽取并持久化。
            GetCycle(turn, minimum, maximum);
            return true;
        }

        internal static SharingCycle GetCycle(TraceTurnContext turn, int minimum, int maximum)
        {
            minimum = Math.Clamp(minimum, 1, 1000);
            maximum = Math.Clamp(maximum, 1, 1000);
            if (minimum > maximum) (minimum, maximum) = (maximum, minimum);
            var storage = turn.Services.Storage;
            var session = Session(turn);
            var suffix = turn.ConversationId + ":" + session;
            var raw = storage.LoadPluginDocument("qq.imagegen", "sharing-cycle:" + suffix);
            var cycle = string.IsNullOrEmpty(raw) ? null : JsonSerializer.Deserialize<SharingCycle>(raw);
            var original = cycle == null ? null : JsonSerializer.Serialize(cycle);
            var checkedRaw = storage.LoadPluginDocument("qq.imagegen", "sharing-check:" + suffix);
            var checkedAt = string.IsNullOrEmpty(checkedRaw) ? 0 : JsonSerializer.Deserialize<long>(checkedRaw);
            var imageAt = storage.GetRecentOperationalEvents(turn.ConversationId, 200)
                .Where(x => x.Kind == OperationalEventKindValues.OutboundImage && x.SourcePluginId == "builtin.onebot" &&
                    (session.Length == 0 || SessionKey(x.PayloadJson) == session))
                .Select(x => x.CreatedUnixMs).DefaultIfEmpty(0).Max();
            var anchor = Math.Max(checkedAt, imageAt);
            // 保留已知锚点，旧图片离开近期查询窗口不导致重新计数。
            if (cycle == null || anchor > cycle.Anchor)
                cycle = new SharingCycle { Anchor = anchor, CountedThrough = anchor };
            if (cycle.Minimum != minimum || cycle.Maximum != maximum || cycle.Threshold < minimum || cycle.Threshold > maximum)
            {
                cycle.Minimum = minimum;
                cycle.Maximum = maximum;
                cycle.Threshold = Random.Shared.Next(minimum, maximum + 1);
            }
            var pair = storage.LoadPairIdentity();
            var fresh = storage.GetRecentDialogueMoments(turn.ConversationId, 200)
                .Where(x => pair.IsCompanionMoment(x.Role) && x.CreatedUnixMs > cycle.CountedThrough &&
                    (session.Length == 0 || SessionKey(x.PayloadJson) == session)).ToList();
            cycle.Count += fresh.Count;
            if (fresh.Count > 0) cycle.CountedThrough = fresh.Max(x => x.CreatedUnixMs);
            var updated = JsonSerializer.Serialize(cycle);
            if (updated != original) storage.SavePluginDocument("qq.imagegen", "sharing-cycle:" + suffix, updated);
            return cycle;
        }

        private static string Session(TraceTurnContext turn)
        {
            var session = SessionKey(turn.Moment?.PayloadJson);
            return session.Length > 0 ? session :
                SessionKey(turn.Services.Storage.LoadPluginDocument("builtin.onebot", "last_session"));
        }

        private static string SessionKey(string payload)
        {
            try
            {
                using (var doc = JsonDocument.Parse(payload ?? "{}"))
                    if (doc.RootElement.TryGetProperty("session_type", out var type) &&
                        doc.RootElement.TryGetProperty("session_id", out var id))
                        return type.GetString() + ":" + id.GetString();
            }
            catch (JsonException) { }
            return string.Empty;
        }
    }
}
