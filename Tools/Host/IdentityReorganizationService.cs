using System;
using System.Globalization;
using System.IO;
using TraceSoul2.Manager;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using TraceSoul2.Logic;

namespace TraceSoul2.Host
{
    public sealed class IdentityReorganizationService : IHostedService
    {
        private readonly SoulRuntime runtime;
        private readonly DailyPipelineWorker daily;
        private readonly object sync = new();
        private readonly CancellationTokenSource lifetime = new();
        private Task work = Task.CompletedTask;
        private bool busy;
        private string lastError;
        public IdentityReorganizationService(SoulRuntime runtime, DailyPipelineWorker daily)
        { this.runtime = runtime; this.daily = daily; }
        public object Status(string slot)
        {
            if (!Array.Exists(IdentityReorganizationLogic.Slots, x => x == slot)) throw new ArgumentException("未知身份项。");
            lock (sync)
            {
                var draft = IdentityReorganizationLogic.Read(runtime.Store, runtime.ConversationId, slot);
                if (!busy && draft?.status == "running") draft.status = "interrupted";
                return new { busy, error = lastError, defaultDay = MemoryDayLogic.ClosedDayKey(DateTimeOffset.Now), draft };
            }
        }
        public void Start(string slot, string day, bool restart)
        {
            if (!Array.Exists(IdentityReorganizationLogic.Slots, x => x == slot)) throw new ArgumentException("未知身份项。");
            day = (day ?? "").Trim();
            if (day.Length > 0 && (!DateTime.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ||
                string.CompareOrdinal(day, MemoryDayLogic.ClosedDayKey(DateTimeOffset.Now)) > 0))
                throw new ArgumentException("关联日期必须是已结束的记忆日。");
            var client = runtime.Providers.CreateReviewClient();
            if (client == null) throw new InvalidOperationException("请先配置复盘模型。");
            lock (sync)
            {
                if (busy) throw new InvalidOperationException("身份重整正在执行，请等待结果。");
                busy = true; lastError = null;
                work = Task.Run(async () => {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    timeout.CancelAfter(TimeSpan.FromMinutes(20));
                    try
                    {
                        using (await daily.HoldForArchiveAsync(timeout.Token))
                        using (var store = new SqliteMemoryManager(Path.Combine(runtime.DataDirectory, "tracesoul2-brainframe.sqlite3")))
                            await IdentityReorganizationLogic.GenerateAsync(store, runtime.ConversationId,
                                slot, day, client, timeout.Token, restart);
                        runtime.Emit("身份重整候选已生成，请在身份页查看对照后写回。");
                    }
                    catch (Exception error)
                    {
                        lock (sync) lastError = error is IdentityReorganizationException ? error.Message : FailureProtection.SafeReason(error);
                        var draft = IdentityReorganizationLogic.Read(runtime.Store, runtime.ConversationId, slot);
                        if (draft?.status != "ready" && draft?.status != "applied")
                        {
                            draft ??= new IdentityReorganizationLogic.Draft { root = runtime.ConversationId, slot = slot, day = day };
                            draft.status = "failed"; draft.error = FailureProtection.SafeReason(error);
                            // These are product-level validation messages, never raw upstream errors.
                            if (error is IdentityReorganizationException)
                                draft.error = error.Message;
                            IdentityReorganizationLogic.Save(runtime.Store, draft);
                        }
                        runtime.Emit("身份重整未完成，已保留原卡及已有归并进度。");
                    }
                    finally { lock (sync) busy = false; }
                });
            }
        }
        public async Task<IdentityReorganizationLogic.Draft> ApplyAsync(string slot, string id, CancellationToken token)
        {
            lock (sync) { if (busy) throw new InvalidOperationException("身份重整正在执行。"); busy = true; }
            try
            {
                using (await daily.HoldForArchiveAsync(token))
                    return await runtime.ApplyIdentityReorganizationAsync(slot, id, token);
            }
            finally { lock (sync) busy = false; }
        }
        public Task StartAsync(CancellationToken token) => Task.CompletedTask;
        public async Task StopAsync(CancellationToken token) { lifetime.Cancel(); await work.WaitAsync(token); }
    }
}
