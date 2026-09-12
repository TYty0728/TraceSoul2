using System;
using System.Linq;
using System.Text.Json;
using TraceSoul2.Data;
using TraceSoul2.Plugins;

namespace TraceSoul2.ExternalPlugins
{
    /// <summary>相机用真实发送回执提醒分享节奏；不替心智决定、不按轮数强制发图。</summary>
    internal static class CameraSharingContext
    {
        internal static string Build(TraceTurnContext turn)
        {
            var storage = turn?.Services?.Storage;
            if (storage == null) return string.Empty;
            var session = SessionKey(turn.Moment?.PayloadJson);
            var last = storage.GetRecentOperationalEvents(turn.ConversationId, 200)
                .Where(x => x.Kind == OperationalEventKindValues.OutboundImage &&
                            x.SourcePluginId == "builtin.onebot" &&
                            (session.Length == 0 || SessionKey(x.PayloadJson) == session))
                .OrderByDescending(x => x.CreatedUnixMs).FirstOrDefault();
            var pair = storage.LoadPairIdentity();
            var replies = storage.GetRecentDialogueMoments(turn.ConversationId, 80)
                .Count(x => pair.IsCompanionMoment(x.Role) &&
                            (last == null || x.CreatedUnixMs > last.CreatedUnixMs) &&
                            (session.Length == 0 || SessionKey(x.PayloadJson) == session));
            var feedback = last == null
                ? "最近可查的发送记录里没有给她发过照片；这不代表从来没有拍过。"
                : "最近一张照片已成功发出，距今约" +
                  Math.Max(0, (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - last.CreatedUnixMs) / 60000) +
                  "分钟；之后已有" + replies + "次文字回应。";
            var invitation = replies >= 6
                ? "我们已经有一段文字相处。现在主动想一想：有没有想让她看见的自己或眼前？若有，就选 image=有，把普通此刻也分享给她，不必等索图或新事件。"
                : last == null
                    ? "这一拍如果想让她看看自己或眼前，可以自然地选 image=有，不必等索图或新事件。"
                    : "刚分享过的照片也算相处的一部分；有新的分享欲或她明确想看时可以再拍，不必机械连发。";
            return "【相机此刻】\n" + feedback + "\n" + invitation +
                   "不是拍照任务或固定配额；她不想收图、这一拍需要专心听她说话，或没有分享欲时选无。不要向她汇报这些计数。";
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
