using TraceSoul2.Data;

namespace TraceSoul2.Logic
{
    /// <summary>中枢入口：从事件上读出该叫醒哪一套循环。</summary>
    public static class KernelWakeLogic
    {
        public static string Resolve(PluginEventData source)
        {
            if (source == null) return KernelWakeValues.Dialogue;
            if (LooksLikeDailyReview(source.Content))
                return KernelWakeValues.Subconscious;
            if (!string.IsNullOrWhiteSpace(source.Wake))
            {
                var named = KernelWakeValues.Normalize(source.Wake);
                if (named == KernelWakeValues.Subconscious)
                    return KernelWakeValues.Mind;
                if (!string.IsNullOrEmpty(named)) return named;
            }
            var fromPayload = ReadPayloadWake(source.PayloadJson);
            if (fromPayload == KernelWakeValues.Subconscious)
                return KernelWakeValues.Mind;
            if (!string.IsNullOrEmpty(fromPayload)) return fromPayload;
            return Infer(source.Role, source.Content);
        }

        public static string Infer(string role, string content)
        {
            if (LooksLikeDailyReview(content)) return KernelWakeValues.Subconscious;
            if (string.Equals(role, "system_event", System.StringComparison.OrdinalIgnoreCase) ||
                string.Equals(role, "system", System.StringComparison.OrdinalIgnoreCase))
                return KernelWakeValues.Mind;
            return KernelWakeValues.Dialogue;
        }

        public static bool LooksLikeDailyReview(string content)
        {
            return (content ?? string.Empty).IndexOf("每日复盘", System.StringComparison.Ordinal) >= 0;
        }

        public static bool IsSubconscious(string wake)
        {
            return wake == KernelWakeValues.Subconscious;
        }

        private static string ReadPayloadWake(string payloadJson)
        {
            var json = payloadJson ?? string.Empty;
            const string key = "\"wake\":\"";
            var at = json.IndexOf(key, System.StringComparison.Ordinal);
            if (at < 0) return string.Empty;
            var start = at + key.Length;
            var end = json.IndexOf('"', start);
            if (end <= start) return string.Empty;
            return KernelWakeValues.Normalize(json.Substring(start, end - start));
        }
    }
}
