using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TraceSoul2.Data;
using TraceSoul2.Manager;
using TraceSoul2.Prompts;
using TraceSoul2.Util;

namespace TraceSoul2.Logic
{
    /// <summary>
    /// 日终复盘成功后的夜间余温：有当天相处才开口，且只在后半夜窗口发给她。
    /// 不是复盘报告，也不叫醒睡着的心智。
    /// </summary>
    public static class NightResidueLogic
    {
        public const string PluginId = "night.residue";
        public const string DocumentKey = "days";
        public const string ActionSpeak = "speak";
        public const string ActionSkipWindow = "skip_window";
        public const string ActionSkipEmpty = "skip_empty";
        public const string ActionSkipHandled = "skip_handled";
        public const string ActionSkipNotClosed = "skip_not_closed";
        public const string StatusSent = "sent";
        public const string StatusSilent = "silent";
        public const string StatusSkipped = "skipped";
        public const int WindowEndHour = 9;
        public const int WindowEndMinute = 30;
        public const int ReplyCharCap = 500;
        public const int EventCap = 8;

        public static bool LooksLike(string content)
        {
            return (content ?? string.Empty).IndexOf(CorePrompts.NightResidue.ContentPrefix,
                StringComparison.Ordinal) >= 0;
        }

        public static string BuildContent(string dayKey)
        {
            return CorePrompts.NightResidue.ContentPrefix + (dayKey ?? string.Empty).Trim();
        }

        public static string DayKeyFromContent(string content)
        {
            var value = (content ?? string.Empty).Trim();
            var prefix = CorePrompts.NightResidue.ContentPrefix;
            if (!value.StartsWith(prefix, StringComparison.Ordinal)) return string.Empty;
            return value.Substring(prefix.Length).Trim();
        }

        public static bool InSpeakWindow(DateTimeOffset now)
        {
            var local = now.ToOffset(MemoryDayLogic.ChinaOffset);
            var start = MemoryDayLogic.CurrentStart(local);
            var end = start.AddHours(WindowEndHour - MemoryDayLogic.BoundaryHour)
                .AddMinutes(WindowEndMinute);
            return local >= start && local < end;
        }

        public static NightResidueDecision Evaluate(
            IMemoryStore store,
            string conversationId,
            string dayKey,
            DateTimeOffset now)
        {
            dayKey = (dayKey ?? string.Empty).Trim();
            DateTimeOffset dayStart;
            if (!MemoryDayLogic.TryStartOf(dayKey, out dayStart))
            {
                return new NightResidueDecision
                {
                    Action = ActionSkipNotClosed,
                    Reason = "日期无效",
                    DayKey = dayKey
                };
            }
            if (HasHandled(store, dayKey))
            {
                return new NightResidueDecision
                {
                    Action = ActionSkipHandled,
                    Reason = "这一天的夜里的话已经处理过",
                    DayKey = dayKey
                };
            }
            if (!string.Equals(dayKey, MemoryDayLogic.ClosedDayKey(now), StringComparison.Ordinal))
            {
                return new NightResidueDecision
                {
                    Action = ActionSkipNotClosed,
                    Reason = "只给刚合上的那天留夜里的话",
                    DayKey = dayKey
                };
            }
            if (!InSpeakWindow(now))
            {
                return new NightResidueDecision
                {
                    Action = ActionSkipWindow,
                    Reason = "已经过了后半夜窗口",
                    DayKey = dayKey,
                    RememberStatus = StatusSkipped
                };
            }
            var seed = LoadSeed(store, conversationId, dayKey);
            if (!seed.HasWarmth)
            {
                return new NightResidueDecision
                {
                    Action = ActionSkipEmpty,
                    Reason = "这一天没有相处，不硬留话",
                    DayKey = dayKey,
                    Seed = seed,
                    RememberStatus = StatusSkipped
                };
            }
            return new NightResidueDecision
            {
                Action = ActionSpeak,
                Reason = "有余温，夜里开口",
                DayKey = dayKey,
                Seed = seed
            };
        }

