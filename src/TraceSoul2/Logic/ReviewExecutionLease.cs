using System;
using System.IO;

namespace TraceSoul2.Logic
{
    public static class ReviewExecutionLease
    {
        // OS-held lock, released even on process death. Never infer liveness from a stale PID.
        public static IDisposable Acquire(string directory)
        {
            Directory.CreateDirectory(directory);
            try { return new FileStream(Path.Combine(directory, "review-execution.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None); }
            catch (IOException error) { throw new InvalidOperationException("复盘或维护进程仍持有执行锁，拒绝并发操作。", error); }
        }
    }
}
