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
            // 重启不是新的时间线：先补齐所有已经越过 04:00、但仍有未消费 Moment 的记忆日。
            await CatchUpAsync(stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                var now = DateTimeOffset.Now.ToOffset(MemoryDayLogic.ChinaOffset);
                var nextBoundary = MemoryDayLogic.CurrentStart(now).AddDays(1);
                runtime.Emit("下次自动日构建：" + nextBoundary.ToString("yyyy-MM-dd HH:mm") +
                             "（+08:00 记忆日边界）");
                var wait = nextBoundary - now;
                try
                {
                    await Task.Delay(wait > TimeSpan.Zero ? wait : TimeSpan.FromSeconds(30), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                await CatchUpAsync(stoppingToken);
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

        private async Task CatchUpAsync(CancellationToken cancellationToken)
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
                // 旧日失败时不能越过它更新后续卡片；留到下次启动/边界继续补偿。
                if (exitCode != 0) break;
            }
        }

        private async Task<int> RunDayBuildAsync(string dayKey, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(migrateDll))
            {
                runtime.Emit("自动日构建不可用：找不到 TraceSoul2.Migrate.dll。");
                return -1;
            }
            await buildGate.WaitAsync(cancellationToken);
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
                        return process.ExitCode;
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
