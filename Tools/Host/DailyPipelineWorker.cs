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
    /// <summary>先检查历史，再按旧日到目标日执行；整批互斥，首错停止。</summary>
    public sealed class DailyPipelineWorker : BackgroundService
    {
        private readonly SoulRuntime runtime;
        private readonly string migrateDll;
        private readonly SemaphoreSlim buildGate = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private string HistoryPath => Path.Combine(runtime.DataDirectory, "migration.sqlite3");

        public DailyPipelineWorker(SoulRuntime runtime)
        {
            this.runtime = runtime;
            migrateDll = ResolveMigrateDll();
            runtime.Providers.Protection.RecoverInterruptedAttempts();
        }

        public bool IsAvailable => !string.IsNullOrWhiteSpace(migrateDll);
        public string MigrateDll => migrateDll;

        public DailyBuildPlan Inspect(string dayKey, bool includeBusy = true)
        {
            var now = DateTimeOffset.Now;
            var closed = MemoryDayLogic.ClosedDayKey(now);
            var target = string.IsNullOrWhiteSpace(dayKey) ? closed : dayKey.Trim();
            if (!DailyBuildPreflight.ValidDay(target) || string.CompareOrdinal(target, closed) > 0)
                throw new ArgumentException("请选择已结束的记忆日（yyyy-MM-dd），最晚为 " + closed + "。");
            DailyBuildPlan plan;
            try
            {
                plan = DailyBuildPreflight.Plan(target, closed,
                    runtime.Store.GetUnbuiltMemoryDayKeysBefore(MemoryDayLogic.CurrentStart(now).ToUnixTimeMilliseconds()),
                    DailyBuildHistory.ReadStates(HistoryPath), DailyBuildHistory.ReadAttempts(HistoryPath, target));
            }
            catch (Exception)
            {
                plan = new DailyBuildPlan { Day = target };
                plan.Blockers.Add("无法读取历史构建状态，请先检查 migration.sqlite3 和主库；本次不会启动构建。");
            }
            plan.Busy = includeBusy && buildGate.CurrentCount == 0;
            if (plan.Busy)
                foreach (var issue in plan.HistoricalFailures.Concat(plan.RecentAttempts).Where(x => x.Status == "running"))
                    issue.Reason = "正在执行";
            if (!IsAvailable) plan.Blockers.Add("找不到 TraceSoul2.Migrate.dll，请修复云端程序包。");
            if (!runtime.Store.LoadPairIdentity().IsComplete) plan.Blockers.Add("请先配置两人身份。");
            var provider = Environment.GetEnvironmentVariable("TRACESOUL2_MIGRATION_PROVIDER");
            var model = Environment.GetEnvironmentVariable("TRACESOUL2_MIGRATION_MODEL");
            var client = runtime.Providers.CreateReviewClient(provider, model);
            plan.ReviewTemperature = runtime.Providers.ReviewTemperature;
            plan.ProviderOverride = !string.IsNullOrWhiteSpace(provider);
            plan.ProviderId = client?.ProviderId ?? provider ?? "";
            plan.Model = client?.Model ?? model ?? "";
            if (client == null) plan.Blockers.Add("复盘模型未配置或缺少 API Key。");
            foreach (var fault in runtime.Providers.Protection.List().Where(x =>
                         !x.NonBlocking && (x.Key == "daily" || x.Key == "provider:" + plan.ProviderId)))
                plan.Blockers.Add(fault.Label + "：" + fault.Reason + "；请先处理并在错误保护中恢复。");
            return plan;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var historyReported = false;
            var lastBlocked = "";
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (buildGate.CurrentCount > 0)
                    {
                        var plan = Inspect(null);
                        // 旧版遗留 failed/running 也不能一启动就重复收费。
                        if (plan.HistoricalFailures.Count > 0)
                        {
                            if (!historyReported)
                            {
                                var first = plan.HistoricalFailures[0];
                                runtime.Providers.Protection.Pause("daily", "历史日构建（" + first.Day + "）",
                                    new InvalidOperationException(first.Reason));
                                runtime.Emit("发现历史构建错误：" + first.Day + "｜" + first.Reason + "。请在 WebUI 检查并处理历史后继续。");
                                historyReported = true;
                            }
                        }
                        else if (plan.Ready && buildGate.Wait(0))
                        {
                            await RunReservedPlanAsync(plan.Day, stoppingToken);
                            historyReported = false;
                        }
                        else if (plan.Blockers.Count > 0)
                        {
                            var blocked = string.Join("；", plan.Blockers);
                            if (blocked != lastBlocked) runtime.Emit("日构建检查未通过：" + blocked);
                            lastBlocked = blocked;
                        }
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception error)
                {
                    runtime.Providers.Protection.Pause("daily", "日构建检查", error);
                    runtime.Emit("日构建检查失败，已停止自动执行。");
                }
                await Task.Delay(DailyPipelineScheduleLogic.PollInterval, stoppingToken);
            }
        }

        public DailyBuildPlan Trigger(string dayKey)
        {
            if (!buildGate.Wait(0)) throw new InvalidOperationException("已有日构建队列正在执行，请勿重复启动。");
            DailyBuildPlan plan;
            try
            {
                plan = Inspect(dayKey, false);
                RequireReady(plan);
            }
            catch { buildGate.Release(); throw; }
            _ = RunReservedPlanAsync(plan.Day, lifetime.Token);
            return plan;
        }

        private static void RequireReady(DailyBuildPlan plan)
        {
            if (plan.Blockers.Count > 0) throw new InvalidOperationException(string.Join("；", plan.Blockers));
            if (plan.Days.Count == 0) throw new InvalidOperationException("目标日期及之前的日构建已完成，无需重复执行。");
        }

        private async Task RunReservedPlanAsync(string target, CancellationToken token)
        {
            try
            {
                // 服务端在真正启动前再检查一次，前端页面不能绕过旧日和暂停检查。
                var plan = Inspect(target, false);
                RequireReady(plan);
                runtime.Emit("日构建检查通过，执行顺序：" + string.Join(" → ", plan.Days));
                var succeeded = await DailyBuildPreflight.RunInOrderAsync(plan.Days,
                    day => RunDayBuildAsync(day, token));
                runtime.Emit(succeeded ? "历史待补与目标日构建均已完成。" : "日构建队列已停止；旧日未完成，不会执行后续日期。");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception error)
            {
                runtime.Providers.Protection.Pause("daily", "日构建队列", error);
                runtime.Emit("日构建队列已暂停：" + FailureProtection.SafeReason(error));
            }
            finally { buildGate.Release(); }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            lifetime.Cancel();
            await base.StopAsync(cancellationToken);
            await buildGate.WaitAsync(cancellationToken);
            buildGate.Release();
        }

        private async Task<int> RunDayBuildAsync(string dayKey, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lifetime.Token.ThrowIfCancellationRequested();
            runtime.Providers.Protection.ThrowIfPaused("daily");
            runtime.Providers.Protection.BeginAttempt("daily", "日构建复盘（" + dayKey + "）");
            try
            {
                runtime.Emit("日构建启动：" + dayKey);
                var startInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    ArgumentList = { migrateDll, "build", "--day", dayKey },
                    UseShellExecute = false
                };
                startInfo.Environment["TRACESOUL2_DATA"] = runtime.DataDirectory;
                using var process = Process.Start(startInfo);
                if (process == null) throw new InvalidOperationException("无法启动日构建进程。");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromMinutes(30));
                try { await process.WaitForExitAsync(timeout.Token); }
                catch
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                    throw;
                }
                var state = DailyBuildHistory.ReadStates(HistoryPath).FirstOrDefault(x => x.DayKey == dayKey);
                runtime.Emit("日构建结束（exit " + process.ExitCode + "）：" + dayKey);
                if (process.ExitCode != 0 || state?.Status != "done")
                {
                    runtime.Providers.Protection.FailAttempt("daily",
                        new InvalidOperationException(state?.Error ?? "子进程未确认完成"));
                    return -1;
                }
                runtime.Providers.Protection.CompleteAttempt("daily");
            }
            catch (Exception error)
            {
                runtime.Providers.Protection.FailAttempt("daily", error);
                runtime.Emit("日构建失败：" + dayKey + "｜" + FailureProtection.SafeReason(error));
                return -1;
            }
            await runtime.TrySpeakNightResidueAsync(dayKey, cancellationToken);
            return 0;
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
