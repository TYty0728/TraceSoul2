using System;
using System.Globalization;

namespace TraceSoul2.Logic
{
    /// <summary>
    /// 记忆日：每天 04:00（+08:00）切日。04:00 前的时刻归前一天。
    /// 日构建要跑的是刚结束的那天，不是 now 所在的、刚开始的那天。
    /// </summary>
    public static class MemoryDayLogic
    {
        public static readonly TimeSpan ChinaOffset = TimeSpan.FromHours(8);
        public const int BoundaryHour = 4;

        /// <summary>当前尚未结束的记忆日起点（该日 04:00）。</summary>
        public static DateTimeOffset CurrentStart(DateTimeOffset now)
        {
            var local = now.ToOffset(ChinaOffset);
            var start = new DateTimeOffset(local.Date.AddHours(BoundaryHour), ChinaOffset);
            if (local < start) start = start.AddDays(-1);
            return start;
        }

        /// <summary>now 落在哪一个记忆日（进行中的那天）。</summary>
        public static string CurrentDayKey(DateTimeOffset now)
        {
            return CurrentStart(now).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        /// <summary>刚合上、应被日构建的记忆日。04:00 边界上取前一天。</summary>
        public static string ClosedDayKey(DateTimeOffset now)
        {
            return CurrentStart(now).AddDays(-1)
                .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
    }
}
