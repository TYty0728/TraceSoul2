using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using TraceSoul2.Data;

namespace TraceSoul2.Logic
{
    /// <summary>时间阶梯的名额。记忆页用完整名额，runtime 注入用更短的名额。</summary>
    public static class LadderContextLogic
    {
        public static int Capacity(string tier) => tier switch
        {
            "day" => 10,
            "week" => 3,
            "month" => 3,
            "year" => 3,
            "forever" => 3,
            _ => 0
        };

        /// <summary>runtime 注入名额。日榜 5 条，周、月、年、永久各 2 条。页面仍按完整名额展示。</summary>
        public static int InjectCount(string tier) => tier switch
        {
            "day" => 5,
            "week" => 2,
            "month" => 2,
            "year" => 2,
            "forever" => 2,
            _ => 0
        };

        /// <summary>昨天的日榜，以及不晚于当前记忆日的最新周、月、年榜和永久榜。空榜不出现。</summary>
        public static IReadOnlyList<LadderBoard> Boards(IEnumerable<LadderItemRecord> items, DateTimeOffset now)
            => Boards(items, now, Capacity);

        private static IReadOnlyList<LadderBoard> Boards(IEnumerable<LadderItemRecord> items, DateTimeOffset now, Func<string, int> capacity)
        {
            var all = (items ?? Array.Empty<LadderItemRecord>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Label)).ToList();
            var start = MemoryDayLogic.CurrentStart(now);
            var boards = new List<LadderBoard>();
            Add(boards, "昨天", all, "day", MemoryDayLogic.ClosedDayKey(now), exact: true, capacity);
            Add(boards, "本周", all, "week", MondayOf(start.DateTime).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), exact: false, capacity);
            Add(boards, "本月", all, "month", start.ToString("yyyy-MM", CultureInfo.InvariantCulture), exact: false, capacity);
            Add(boards, "本年", all, "year", start.ToString("yyyy", CultureInfo.InvariantCulture), exact: false, capacity);
            Add(boards, "永久", all, "forever", "forever", exact: true, capacity);
            return boards;
        }

        /// <summary>runtime 注入的榜单正文。只写序号和摘要，不写上榜理由，并按注入名额截短。</summary>
        public static string Reading(IEnumerable<LadderItemRecord> items, DateTimeOffset now)
        {
            var builder = new StringBuilder();
            foreach (var board in Boards(items, now, InjectCount))
            {
                if (builder.Length > 0) builder.Append('\n');
                builder.Append(board.Title).Append('\n');
                WriteKind(builder, board.Items, "event", string.Empty);
                WriteKind(builder, board.Items, "cognition", "认知");
            }
            return builder.ToString().TrimEnd();
        }

        private static void Add(List<LadderBoard> boards, string title, List<LadderItemRecord> all, string tier, string period, bool exact, Func<string, int> capacity)
        {
            var cap = capacity(tier);
            if (cap <= 0) return;
            var pool = all.Where(x => string.Equals(x.Tier, tier, StringComparison.Ordinal)).ToList();
            if (pool.Count == 0) return;
            var key = exact
                ? period
                : pool.Select(x => x.PeriodKey ?? string.Empty)
                    .Where(x => x.Length > 0 && string.CompareOrdinal(x, period) <= 0)
                    .OrderByDescending(x => x, StringComparer.Ordinal)
                    .FirstOrDefault();
            if (string.IsNullOrEmpty(key)) return;
            var chosen = pool.Where(x => x.PeriodKey == key).ToList();
            var rows = TakeKind(chosen, "event", cap).Concat(TakeKind(chosen, "cognition", cap)).ToList();
            if (rows.Count == 0) return;
            boards.Add(new LadderBoard { Title = title, Tier = tier, PeriodKey = key, Items = rows });
        }

        private static List<LadderItemRecord> TakeKind(List<LadderItemRecord> chosen, string kind, int cap)
            => chosen.Where(x => Kind(x) == kind).OrderBy(x => x.Rank).ThenBy(x => x.RefId).Take(cap).ToList();

        private static void WriteKind(StringBuilder builder, IReadOnlyList<LadderItemRecord> chosen, string kind, string heading)
        {
            var rows = chosen.Where(x => Kind(x) == kind).ToList();
            if (rows.Count == 0) return;
            if (heading.Length > 0) builder.Append(heading).Append('\n');
            for (var i = 0; i < rows.Count; i++)
                builder.Append(i + 1).Append(". ").Append(rows[i].Label.Trim()).Append('\n');
        }

        private static string Kind(LadderItemRecord item)
            => string.IsNullOrWhiteSpace(item.ListKind) || item.ListKind == "event" ? "event" : item.ListKind;

        private static DateTime MondayOf(DateTime day)
        {
            var offset = ((int)day.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
            return day.Date.AddDays(-offset);
        }
    }

    /// <summary>当前可见的一层榜：事件在前，认知在后，都已按名额截取。</summary>
    public sealed class LadderBoard
    {
        public string Title { get; set; } = string.Empty;
        public string Tier { get; set; } = string.Empty;
        public string PeriodKey { get; set; } = string.Empty;
        public IReadOnlyList<LadderItemRecord> Items { get; set; } = Array.Empty<LadderItemRecord>();
    }
}
