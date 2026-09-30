using System;
using System.Linq;
using TraceSoul2.Data;
using TraceSoul2.Manager;

namespace TraceSoul2.Logic
{
    public static class DayTrajectoryLogic
    {
        public static DayTrajectoryRecord Read(IMemoryStore store, string context, string day)
        {
            if (store is not SqliteMemoryManager sqlite) return store.LoadDayTrajectory(day);
            var entries = sqlite.GetDayTrajectoryEntries(context, day);
            if (entries.Count == 0) return null;
            return new DayTrajectoryRecord { DayKey = day, UpdatedUnixMs = entries.Max(x => x.CreatedUnixMs),
                Text = string.Join("\n", entries.Select(x => "[" + DateTimeOffset.FromUnixTimeMilliseconds(x.CreatedUnixMs)
                    .ToOffset(MemoryDayLogic.ChinaOffset).ToString("HH:mm") + "] " + x.Text)) };
        }
    }
}
