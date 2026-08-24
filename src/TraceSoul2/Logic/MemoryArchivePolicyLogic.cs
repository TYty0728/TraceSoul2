using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using TraceSoul2.Data;
using TraceSoul2.Manager;
using TraceSoul2.Util;

namespace TraceSoul2.Logic
{
    /// <summary>
    /// 小复盘的确定性闸门。Mind 只提供“话题已经放下”的信号，
    /// 是否真的启动第三次 LLM 由累计量与明确记忆指令共同决定。
    /// </summary>
    public static class MemoryArchivePolicyLogic
    {
        private const string PluginId = "builtin.memory";
        private const string CursorDocumentPrefix = "archive-cursor:";

        /// <summary>20 轮来回约等于 40 条双方 Moment。</summary>
        public const int SoftDialogueMomentThreshold = 40;

        /// <summary>30 轮来回仍未遇到自然边界时，强制做一次小复盘。</summary>
        public const int HardDialogueMomentThreshold = 60;

        public sealed class GateResult
        {
            public bool ShouldArchive { get; internal set; }
            public bool ExplicitRequest { get; internal set; }
            public bool ForcedByBacklog { get; internal set; }
            public bool TopicBoundarySuggested { get; internal set; }
            public int UnbuiltDialogueMoments { get; internal set; }
            public string Reason { get; internal set; }
        }

        [Serializable]
        private sealed class ArchiveCursorData
        {
            public long last_archived_unix_ms;
        }

        public static GateResult Evaluate(
            MindDecisionData mind,
            MomentRecord currentMoment,
            IEnumerable<MomentRecord> recentMoments,
            PairIdentity pair,
            long afterUnixMs = 0)
        {
            pair = pair ?? PairIdentity.Missing;
            var window = SelectArchiveWindow(
                recentMoments, currentMoment, pair, int.MaxValue, afterUnixMs);
            var count = window.Count;
            var explicitRequest = IsExplicitMemoryRequest(currentMoment == null
                ? string.Empty
                : currentMoment.Content);
            var boundary = mind != null && mind.archive;
            var forced = count >= HardDialogueMomentThreshold;
            var shouldArchive = explicitRequest || forced ||
                                (boundary && count >= SoftDialogueMomentThreshold);

            return new GateResult
            {
                ShouldArchive = shouldArchive,
                ExplicitRequest = explicitRequest,
                ForcedByBacklog = forced,
                TopicBoundarySuggested = boundary,
                UnbuiltDialogueMoments = count,
                Reason = explicitRequest
                    ? "对方明确要求记住"
                    : forced
                        ? "未复盘对话达到硬上限"
                        : boundary && count >= SoftDialogueMomentThreshold
                            ? "话题已放下且达到小复盘门槛"
                            : boundary
                                ? "话题边界已记录，累计量尚未达到门槛"
                                : "继续累计"
            };
        }

        public static List<MomentRecord> SelectArchiveWindow(
            IEnumerable<MomentRecord> recentMoments,
            MomentRecord currentMoment,
            PairIdentity pair,
            int take = HardDialogueMomentThreshold,
            long afterUnixMs = 0)
        {
            pair = pair ?? PairIdentity.Missing;
            var byId = new Dictionary<string, MomentRecord>(StringComparer.Ordinal);
            foreach (var moment in (recentMoments ?? Enumerable.Empty<MomentRecord>())
                         .Concat(currentMoment == null
                             ? Enumerable.Empty<MomentRecord>()
                             : new[] { currentMoment }))
            {
                if (moment == null || !IsUnbuiltDialogueMoment(moment, pair)) continue;
                if (moment.CreatedUnixMs <= afterUnixMs) continue;
                var key = string.IsNullOrWhiteSpace(moment.Id)
                    ? "at:" + moment.CreatedUnixMs + ":" + (moment.Role ?? string.Empty) + ":" +
                      (moment.Content ?? string.Empty)
                    : moment.Id;
                byId[key] = moment;
            }

            var ordered = byId.Values
                .OrderBy(x => x.CreatedUnixMs)
                .ThenBy(x => x.Id ?? string.Empty, StringComparer.Ordinal)
                .ToList();
            if (take <= 0 || ordered.Count <= take) return ordered;
            return ordered.Skip(ordered.Count - take).ToList();
        }

        /// <summary>
        /// 游标将“本次小复盘之后的新对话”与历史未构筑数据隔开。
        /// 旧数据库第一次启用时以最近事件索引为基线，避免把迁移遗留一次次补跑。
        /// </summary>
        public static long LoadArchiveCursor(IMemoryStore storage, string conversationId)
        {
            if (storage == null) return 0;
            try
            {
                var raw = storage.LoadPluginDocument(
                    PluginId, CursorDocumentPrefix + (conversationId ?? string.Empty));
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    var state = TraceJson.FromJson<ArchiveCursorData>(raw);
                    if (state != null && state.last_archived_unix_ms > 0)
                        return state.last_archived_unix_ms;
                }
            }
            catch
            {
                // 损坏的可派生游标不应阻断对话；下面用已有事件索引兜底。
            }

            try
            {
                var bootstrap = (storage.GetActiveEventIndexes() ?? new List<EventIndexRecord>())
                    .Where(x => x != null)
                    .Select(x => x.CreatedUnixMs)
                    .DefaultIfEmpty(0)
                    .Max();
                if (bootstrap > 0)
                    SaveArchiveCursor(storage, conversationId, bootstrap);
                return bootstrap;
            }
            catch
            {
                return 0;
            }
        }

        public static void SaveArchiveCursor(
            IMemoryStore storage,
            string conversationId,
            long lastArchivedUnixMs)
        {
            if (storage == null || lastArchivedUnixMs <= 0) return;
            storage.SavePluginDocument(
                PluginId,
                CursorDocumentPrefix + (conversationId ?? string.Empty),
                TraceJson.ToJson(new ArchiveCursorData
                {
                    last_archived_unix_ms = lastArchivedUnixMs
                }));
        }

        public static bool IsExplicitMemoryRequest(string content)
        {
            var text = (content ?? string.Empty).Trim();
            if (text.Length == 0) return false;
            return Regex.IsMatch(text,
                @"(?:帮我|替我|给我|请你)?记(?:一下|下来|着|住)|你要记住|请记住|以后(?:别|不要)忘|别忘(?:了)?|记到(?:心里|记忆里)",
                RegexOptions.CultureInvariant);
        }

        private static bool IsUnbuiltDialogueMoment(MomentRecord moment, PairIdentity pair)
        {
            if (string.Equals(moment.MemoryStatus, "built", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(moment.MemoryStatus, "operational", StringComparison.OrdinalIgnoreCase))
                return false;
            if (string.IsNullOrWhiteSpace(moment.Content)) return false;
            var role = (moment.Role ?? string.Empty).Trim();
            return pair.IsHumanMoment(role) || pair.IsCompanionMoment(role) ||
                   string.Equals(role, "user", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase);
        }
    }
}
