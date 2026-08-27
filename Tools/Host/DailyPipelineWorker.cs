using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using TraceSoul2.Logic;

namespace TraceSoul2.Host
{
    /// <summary>
    /// 自动日构建循环：每天 04:00（+08:00 记忆日边界）自动把刚结束的那天
    /// 未归档 Moment 构筑成结构性记忆（索引/条目/向量）并做复盘与日榜。
    /// 成功后若仍在后半夜窗口，再漏一句夜里的余温给她。
    /// 用数据目录里用户填写的 API Key；每次构建的 LLM 调用可到 5090 实时监视台观看。
    /// </summary>
    public sealed class DailyPipelineWorker : BackgroundService
    {
        private readonly SoulRuntime runtime;
        private readonly string migrateDll;
        private readonly SemaphoreSlim buildGate = new SemaphoreSlim(1, 1);

        public DailyPipelineWorker(SoulRuntime runtime)
        {
            this.runtime = runtime;
            migrateDll = ResolveMigrateDll();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var lastSucceededClosedDay = string.Empty;
            var lastAttemptFailed = false;
            while (!stoppingToken.IsCancellationRequested)
            {
                var now = DateTimeOffset.Now.ToOffset(MemoryDayLogic.ChinaOffset);
                var unbuilt = runtime.Store.GetUnbuiltMemoryDayKeysBefore(
                    MemoryDayLogic.CurrentStart(now).ToUnixTimeMilliseconds());
                if (DailyPipelineScheduleLogic.ShouldCatchUp(
                        now, lastSucceededClosedDay, lastAttemptFailed, unbuilt))
                {
                    var ok = await CatchUpAsync(stoppingToken);
                    lastAttemptFailed = !ok;
                    if (ok)
                        lastSucceededClosedDay = MemoryDayLogic.ClosedDayKey(now);
                    var nextBoundary = MemoryDayLogic.CurrentStart(now).AddDays(1);
                    runtime.Emit(ok
                        ? "下次自动日构建：" + nextBoundary.ToString("yyyy-MM-dd HH:mm") +
                          "（+08:00 记忆日边界，墙钟每分钟核对）"
                        : "日构建未完成，" +
                          ((int)DailyPipelineScheduleLogic.RetryInterval.TotalMinutes) +
                          " 分钟后重试（不会等到明天）");
                }
                var wait = DailyPipelineScheduleLogic.NextWait(now, lastAttemptFailed);
                try
                {
                    await Task.Delay(wait, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        /// <summary>手动触发（控制台按钮 / API）：默认构建刚结束的记忆日，可指定 --day。返回实际构建的日期。</summary>
        public string Trigger(string dayKey)
        {
            var target = string.IsNullOrWhiteSpace(dayKey)
                ? MemoryDayLogic.ClosedDayKey(DateTimeOffset.Now)
                : dayKey.Trim();
            _ = RunDayBuildAsync(target, CancellationToken.None);
            return target;
        }

        public bool IsAvailable { get { return !string.IsNullOrWhiteSpace(migrateDll); } }
        public string MigrateDll { get { return migrateDll; } }

        private async Task<bool> CatchUpAsync(CancellationToken cancellationToken)
        {
            var now = DateTimeOffset.Now.ToOffset(MemoryDayLogic.ChinaOffset);
            var currentStart = MemoryDayLogic.CurrentStart(now);
            var closedDay = MemoryDayLogic.ClosedDayKey(now);
            var days = runtime.Store.GetUnbuiltMemoryDayKeysBefore(currentStart.ToUnixTimeMilliseconds())
                .Where(x => string.Compare(x, closedDay, StringComparison.Ordinal) <= 0)
                .Append(closedDay)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();
            if (days.Count > 1)
                runtime.Emit("检测到待补日终复盘：" + string.Join("、", days));
            foreach (var day in days)
            {
                var exitCode = await RunDayBuildAsync(day, cancellationToken);
                // 旧日失败时不能越过它更新后续卡片；短间隔重试，不再干等到下一个 04:00。
                if (exitCode != 0) return false;
            }
            return true;
        }

        private async Task<int> RunDayBuildAsync(string dayKey, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(migrateDll))
            {
                runtime.Emit("自动日构建不可用：找不到 TraceSoul2.Migrate.dll。");
                return -1;
            }
            await buildGate.WaitAsync(cancellationToken);
            var exitCode = -1;
            try
            {
                runtime.Emit("日构建启动：" + dayKey + "（全天分批 → 长期沉淀 → 次日继承 → 退出本日切片）");
                var startInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = "\"" + migrateDll + "\" build --day " + dayKey,
                    UseShellExecute = false
                };
                startInfo.Environment["TRACESOUL2_DATA"] = runtime.DataDirectory;
                try
                {
                    using (var process = Process.Start(startInfo))
                    {
                        if (process == null) throw new InvalidOperationException("无法启动日构建进程。");
                        await process.WaitForExitAsync(cancellationToken);
                        runtime.Emit("日构建结束（exit " + process.ExitCode + "）：" + dayKey);
                        exitCode = process.ExitCode;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return -1;
                }
                catch (Exception exception)
                {
                    runtime.Emit("日构建启动失败：" + exception.Message);
                    return -1;
                }
            }
            finally
            {
                buildGate.Release();
            }
            if (exitCode == 0)
                await runtime.TrySpeakNightResidueAsync(dayKey, cancellationToken);
            return exitCode;
        }

        private static string ResolveMigrateDll()
        {
            var fromEnv = Environment.GetEnvironmentVariable("TRACESOUL2_MIGRATE_DLL");
            if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv)) return fromEnv;
            var besideHost = Path.Combine(AppContext.BaseDirectory, "TraceSoul2.Migrate.dll");
            if (File.Exists(besideHost)) return besideHost;

            var frameworkDir = new DirectoryInfo(
                AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var configurationDir = frameworkDir.Parent;
            var hostDir = configurationDir?.Parent?.Parent;
            var toolsDir = hostDir?.Parent;
            if (configurationDir == null || toolsDir == null) return null;

            var candidate = Path.Combine(
                toolsDir.FullName,
                "Migration", "bin", configurationDir.Name, frameworkDir.Name,
                "TraceSoul2.Migrate.dll");
            return File.Exists(candidate) ? candidate : null;
        }
    }
}
