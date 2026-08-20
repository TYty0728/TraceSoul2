using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TraceSoul2.Migrate
{
    /// <summary>
    /// 全量自动循环：导入全部日志 → 分类 → 按记忆日逐天循环构筑
    /// （一天一导入 moment → 结构性记忆入库 → 当天复盘卡片/内心 → 当天日榜），
    /// 空天也走当天循环。每步进度追加写入 full_run_report.txt。
    /// </summary>
    public static class FullLoopRunner
    {
        public static async Task<int> RunAsync(MigrationContext context, string[] args)
        {
            var log = CliArgs.Value(args, "--log");
            if (string.IsNullOrWhiteSpace(log) || !File.Exists(log))
                throw new InvalidOperationException("需要 --log <full_log.txt>。");
            var range = DateRange.Parse(args);

            var rangeArgs = new List<string>();
            if (range.From.HasValue) { rangeArgs.Add("--from"); rangeArgs.Add(range.From.Value.ToString("yyyy-MM-dd")); }
            if (range.To.HasValue) { rangeArgs.Add("--to"); rangeArgs.Add(range.To.Value.ToString("yyyy-MM-dd")); }

            var report = new StringBuilder();
            void Log(string line)
            {
                Console.WriteLine(line);
                report.AppendLine(DateTimeOffset.Now.ToString("HH:mm:ss") + " " + line);
                File.WriteAllText(
                    Path.Combine(context.DataDirectory, "full_run_report.txt"),
                    report.ToString(), Encoding.UTF8);
            }

            // ① 导入（幂等游标，断点续传）
            Log("========== ① 导入全部日志 ==========");
            var importArgs = new List<string> { "--log", log };
            importArgs.AddRange(rangeArgs);
            await FullLogImporter.RunAsync(context, importArgs.ToArray());

            // ② Realm 分类
            Log("========== ② 批量分类 ==========");
            await RealmClassifier.RunAsync(context, rangeArgs.ToArray());

            // ③ 范围确定：未指定则按已导入 Moment 的起止记忆日
            var days = ListDays(context, range);
            Log("========== ③ 逐天循环构筑：共 " + days.Count + " 个记忆日 ==========");

            var done = 0;
            var skipped = 0;
            var failed = 0;
            foreach (var day in days)
            {
                // 断点续跑：该天没有未归档 Moment 且已标记完成 → 跳过（完成标记不受榜单晋升移动影响）。
                var dayRange = DateRange.Parse(new[] { "--from", day, "--to", day });
                var unbuilt = context.Migration.GetUnbuiltMomentsInRange(
                    dayRange.DayStartMs(System.DateTime.ParseExact(day, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)),
                    dayRange.DayEndMs(System.DateTime.ParseExact(day, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)));
                if (unbuilt.Count == 0 && context.Migration.IsDayCompleted(day))
                {
                    Log("----- DAY " + day + " 已完成，跳过 -----");
                    skipped += 1;
                    continue;
                }
                Log("----- DAY " + day + " 开始（" + (done + skipped + failed + 1) + "/" + days.Count + "）-----");
                try
                {
                    await DayBuilder.RunAsync(context, new[] { "--day", day });
                    Log("----- DAY " + day + " 完成 -----");
                    done += 1;
                }
                catch (Exception exception)
                {
                    failed += 1;
                    Log("----- DAY " + day + " 失败：" + exception.Message + " -----");
                }
            }
            Log("========== 全部完成：成功 " + done + "，跳过 " + skipped + "，失败 " + failed + " ==========");
            Log("========== ④ 榜单晋升兜底：周/月/年/永久全周期重建 ==========");
            await DayLadderLogic.PromoteAllAsync(context, context.RequirePair(), context.RequireLlm());
            return failed == 0 ? 0 : 1;
        }

        private static List<string> ListDays(MigrationContext context, DateRange range)
        {
            if (range.From.HasValue && range.To.HasValue)
            {
                var days = new List<string>();
                for (var day = range.From.Value.Date; day <= range.To.Value.Date; day = day.AddDays(1))
                    days.Add(day.ToString("yyyy-MM-dd"));
                return days;
            }
            var startMs = context.Migration.MinMomentUnixMs();
            var endMs = context.Migration.MaxMomentUnixMs();
            return context.Migration.GetMemoryDaysInRange(startMs, endMs);
        }
    }
}
