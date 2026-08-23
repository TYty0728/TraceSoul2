using System;
using System.Collections.Generic;

namespace TraceSoul2.Util
{
    /// <summary>
    /// 时间自然语言翻译（与迁移管线 TimeLanguage 同一张表）：
    /// 代码确定性计算日内时段 / 工作日 / 周几，翻译成固定中文，不经过 LLM。
    /// 数据入库仍记录精确时间戳；这里只负责注入给 Brain 的自然语言时间感。
    /// </summary>
    public static class TimeLanguageUtil
    {
        public static string PeriodZh(DateTimeOffset timestamp)
        {
            var hour = timestamp.Hour;
            if (hour <= 4) return "凌晨";
            if (hour <= 6) return "清晨";
            if (hour <= 8) return "早上";
            if (hour <= 11) return "上午";
            if (hour == 12) return "中午";
            if (hour <= 16) return "下午";
            if (hour <= 18) return "傍晚";
            if (hour <= 22) return "晚上";
            return "深夜";
        }

        public static string DayKindZh(DateTimeOffset timestamp, ISet<DateTime> holidays = null)
        {
            if (holidays != null && holidays.Contains(timestamp.Date)) return "假日";
            return timestamp.DayOfWeek == DayOfWeek.Saturday || timestamp.DayOfWeek == DayOfWeek.Sunday
                ? "周末"
                : "工作日";
        }

        public static string WeekZh(DateTimeOffset timestamp)
        {
            switch (timestamp.DayOfWeek)
            {
                case DayOfWeek.Monday: return "周一";
                case DayOfWeek.Tuesday: return "周二";
                case DayOfWeek.Wednesday: return "周三";
                case DayOfWeek.Thursday: return "周四";
                case DayOfWeek.Friday: return "周五";
                case DayOfWeek.Saturday: return "周六";
                default: return "周日";
            }
        }

        /// <summary>如：2026年8月16日（周日·周末）下午 6点28分。</summary>
        public static string NaturalNow(DateTimeOffset now)
        {
            var minute = now.Minute;
            var timeText = minute == 0 ? now.Hour + "点整" : now.Hour + "点" + minute + "分";
            return now.Year + "年" + now.Month + "月" + now.Day + "日（" +
                   WeekZh(now) + "·" + DayKindZh(now) + "）" + PeriodZh(now) + " " + timeText;
        }

        public static string ElapsedZh(long fromUnixMs, long toUnixMs)
        {
            if (fromUnixMs <= 0 || toUnixMs <= fromUnixMs) return "刚才";
            var minutes = (toUnixMs - fromUnixMs) / 60000L;
            if (minutes < 2) return "刚才";
            if (minutes < 60) return minutes + "分钟";
            var hours = minutes / 60L;
            if (hours < 24)
            {
                var rest = minutes % 60L;
                return rest == 0 ? hours + "小时" : hours + "小时" + rest + "分钟";
            }
            var days = hours / 24L;
            return days + "天" + (hours % 24L == 0 ? string.Empty : (hours % 24L) + "小时");
        }
    }
}
