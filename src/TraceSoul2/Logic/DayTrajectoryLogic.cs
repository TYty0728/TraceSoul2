using System;
using System.Linq;
using System.Text.RegularExpressions;
using TraceSoul2.Data;
using TraceSoul2.Manager;

namespace TraceSoul2.Logic
{
    public static class DayTrajectoryLogic
    {
        private static readonly Regex LeadingTime = new(@"^(?:\s*[\[【](?:(?:\d{4}年\d{1,2}月\d{1,2}日|\d{4}-\d{1,2}-\d{1,2})\s*)?\d{1,2}:\d{2}(?::\d{2})?[\]】]\s*)+", RegexOptions.CultureInvariant);
        public static string CleanLeadingTime(string text) => LeadingTime.Replace(text ?? string.Empty, "").Trim();
        public static string EntryText(DayTrajectoryEntryRecord entry)
            => entry.SourceMomentId?.StartsWith("legacy:", StringComparison.Ordinal) == true ? entry.Text : CleanLeadingTime(entry.Text);

        public static DayTrajectoryRecord ReadOverview(IMemoryStore store, string context, string day)
        {
            if (store is not SqliteMemoryManager sqlite) return Read(store, context, day);
            var items = DayTrajectoryOverviewLogic.Read(sqlite, context, day);
            string Time(long value) => DateTimeOffset.FromUnixTimeMilliseconds(value).ToOffset(MemoryDayLogic.ChinaOffset).ToString("MM-dd HH:mm");
            return items.Count == 0 ? null : new DayTrajectoryRecord { DayKey = day, UpdatedUnixMs = items.Max(x => x.End),
                Text = string.Join("\n", items.Select(x => "[" + Time(x.Start) + (x.End > x.Start ? "～" + Time(x.End) : "") + "] " + x.Text)) };
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
