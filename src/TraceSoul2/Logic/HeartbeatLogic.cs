using System;
using System.Collections.Generic;
using TraceSoul2.Data;
using TraceSoul2.Manager;
using TraceSoul2.Plugins.Builtin;
using TraceSoul2.Util;

namespace TraceSoul2.Logic
{
    /// <summary>
    /// 心跳：Moment 处理完后排一次；到期自己想要不要说、办事、睡下、多久后再跳。
    /// 睡着后停跳，直到打破性 Moment（现在就是她发来的话）才醒来。
    /// </summary>
    public static class HeartbeatLogic
    {
        public const string PluginId = "builtin.time";
        public const string ScheduleDocumentKey = "schedules";
        public const string HeartbeatContent = "心跳";
        public const int DefaultMinMinutes = 10;
        public const int DefaultMaxMinutes = 20;
        public const int MinuteCap = 180;

        private static readonly Random Random = new Random();

        public static bool IsHeartbeatContent(string content)
        {
            var value = (content ?? string.Empty).Trim();
            if (value == HeartbeatContent) return true;
            const string due = TimeSchedulerPrompts.DuePrefix;
            if (value.StartsWith(due, StringComparison.Ordinal))
                value = value.Substring(due.Length).Trim();
            return value == HeartbeatContent;
        }

        public static bool IsHeartbeatOrLegacyContinue(string content)
        {
            if (IsHeartbeatContent(content)) return true;
            return InnerLifeLogic.IsContinuationContent(content);
        }

        public static bool IsEnabled(int minMinutes, int maxMinutes)
        {
            return Math.Max(minMinutes, maxMinutes) > 0;
        }

        public static int ClampMinutes(int minutes)
        {
            if (minutes <= 0) return 0;
            return minutes > MinuteCap ? MinuteCap : minutes;
        }

        public static void NormalizeRange(ref int minMinutes, ref int maxMinutes)
        {
            minMinutes = Math.Max(0, Math.Min(MinuteCap, minMinutes));
            maxMinutes = Math.Max(0, Math.Min(MinuteCap, maxMinutes));
            if (maxMinutes < minMinutes)
            {
                var swap = minMinutes;
                minMinutes = maxMinutes;
                maxMinutes = swap;
            }
        }

        public static long PickDueUnixMs(int minMinutes, int maxMinutes, DateTimeOffset now)
        {
            NormalizeRange(ref minMinutes, ref maxMinutes);
            if (!IsEnabled(minMinutes, maxMinutes)) return 0;
            var lo = Math.Max(1, minMinutes);
            var hi = Math.Max(lo, maxMinutes);
            int minutes;
            lock (Random)
                minutes = lo == hi ? lo : Random.Next(lo, hi + 1);
            return now.AddMinutes(minutes).ToUnixTimeMilliseconds();
        }

        public static long DueFromMinutes(int minutes, DateTimeOffset now)
        {
            minutes = ClampMinutes(minutes);
            if (minutes <= 0) return 0;
            return now.AddMinutes(minutes).ToUnixTimeMilliseconds();
        }

        public static bool IsBreaking(PluginEventData source, PairIdentity pair)
        {
            if (source == null) return false;
            if (source.Breaking) return true;
            return pair != null && pair.IsHumanMoment(source.Role);
        }

        public static bool ShouldSkipWhileAsleep(PluginEventData source, PairIdentity pair, string wake)
        {
            if (KernelWakeLogic.IsSubconscious(wake) || KernelWakeLogic.LooksLikeDailyReview(
                    source == null ? string.Empty : source.Content))
                return false;
            return !IsBreaking(source, pair);
        }

        public static long? NextDueUnixMs(IMemoryStore storage, string conversationId)
        {
            if (storage == null || string.IsNullOrWhiteSpace(conversationId)) return null;
            var json = storage.LoadPluginDocument(PluginId, ScheduleDocumentKey);
            if (string.IsNullOrWhiteSpace(json)) return null;
            ScheduleFile file;
            try { file = TraceJson.FromJson<ScheduleFile>(json); }
            catch { return null; }
            if (file == null || file.items == null) return null;
            long? soonest = null;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var item in file.items)
            {
                if (item == null || !item.enabled) continue;
                if (!string.Equals(item.conversation_id, conversationId, StringComparison.Ordinal))
                    continue;
                if (!IsHeartbeatOrLegacyContinue(item.content)) continue;
                if (item.due_unix_ms <= now) continue;
                if (!soonest.HasValue || item.due_unix_ms < soonest.Value)
                    soonest = item.due_unix_ms;
            }
            return soonest;
        }

        [Serializable]
        private sealed class ScheduleFile
        {
            public List<ScheduleItem> items = new List<ScheduleItem>();
        }

#pragma warning disable 0649
        [Serializable]
        private sealed class ScheduleItem
        {
            public string conversation_id = string.Empty;
            public string content = string.Empty;
            public long due_unix_ms;
            public bool enabled;
        }
#pragma warning restore 0649
    }
}
