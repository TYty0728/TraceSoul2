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
        internal const int ReplyThreshold = 6;
        internal static string Build(TraceTurnContext turn)
        {
            var storage = turn?.Services?.Storage;
            if (storage == null) return string.Empty;
            // 本轮已经尝试相机时，由执行反馈引导后续，不重复给出节奏邀请。
            if (turn.Workspace.Results.Any(x => x?.CapabilityId == "qq.imagegen.generate")) return string.Empty;
            var session = Session(turn);
            var checkedAt = LastCheck(turn, session);
            var last = storage.GetRecentOperationalEvents(turn.ConversationId, 200)
                .Where(x => x.Kind == OperationalEventKindValues.OutboundImage &&
                            x.SourcePluginId == "builtin.onebot" &&
                            (session.Length == 0 || SessionKey(x.PayloadJson) == session))
                .OrderByDescending(x => x.CreatedUnixMs).FirstOrDefault();
            var pair = storage.LoadPairIdentity();
            var replies = storage.GetRecentDialogueMoments(turn.ConversationId, 80)
                .Count(x => pair.IsCompanionMoment(x.Role) &&
                            x.CreatedUnixMs > Math.Max(last?.CreatedUnixMs ?? 0, checkedAt) &&
                            (session.Length == 0 || SessionKey(x.PayloadJson) == session));
            if (replies < ReplyThreshold) return string.Empty;
            return "【相机此刻】\n结合当下的表达和场景，拍一张适合这一刻的照片，让她看看此刻的自己或眼前。分享贴合当前话题，并照顾她明确的收图与安静约定。";
        }

        internal static bool TryReserve(TraceTurnContext turn)
        {
            if (Build(turn).Length == 0) return false;
            var session = Session(turn);
            var pair = turn.Services.Storage.LoadPairIdentity();
            var latest = turn.Services.Storage.GetRecentDialogueMoments(turn.ConversationId, 80)
                .Where(x => pair.IsCompanionMoment(x.Role) &&
                    (session.Length == 0 || SessionKey(x.PayloadJson) == session))
                .Select(x => x.CreatedUnixMs).DefaultIfEmpty(0).Max();
            turn.Services.Storage.SavePluginDocument("qq.imagegen", "sharing-check:" + turn.ConversationId + ":" + session,
                JsonSerializer.Serialize(latest));
            return true;
        }

        private static long LastCheck(TraceTurnContext turn, string session)
        {
            var raw = turn.Services.Storage.LoadPluginDocument("qq.imagegen", "sharing-check:" + turn.ConversationId + ":" + session);
            return string.IsNullOrEmpty(raw) ? 0 : JsonSerializer.Deserialize<long>(raw);
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
