using System;
using System.Diagnostics;
using System.IO;
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

        public DailyPipelineWorker(SoulRuntime runtime)
        {
            this.runtime = runtime;
            migrateDll = ResolveMigrateDll();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
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
                var dayKey = MemoryDayLogic.ClosedDayKey(DateTimeOffset.Now);
                StartDayBuild(dayKey);
                // 构建本身可能超过一分钟；等下一个边界再跑，不会重复（build 幂等且只消费未归档 Moment）。
            }
        }

        /// <summary>手动触发（控制台按钮 / API）：默认构建刚结束的记忆日，可指定 --day。返回实际构建的日期。</summary>
        public string Trigger(string dayKey)
        {
            var target = string.IsNullOrWhiteSpace(dayKey)
                ? MemoryDayLogic.ClosedDayKey(DateTimeOffset.Now)
                : dayKey.Trim();
            StartDayBuild(target);
            return target;
        }

        public bool IsAvailable { get { return !string.IsNullOrWhiteSpace(migrateDll); } }
        public string MigrateDll { get { return migrateDll; } }

        private void StartDayBuild(string dayKey)
        {
            if (string.IsNullOrWhiteSpace(migrateDll))
            {
                runtime.Emit("自动日构建不可用：找不到 TraceSoul2.Migrate.dll。");
                return;
            }
            runtime.Emit("日构建启动：" + dayKey + "（未归档 Moment → 事件/条目/向量 → 复盘 → 日榜）");
            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "\"" + migrateDll + "\" build --day " + dayKey,
                UseShellExecute = false
            };
            startInfo.Environment["TRACESOUL2_DATA"] = runtime.DataDirectory;
            try
            {
                var process = Process.Start(startInfo);
                process.EnableRaisingEvents = true;
                process.Exited += (sender, args) =>
                    runtime.Emit("日构建结束（exit " + process.ExitCode + "）：" + dayKey);
            }
            catch (Exception exception)
            {
                runtime.Emit("日构建启动失败：" + exception.Message);
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
