using System;
using System.Collections.Generic;
using System.Linq;
using TraceSoul2.Data;
using TraceSoul2.Manager;
using TraceSoul2.Plugins.Builtin;
using TraceSoul2.Util;

namespace TraceSoul2.Logic
{
    /// <summary>
    /// 心跳：Moment 处理完后排一次；到期自己想要不要说、办事、睡下、多久后再跳。
    /// 睡着或空闲后停跳。睡着要等她发来才醒；空闲还会被以前约好的时间任务叫醒。
    /// </summary>
    public static class HeartbeatLogic
    {
        public const string PluginId = "builtin.time";
        public const string ScheduleDocumentKey = "schedules";
        public const string HeartbeatContent = "心跳";
        public const string PlanSeparator = "｜醒来计划：";
        public const string DefaultNextPlan = "重新看看时间、她有没有新消息和近期计划，再决定是否联系";
        public const string DefaultLongFollowUpPlan = "隔几个小时再看看时间、她有没有新消息和近期计划";
        public const int DefaultMinMinutes = 10;
        public const int DefaultMaxMinutes = 20;
        public const int DefaultLongFollowUpMinutes = 240;
        public const int IdleMinutesThreshold = 180;
        public const int MinuteCap = 720;

        private static readonly Random Random = new Random();

        public static bool IsHeartbeatContent(string content)
        {
            var value = (content ?? string.Empty).Trim();
            const string due = TimeSchedulerPrompts.DuePrefix;
            if (value.StartsWith(due, StringComparison.Ordinal))
                value = value.Substring(due.Length).Trim();
            return value == HeartbeatContent ||
                   value.StartsWith(HeartbeatContent + PlanSeparator, StringComparison.Ordinal);
        }

        public static string BuildContent(string nextPlan)
        {
            nextPlan = LimitPlan(nextPlan);
            return HeartbeatContent + PlanSeparator +
                   (nextPlan.Length == 0 ? DefaultNextPlan : nextPlan);
        }

        public static string ExtractPlan(string content)
        {
            var value = (content ?? string.Empty).Trim();
            const string due = TimeSchedulerPrompts.DuePrefix;
            if (value.StartsWith(due, StringComparison.Ordinal))
                value = value.Substring(due.Length).Trim();
            var marker = HeartbeatContent + PlanSeparator;
            if (!value.StartsWith(marker, StringComparison.Ordinal)) return string.Empty;
            return LimitPlan(value.Substring(marker.Length).Trim());
        }

        private static string LimitPlan(string value)
        {
            value = (value ?? string.Empty).Trim();
            return value.Length <= 120 ? value : value.Substring(0, 120);
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

        /// <summary>
        /// 心跳开口闸门。JSON 示例默认 speak=false，模型常一边写下 speak_center
        /// 一边保持安静；有想让她现在听见的话就开口。没有独立意图的开口则收回。
        /// </summary>
        public static void ApplySpeakGate(MindDecisionData decision)
        {
            if (decision == null) return;
            if (decision.sleep)
            {
                decision.speak = false;
                return;
            }
            var center = (decision.speak_center ?? string.Empty).Trim();
            if (!decision.speak && center.Length > 0)
            {
                decision.speak = true;
                if (string.IsNullOrWhiteSpace(decision.heartbeat_intent))
                    decision.heartbeat_intent = center;
                return;
            }
            if (decision.speak && string.IsNullOrWhiteSpace(decision.heartbeat_intent))
                decision.speak = false;
        }

        /// <summary>
        /// 心跳醒着时必须留下下一次检查；只有明确睡下或进入空闲才真正停跳。
        /// 模型没有给出分钟数时拉长到数小时；若同时决定安静，后续会进入空闲。
        /// </summary>
        public static int ResolveFollowUpMinutes(bool sleep, int requestedMinutes)
        {
            if (sleep) return 0;
            var requested = ClampMinutes(requestedMinutes);
            return requested > 0 ? requested : DefaultLongFollowUpMinutes;
        }

        /// <summary>
        /// 心跳决定不开口，且下次要等很久：不要空转心跳，进入空闲直到被激活。
        /// </summary>
        public static bool ShouldEnterIdle(bool speak, bool sleep, int requestedMinutes)
        {
            if (sleep || speak) return false;
            return ResolveFollowUpMinutes(false, requestedMinutes) >= IdleMinutesThreshold;
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
            if (KernelWakeLogic.IsSubconscious(wake) || KernelWakeLogic.IsNightResidue(wake) ||
                KernelWakeLogic.LooksLikeDailyReview(
                    source == null ? string.Empty : source.Content) ||
                NightResidueLogic.LooksLike(source == null ? string.Empty : source.Content))
                return false;
            return !IsBreaking(source, pair);
        }

        /// <summary>空闲只停心跳；她发来的话、约好的时间任务、夜间余温仍会进来。</summary>
        public static bool ShouldSkipWhileIdle(PluginEventData source, PairIdentity pair)
        {
            if (IsBreaking(source, pair)) return false;
            return IsHeartbeatOrLegacyContinue(source == null ? string.Empty : source.Content);
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

        public static string NextPlan(IMemoryStore storage, string conversationId)
        {
            if (storage == null || string.IsNullOrWhiteSpace(conversationId)) return string.Empty;
            var json = storage.LoadPluginDocument(PluginId, ScheduleDocumentKey);
            if (string.IsNullOrWhiteSpace(json)) return string.Empty;
            ScheduleFile file;
            try { file = TraceJson.FromJson<ScheduleFile>(json); }
            catch { return string.Empty; }
            if (file == null || file.items == null) return string.Empty;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var item = file.items
                .Where(x => x != null && x.enabled &&
                            string.Equals(x.conversation_id, conversationId, StringComparison.Ordinal) &&
                            IsHeartbeatOrLegacyContinue(x.content) && x.due_unix_ms > now)
                .OrderBy(x => x.due_unix_ms)
                .FirstOrDefault();
            if (item == null) return string.Empty;
            return ExtractPlan(item.content);
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
