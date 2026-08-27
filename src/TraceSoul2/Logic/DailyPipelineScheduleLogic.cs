using System;
using System.Collections.Generic;
using System.Linq;

namespace TraceSoul2.Logic
{
    /// <summary>
    /// 日构建唤醒：按墙钟判断该不该补跑，单次等待最多一分钟。
    /// 一次 Delay 到下一个 04:00 在 Windows 睡眠时会暂停，睡过边界就再也等不到；
    /// 失败后如果再去等明天 04:00，当天也就补不回来。
    /// </summary>
    public static class DailyPipelineScheduleLogic
    {
        public static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);
        public static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(5);

        public static bool ShouldCatchUp(
            DateTimeOffset now,
            string lastSucceededClosedDay,
            bool lastAttemptFailed,
            IEnumerable<string> unbuiltDaysBeforeCurrentStart)
        {
            if (lastAttemptFailed) return true;
            var closed = MemoryDayLogic.ClosedDayKey(now);
            if (!string.Equals((lastSucceededClosedDay ?? string.Empty).Trim(), closed,
                    StringComparison.Ordinal))
                return true;
            return (unbuiltDaysBeforeCurrentStart ?? Enumerable.Empty<string>())
                .Any(x => !string.IsNullOrWhiteSpace(x) &&
                          string.Compare(x.Trim(), closed, StringComparison.Ordinal) <= 0);
        }

        /// <summary>离下一个 04:00 再远，也最多睡一分钟再看墙钟；失败则五分钟后重试。</summary>
        public static TimeSpan NextWait(DateTimeOffset now, bool lastAttemptFailed)
        {
            if (lastAttemptFailed) return RetryInterval;
            var local = now.ToOffset(MemoryDayLogic.ChinaOffset);
            var until = MemoryDayLogic.CurrentStart(local).AddDays(1) - local;
            if (until <= TimeSpan.Zero) return PollInterval;
            return until > PollInterval ? PollInterval : until;
        }
    }
}
