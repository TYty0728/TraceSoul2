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
        private readonly ReviewMaintenanceGate buildGate;
        private readonly AsyncLocal<bool> maintenanceArchive = new();
        private Task manualRun;
        private ReviewMaintenanceGate.Lease externalLease;
        private string externalId;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private string HistoryPath => Path.Combine(runtime.DataDirectory, "migration.sqlite3");

        public DailyPipelineWorker(SoulRuntime runtime)
        {
            this.runtime = runtime;
            buildGate = new(() => ReviewExecutionLease.Acquire(runtime.DataDirectory));
            migrateDll = ResolveMigrateDll();
            try
            {
                using var lease = ReviewExecutionLease.Acquire(runtime.DataDirectory);
                DailyBuildHistory.RecoverInterrupted(HistoryPath, "上次进程已结束，检查点保留");
                runtime.Providers.Protection.RecoverInterruptedAttempts();
            }
            catch (InvalidOperationException) { runtime.Emit("仍有复盘进程持有执行锁，保留运行状态。"); }
        }

        public bool IsAvailable => !string.IsNullOrWhiteSpace(migrateDll);
        public string MigrateDll => migrateDll;

        public Task<ReviewMaintenanceGate.Lease> HoldMaintenanceAsync(CancellationToken token)
            => buildGate.ReserveAsync(token);
        public async Task ArchiveDuringMaintenanceAsync(Func<Task> archive)
        {
            if (!buildGate.MaintenanceHeld) throw new InvalidOperationException("更新存档必须持有维护租约。");
            maintenanceArchive.Value = true;
            try { await archive(); } finally { maintenanceArchive.Value = false; }
        }
        public async Task<string> BeginExternalMaintenanceAsync(CancellationToken token)
        {
            var lease = await HoldMaintenanceAsync(token);
            externalId = Guid.NewGuid().ToString("N"); externalLease = lease;
            return externalId;
        }
        public object MaintenanceStatus() => new { busy = buildGate.Busy, lease = externalId };
        public void EndExternalMaintenance(string id)
        {
            if (id != externalId || externalLease == null) throw new InvalidOperationException("维护租约不匹配。");
            var lease = externalLease; externalLease = null; externalId = null; lease.Dispose();
        }
        public async Task<IDisposable> HoldForArchiveAsync(CancellationToken token)
        {
            if (maintenanceArchive.Value) return new ArchiveLease(null);
            while (!buildGate.TryWork()) await Task.Delay(100, token);
            try { return new ArchiveLease(buildGate, ReviewExecutionLease.Acquire(runtime.DataDirectory)); }
            catch { buildGate.EndWork(); throw; }
        }
        private sealed class ArchiveLease : IDisposable
        {
            private ReviewMaintenanceGate gate;
            private IDisposable execution;
            public ArchiveLease(ReviewMaintenanceGate gate, IDisposable execution = null) { this.gate = gate; this.execution = execution; }
            public void Dispose() { Interlocked.Exchange(ref execution, null)?.Dispose(); Interlocked.Exchange(ref gate, null)?.EndWork(); }
        }

        public DailyBuildPlan Inspect(string dayKey, bool includeBusy = true, bool includePendingReviews = true)
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
                    DailyBuildHistory.ReadStates(HistoryPath), DailyBuildHistory.ReadAttempts(HistoryPath, target),
                    runtime.Store.GetUnbuiltPublicMemoryDayKeysBefore(MemoryDayLogic.CurrentStart(now).ToUnixTimeMilliseconds()),
                    runtime.Store.GetUnreviewedRuntimeDayKeysBefore(MemoryDayLogic.CurrentStart(now).ToUnixTimeMilliseconds()),
                    includePendingReviews);
                foreach (var day in plan.Days)
                {
                    var recovery = DailyBuildPreflight.LegacyRecoveryBlocker(
                        Path.Combine(runtime.DataDirectory, "tracesoul2-brainframe.sqlite3"), day);
                    if (recovery != null) plan.Blockers.Add(day + "：" + recovery);
                }
            }
            catch (Exception)
            {
                plan = new DailyBuildPlan { Day = target };
                plan.Blockers.Add("无法读取历史构建状态，请先检查 migration.sqlite3 和主库；本次不会启动构建。");
            }
            plan.Busy = includeBusy && buildGate.Busy;
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
            if (runtime.AutomaticWorkPaused) return;
            var historyReported = false;
            var lastBlocked = "";
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (!buildGate.Busy)
                    {
                        var plan = Inspect(null, includePendingReviews: false);
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
                        else if (plan.Ready && buildGate.TryWork())
                        {
                            await RunReservedPlanAsync(plan.Day, stoppingToken, includePendingReviews: false);
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
            if (!buildGate.TryWork()) throw new InvalidOperationException("已有日构建队列正在执行，请勿重复启动。");
            DailyBuildPlan plan;
            try
            {
                plan = Inspect(dayKey, false);
                RequireReady(plan);
            }
            catch { buildGate.EndWork(); throw; }
            manualRun = RunReservedPlanAsync(plan.Day, lifetime.Token);
            return plan;
        }

        private static void RequireReady(DailyBuildPlan plan)
        {
            if (plan.Blockers.Count > 0) throw new InvalidOperationException(string.Join("；", plan.Blockers));
            if (plan.Days.Count == 0) throw new InvalidOperationException("目标日期及之前的日构建已完成，无需重复执行。");
        }

        private async Task RunReservedPlanAsync(string target, CancellationToken token, bool includePendingReviews = true)
        {
            try
            {
                // 服务端在真正启动前再检查一次，前端页面不能绕过旧日和暂停检查。
                var plan = Inspect(target, false, includePendingReviews);
                RequireReady(plan);
                runtime.Emit("日构建检查通过，执行顺序：" + string.Join(" → ", plan.Days));
                var succeeded = true;
                var yieldedForMaintenance = false;
                foreach (var day in plan.Days)
                {
                    if (buildGate.MaintenanceRequested) { yieldedForMaintenance = true; break; }
                    if (await RunDayBuildAsync(day, token) != 0) { succeeded = false; break; }
                }
                var pending = DailyBuildHistory.ReadStates(HistoryPath).Count(x => x.Status == "pending" || x.Status == "deferred" || x.Status == "waiting");
                runtime.Emit(yieldedForMaintenance ? "当前日进度已保存，剩余日期等待维护结束后继续。" : succeeded ? pending > 0 ? "已保存各阶段进度，仍有" + pending + "天部分派生任务待整理，将按预算补做。"
                    : "历史待补与目标日构建均已完成。" : "日构建队列已停止；旧日未完成，不会执行后续日期。");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception error)
            {
                runtime.Providers.Protection.Pause("daily", "日构建队列", error);
                runtime.Emit("日构建队列已暂停：" + FailureProtection.SafeReason(error));
            }
            finally { buildGate.EndWork(); }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            lifetime.Cancel();
            await base.StopAsync(cancellationToken);
            if (manualRun != null) await manualRun.WaitAsync(cancellationToken);
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
                    DailyBuildHistory.RecoverInterrupted(HistoryPath, timeout.IsCancellationRequested ? "子进程超时或服务停止，检查点保留" : "子进程被中断，检查点保留");
                    throw;
                }
                var state = DailyBuildHistory.ReadStates(HistoryPath).FirstOrDefault(x => x.DayKey == dayKey);
                runtime.Emit("日构建结束（exit " + process.ExitCode + "）：" + dayKey);
                if (process.ExitCode != 0 || state == null || (state.Status != "done" && state.Status != "pending" && state.Status != "deferred" && state.Status != "waiting"))
                {
                    runtime.Providers.Protection.FailAttempt("daily",
                        new InvalidOperationException(state?.Error ?? "子进程未确认完成"));
                    return -1;
                }
                runtime.Providers.Protection.CompleteAttempt("daily");
                if (state.Status == "done") runtime.Providers.Protection.Resume("review-backlog:" + dayKey);
                if (state.Status == "pending" || state.Status == "deferred")
                {
                    runtime.Emit(dayKey + "：" + state.Error + (state.RetryCount >= 3 ? "。已达自动补做预算，需检查积压原因。" : "。已保存检查点，将退避后补做。"));
                    if (state.RetryCount >= 3) runtime.Providers.Protection.ReportReviewBacklog(dayKey);
                    // Core memories are complete. Pending prose must not suppress the independent night opportunity.
                }
                if (state.Status == "deferred" || state.Status == "waiting") return 0;
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
