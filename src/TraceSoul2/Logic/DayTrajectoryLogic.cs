using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using TraceSoul2.Data;
using TraceSoul2.Manager;
using TraceSoul2.Util;

namespace TraceSoul2.Logic
{
    public static class DayTrajectoryLogic
    {
        private static readonly Regex LeadingTime = new(@"^(?:\s*[\[【](?:(?:\d{4}年\d{1,2}月\d{1,2}日|\d{4}-\d{1,2}-\d{1,2})\s*)?\d{1,2}:\d{2}(?::\d{2})?[\]】]\s*)+", RegexOptions.CultureInvariant);
        public static string CleanLeadingTime(string text) => LeadingTime.Replace(text ?? string.Empty, "").Trim();
        public static string EntryText(DayTrajectoryEntryRecord entry)
            => entry.SourceMomentId?.StartsWith("legacy:", StringComparison.Ordinal) == true ? entry.Text : CleanLeadingTime(entry.Text);

        private static readonly Regex CalendarHeading = new(@"^\s*(?:(?<year>\d{4})年)?(?<month>\d{1,2})月(?<day>\d{1,2})日\s*(?:(?:周|星期)[一二三四五六日天]\s*)?(?:早晨|早上|上午|中午|下午|傍晚|晚上|深夜|凌晨|清晨)[\s，,：:]*", RegexOptions.CultureInvariant);
        public static string OverviewText(string text, long sourceTime)
        {
            text = CleanLeadingTime(text);
            var match = CalendarHeading.Match(text);
            var at = DateTimeOffset.FromUnixTimeMilliseconds(sourceTime).ToOffset(MemoryDayLogic.ChinaOffset);
            if (match.Success && int.Parse(match.Groups["month"].Value) == at.Month && int.Parse(match.Groups["day"].Value) == at.Day &&
                (!match.Groups["year"].Success || int.Parse(match.Groups["year"].Value) == at.Year) && text.Length > match.Length)
                return text.Substring(match.Length).Trim();
            return text;
        }
        public sealed class PeriodGroup
        {
            public string Time { get; set; }
            public List<string> Events { get; set; }
        }
        public static string OverviewWhen(DayTrajectoryOverviewLogic.Item item, DateTimeOffset now)
        {
            now = now.ToOffset(MemoryDayLogic.ChinaOffset);
            var start = TimeLanguageUtil.RelativeWhen(item.Start, now);
            var end = TimeLanguageUtil.RelativeWhen(item.End, now);
            string Label(string value) => value.StartsWith("今天", StringComparison.Ordinal) ? value.Substring(2) : value;
            var when = Label(start) + (end != start ? "至" + Label(end) : "");
            return when + (item.Legacy ? "（旧摘要保存时段）" : "");
        }
        public static string OverviewLine(DayTrajectoryOverviewLogic.Item item, DateTimeOffset now)
            => OverviewWhen(item, now) + " · " + (item.Legacy ? item.Text : OverviewText(item.Text, item.Start));
        public static List<PeriodGroup> OverviewGroups(IEnumerable<DayTrajectoryOverviewLogic.Item> items, DateTimeOffset now)
            => items.GroupBy(x => OverviewWhen(x, now)).Select(group => new PeriodGroup { Time = group.Key,
                Events = group.Select(x => x.Legacy ? x.Text : OverviewText(x.Text, x.Start)).Distinct(StringComparer.Ordinal).ToList() }).ToList();
        public static string FormatGroups(IEnumerable<PeriodGroup> groups)
            => string.Join("\n", groups.Select(group => group.Time + "\n" + string.Join("\n", group.Events.Select(x => "· " + x))));

        public static DayTrajectoryRecord ReadOverview(IMemoryStore store, string context, string day, DateTimeOffset? now = null)
        {
            if (store is not SqliteMemoryManager sqlite) return Read(store, context, day);
            var items = DayTrajectoryOverviewLogic.Read(sqlite, context, day);
            return items.Count == 0 ? null : new DayTrajectoryRecord { DayKey = day, UpdatedUnixMs = items.Max(x => x.End),
                Text = FormatGroups(OverviewGroups(items, now ?? DateTimeOffset.Now)) };
        }
        public static DayTrajectoryRecord Read(IMemoryStore store, string context, string day)
        {
            if (store is not SqliteMemoryManager sqlite) return store.LoadDayTrajectory(day);
            var entries = sqlite.GetDayTrajectoryEntries(context, day);
            if (entries.Count == 0) return null;
            return new DayTrajectoryRecord { DayKey = day, UpdatedUnixMs = entries.Max(x => x.CreatedUnixMs),
                Text = string.Join("\n", entries.Select(x => "[" + DateTimeOffset.FromUnixTimeMilliseconds(x.CreatedUnixMs)
                    .ToOffset(MemoryDayLogic.ChinaOffset).ToString("HH:mm") + "] " + EntryText(x))) };
        }
    }
}
