using System;
using System.Threading;
using System.Threading.Tasks;

namespace TraceSoul2.Host
{
    // Reservation precedes the wait: no new work can slip between idle and handoff.
    public sealed class ReviewMaintenanceGate
    {
        private readonly object sync = new();
        private readonly SemaphoreSlim work = new(1, 1);
        private bool reserved;
        private Lease current;
        private readonly Func<IDisposable> executionLock;
        public ReviewMaintenanceGate(Func<IDisposable> executionLock = null) { this.executionLock = executionLock; }
        public bool Busy { get { lock (sync) return reserved || work.CurrentCount == 0; } }
        public bool MaintenanceRequested { get { lock (sync) return reserved; } }
        public bool MaintenanceHeld { get { lock (sync) return current != null; } }
        public bool TryWork() { lock (sync) return !reserved && work.Wait(0); }
        public void EndWork() => work.Release();
        public async Task<Lease> ReserveAsync(CancellationToken token)
        {
            lock (sync)
            {
                if (reserved) throw new InvalidOperationException("已有维护操作，不能重复申请。");
                reserved = true;
            }
            var acquired = false;
            try
            {
                await work.WaitAsync(token); acquired = true;
                var lease = new Lease(this, executionLock?.Invoke());
                lock (sync) return current = lease;
            }
            catch { lock (sync) { reserved = false; if (acquired) work.Release(); } throw; }
        }
        public sealed class Lease : IDisposable
        {
            private ReviewMaintenanceGate owner;
            private bool retained;
            private readonly IDisposable execution;
            internal Lease(ReviewMaintenanceGate owner, IDisposable execution) { this.owner = owner; this.execution = execution; }
            public void RetainUntilExit() => retained = true;
            public void Dispose()
            {
                if (retained) return;
                var value = Interlocked.Exchange(ref owner, null);
                if (value == null) return;
                execution?.Dispose();
                lock (value.sync) { value.current = null; value.reserved = false; value.work.Release(); }
            }
        }
    }
}