        public static NightResidueSeed LoadSeed(IMemoryStore store, string conversationId, string dayKey)
        {
            var seed = new NightResidueSeed { DayKey = (dayKey ?? string.Empty).Trim() };
            if (store == null || !MemoryDayLogic.TryStartOf(seed.DayKey, out var start))
                return seed;
            var startMs = start.ToUnixTimeMilliseconds();
            var endMs = start.AddDays(1).ToUnixTimeMilliseconds();
            var events = store.GetActiveEventIndexes()
                .Where(x => x != null && x.TimeUnixMs >= startMs && x.TimeUnixMs < endMs &&
                            !string.IsNullOrWhiteSpace(x.EventSummary))
                .OrderBy(x => x.TimeUnixMs)
                .ToList();
            // 横跨当天取材，避免只看到最早几件事或睡前的最后一拍。
            seed.Events = Enumerable.Range(0, Math.Min(EventCap, events.Count))
                .Select(i => events.Count <= EventCap ? events[i] : events[i * (events.Count - 1) / (EventCap - 1)])
                .Select(x => OneLine(x.EventSummary, 240))
                .Where(x => x.Length > 0)
                .ToList();
            seed.Trajectory = OneLine(DayTrajectoryLogic.Read(store, conversationId, seed.DayKey)?.Text, 1800);
            if (store is SqliteMemoryManager sqlite && !string.IsNullOrWhiteSpace(conversationId))
            {
                var slices = sqlite.GetRuntimeSlices(conversationId, seed.DayKey)
                    .Where(x => x.ContextConversationId == conversationId && x.MemoryVisibility == "private").ToList();
                var pair = store.LoadPairIdentity();
                var dialogue = slices.Select(x => x.MomentId).Chunk(256).SelectMany(sqlite.GetEvidenceMoments)
                    .Where(x => pair.IsHumanMoment(x.Role)).Select(x => x.Id).ToHashSet();
                var notes = slices.Where(x => dialogue.Contains(x.MomentId))
                    .Select(x => new { x.CreatedUnixMs, Text = OneLine(PuzzleViewLogic.Read<DayThought>(x.SnapshotJson)?.Inner, 120) })
                    .Where(x => x.Text.Length > 0).DistinctBy(x => x.Text).ToList();
                const int cap = 6;
                seed.Thoughts = Enumerable.Range(0, Math.Min(cap, notes.Count))
                    .Select(i => notes.Count <= cap ? notes[i] : notes[i * (notes.Count - 1) / (cap - 1)])
                    .Select(x => DateTimeOffset.FromUnixTimeMilliseconds(x.CreatedUnixMs).ToOffset(MemoryDayLogic.ChinaOffset)
                        .ToString("HH:mm") + " " + x.Text).ToList();
            }
            return seed;
        }

        private sealed class DayThought { public string Inner { get; set; } }

