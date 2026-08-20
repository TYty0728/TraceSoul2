using System;
using System.Collections.Generic;

namespace TraceSoul2.Migrate
{
    /// <summary>
    /// 移植自老 TraceSoul（framework/lifecycle/core/time/）：
    /// 代码确定性计算时间关系与日内时段，翻译成固定中文枚举，不经过 LLM。
    /// 回放与测试得到完全相同的表达。
    /// </summary>
    public enum DayPeriod
    {
        BeforeDawn,     // 凌晨 ≤4
        Dawn,           // 清晨 5-6
        EarlyMorning,   // 早上 7-8
        Morning,        // 上午 9-11
        Noon,           // 中午 12
        Afternoon,      // 下午 13-16
        Dusk,           // 傍晚 17-18
        Evening,        // 晚上 19-22
        LateNight       // 深夜 ≥23
    }

    public enum DayRelation
    {
        DayBeforeYesterday,
        Yesterday,
        Today,
        Tomorrow,
        DayAfterTomorrow
    }

    public enum DayKind
    {
        Workday,
        Weekend,
        Holiday
    }

    public static class TimeLanguage
    {
        public static DayPeriod PeriodOf(DateTimeOffset timestamp)
        {
            var hour = timestamp.Hour;
            if (hour <= 4) return DayPeriod.BeforeDawn;
            if (hour <= 6) return DayPeriod.Dawn;
            if (hour <= 8) return DayPeriod.EarlyMorning;
            if (hour <= 11) return DayPeriod.Morning;
            if (hour == 12) return DayPeriod.Noon;
            if (hour <= 16) return DayPeriod.Afternoon;
            if (hour <= 18) return DayPeriod.Dusk;
            if (hour <= 22) return DayPeriod.Evening;
            return DayPeriod.LateNight;
        }

        public static DayRelation? RelationOf(DateTimeOffset target, DateTimeOffset reference)
        {
            var delta = (target.Date - reference.Date).Days;
            switch (delta)
            {
                case -2: return DayRelation.DayBeforeYesterday;
                case -1: return DayRelation.Yesterday;
                case 0: return DayRelation.Today;
                case 1: return DayRelation.Tomorrow;
                case 2: return DayRelation.DayAfterTomorrow;
                default: return null;
            }
        }

        public static DayKind KindOf(DateTimeOffset timestamp, ISet<DateTime> holidays = null)
        {
            if (holidays != null && holidays.Contains(timestamp.Date)) return DayKind.Holiday;
            return timestamp.DayOfWeek == DayOfWeek.Saturday || timestamp.DayOfWeek == DayOfWeek.Sunday
                ? DayKind.Weekend
                : DayKind.Workday;
        }

        public static string PeriodZh(DayPeriod period)
        {
            switch (period)
            {
                case DayPeriod.BeforeDawn: return "凌晨";
                case DayPeriod.Dawn: return "清晨";
                case DayPeriod.EarlyMorning: return "早上";
                case DayPeriod.Morning: return "上午";
                case DayPeriod.Noon: return "中午";
                case DayPeriod.Afternoon: return "下午";
                case DayPeriod.Dusk: return "傍晚";
                case DayPeriod.Evening: return "晚上";
                default: return "深夜";
            }
        }

        public static string RelationZh(DayRelation relation)
        {
            switch (relation)
            {
                case DayRelation.DayBeforeYesterday: return "前天";
                case DayRelation.Yesterday: return "昨天";
                case DayRelation.Today: return "今天";
                case DayRelation.Tomorrow: return "明天";
                default: return "后天";
            }
        }

        public static string DayKindZh(DayKind kind)
        {
            switch (kind)
            {
                case DayKind.Workday: return "工作日";
                case DayKind.Weekend: return "周末";
                default: return "假日";
            }
        }

        /// <summary>时间维度的确定性标签：如「今天下午」「2026年2月16日早上」。</summary>
        public static string TimeLabel(DateTimeOffset timestamp, DateTimeOffset reference)
        {
            var relation = RelationOf(timestamp, reference);
            var day = relation.HasValue
                ? RelationZh(relation.Value)
                : timestamp.Year + "年" + timestamp.Month + "月" + timestamp.Day + "日";
            return day + PeriodZh(PeriodOf(timestamp));
        }

        /// <summary>日类型标签（工作日/周末/假日）。</summary>
        public static string DayKindLabel(DateTimeOffset timestamp, ISet<DateTime> holidays = null)
        {
            return DayKindZh(KindOf(timestamp, holidays));
        }
    }
}