        public static PluginEventData CreateTrigger(string conversationId, string dayKey, string traceId)
        {
            return new PluginEventData
            {
                PluginId = PluginId,
                ConversationId = conversationId,
                ExternalEventId = "night-residue:" + (dayKey ?? string.Empty).Trim(),
                Role = "system_event",
                Content = BuildContent(dayKey),
                Realm = TraceRealmValues.Meta,
                EvidenceType = EvidenceTypeValues.PluginObserved,
                IsOperational = true,
                Wake = KernelWakeValues.NightResidue,
                Breaking = false,
                TraceId = traceId,
                PayloadJson = "{\"day\":\"" + EscapeJson((dayKey ?? string.Empty).Trim()) +
                              "\",\"wake\":\"" + KernelWakeValues.NightResidue + "\"}",
                OccurredUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
        }

        public static bool IsSilentReply(string text)
        {
            var value = (text ?? string.Empty).Trim();
            if (value.Length == 0) return true;
            value = value.Trim('（', '）', '(', ')', '。', '.', '！', '!', '～', '~', ' ');
            return value == "无" || value == "静默" || value == "不说";
        }

        public static string LimitReply(string text)
        {
            var value = (text ?? string.Empty).Trim();
            if (value.Length <= ReplyCharCap) return value;
            throw new InvalidOperationException("夜间余温压缩后仍超长，未截断或发送。");
        }

        public static bool HasHandled(IMemoryStore store, string dayKey)
        {
            return !string.IsNullOrWhiteSpace(StatusOf(store, dayKey));
        }

        public static string StatusOf(IMemoryStore store, string dayKey)
        {
            dayKey = (dayKey ?? string.Empty).Trim();
            if (store == null || dayKey.Length == 0) return string.Empty;
            var log = LoadLog(store);
            var item = (log.days ?? new List<NightResidueDayData>())
                .FirstOrDefault(x => x != null &&
                                     string.Equals(x.day, dayKey, StringComparison.Ordinal));
            return item == null ? string.Empty : (item.status ?? string.Empty).Trim();
        }

        public static void Remember(IMemoryStore store, string dayKey, string status)
        {
            if (store == null) return;
            dayKey = (dayKey ?? string.Empty).Trim();
            status = (status ?? string.Empty).Trim();
            if (dayKey.Length == 0 || status.Length == 0) return;
            var log = LoadLog(store);
            log.days = log.days ?? new List<NightResidueDayData>();
            var item = log.days.FirstOrDefault(x => x != null &&
                string.Equals(x.day, dayKey, StringComparison.Ordinal));
            if (item == null)
            {
                item = new NightResidueDayData { day = dayKey };
                log.days.Add(item);
            }
            item.status = status;
            item.at_unix_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            store.SavePluginDocument(PluginId, DocumentKey, TraceJson.ToJson(log));
        }

        private static NightResidueLogData LoadLog(IMemoryStore store)
        {
            var json = store.LoadPluginDocument(PluginId, DocumentKey);
            if (string.IsNullOrWhiteSpace(json)) return new NightResidueLogData();
            try
            {
                return TraceJson.FromJson<NightResidueLogData>(json) ?? new NightResidueLogData();
            }
            catch
            {
                return new NightResidueLogData();
            }
        }

        private static string OneLine(string value, int max)
        {
            value = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (value.Length <= max) return value;
            return value.Substring(0, max).TrimEnd();
        }

        private static string EscapeJson(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }

    public sealed class NightResidueDecision
    {
        public string Action;
        public string Reason;
        public string DayKey;
        public NightResidueSeed Seed;
        public string RememberStatus;

        public bool ShouldSpeak
        {
            get { return Action == NightResidueLogic.ActionSpeak; }
        }
    }

    public sealed class NightResidueSeed
    {
        public string DayKey;
        public string Trajectory;
        public List<string> Thoughts = new List<string>();
        public List<string> Events = new List<string>();

        public bool HasWarmth
        {
            get { return Events != null && Events.Count > 0; }
        }

        public string FormatForPrompt()
        {
            var builder = new StringBuilder();
            builder.AppendLine(CorePrompts.NightResidue.SeedHeader);
            builder.Append(CorePrompts.NightResidue.DayPrefix).AppendLine(DayKey ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(Trajectory))
                builder.AppendLine("当天相处的脉络（供回想，不逐项复述）：").AppendLine(Trajectory);
            if (Events != null && Events.Count > 0)
            {
                builder.AppendLine(CorePrompts.NightResidue.EventsPrefix);
                foreach (var item in Events)
                    builder.Append("- ").AppendLine(item);
            }
            if (Thoughts.Count > 0)
            {
                builder.AppendLine("【这一天聊天时我一闪而过的念头】");
                foreach (var thought in Thoughts) builder.AppendLine(thought);
            }
            return builder.ToString().TrimEnd();
        }
    }

    [Serializable]
    public sealed class NightResidueLogData
    {
        public List<NightResidueDayData> days = new List<NightResidueDayData>();
    }

    [Serializable]
    public sealed class NightResidueDayData
    {
        public string day;
        public string status;
        public long at_unix_ms;
    }
}
