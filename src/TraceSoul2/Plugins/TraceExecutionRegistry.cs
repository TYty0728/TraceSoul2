using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using TraceSoul2.Data;

namespace TraceSoul2.Plugins
{
    /// <summary>进程内执行账本。设备通过单调序号回报进度；终态不可被迟到回执复活。</summary>
    public sealed class TraceExecutionRegistry : IDisposable
    {
        private sealed class Entry
        {
            public TraceExecutionSnapshotData State;
            public CancellationTokenSource Cancellation;
            public bool Detached;
            public bool Notified;
        }
        private readonly object gate = new object();
        private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>();
        private readonly Queue<TraceExecutionSnapshotData> events = new Queue<TraceExecutionSnapshotData>();
        private bool disposed;

        public string Start(string conversationId, BrainCapabilityCallData call, string bodyId, CancellationToken token)
        {
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException(nameof(TraceExecutionRegistry));
                foreach (var key in entries.Where(x => Terminal(x.Value.State.Status)).Select(x => x.Key)
                             .Take(Math.Max(0, entries.Count - 255)).ToList())
                {
                    entries[key].Cancellation.Dispose();
                    entries.Remove(key);
                }
                if (entries.Count >= 512 || events.Count >= 512)
                    throw new InvalidOperationException("持续执行或待处理回执数量超过上限。");
                var id = Guid.NewGuid().ToString("N");
                entries[id] = new Entry
                {
                    Cancellation = CancellationTokenSource.CreateLinkedTokenSource(token),
                    State = new TraceExecutionSnapshotData
                    {
                        ExecutionId = id, ConversationId = conversationId, CapabilityId = call.capability_id,
                        BodyId = bodyId ?? string.Empty, GroupId = call.group_id ?? string.Empty,
                        Status = "running", Sequence = -1, ConfirmedContent = string.Empty
                    }
                };
                return id;
            }
        }

        public CancellationToken Token(string id)
        {
            lock (gate) return entries[id].Cancellation.Token;
        }

        public void AcceptResult(string id, TraceCapabilityResultData result)
        {
            lock (gate)
            {
                if (!entries.TryGetValue(id, out var entry)) return;
                entry.Detached = result != null && (result.Status == "running" || result.Status == "accepted");
                if (!Terminal(entry.State.Status) && !entry.Detached)
                {
                    entry.State.Status = result?.Status == "success" ? "completed" : "failed";
                    entry.State.Summary = result?.Summary ?? "执行没有返回结果。";
                }
                Notify(entry);
            }
        }

        public bool Report(TraceExecutionReceiptData receipt)
        {
            if (receipt == null || string.IsNullOrEmpty(receipt.ExecutionId)) return false;
            lock (gate)
            {
                if (!entries.TryGetValue(receipt.ExecutionId, out var entry) || Terminal(entry.State.Status) ||
                    receipt.Sequence <= entry.State.Sequence ||
                    (receipt.Status != "running" && !Terminal(receipt.Status))) return false;
                if (receipt.ProgressMs.HasValue && (receipt.ProgressMs < 0 ||
                    receipt.ProgressMs < entry.State.ProgressMs)) return false;
                entry.State.Sequence = receipt.Sequence;
                entry.State.Status = entry.State.Status == "cancel_requested" && receipt.Status == "running"
                    ? "cancel_requested" : receipt.Status;
                if (receipt.ProgressMs.HasValue) entry.State.ProgressMs = receipt.ProgressMs;
                if (receipt.ConfirmedContent != null) entry.State.ConfirmedContent = Limit(receipt.ConfirmedContent);
                entry.State.Summary = Limit(receipt.Summary);
                Notify(entry);
                return true;
            }
        }

        /// <summary>取消请求与已确认停止分开；驱动收到 token 后必须回报 cancelled。</summary>
        public bool Cancel(string id, string conversationId)
        {
            CancellationTokenSource cancellation;
            lock (gate)
            {
                if (!entries.TryGetValue(id ?? string.Empty, out var entry) || Terminal(entry.State.Status) ||
                    entry.State.ConversationId != conversationId) return false;
                entry.State.Status = "cancel_requested";
                cancellation = entry.Cancellation;
            }
            try { cancellation.Cancel(); }
            catch (ObjectDisposedException) { return false; } // 并发完成并清理时已无需取消。
            return true;
        }

        public IReadOnlyList<TraceExecutionSnapshotData> List(string conversationId)
        {
            lock (gate) return entries.Values.Where(x => x.State.ConversationId == conversationId)
                .Select(x => Copy(x.State)).ToList();
        }

        /// <summary>插件禁用/卸载时先请求停止其仍在运行的动作。</summary>
        public void CancelCapabilities(IEnumerable<string> capabilityIds)
        {
            var ids = new HashSet<string>(capabilityIds ?? Array.Empty<string>(), StringComparer.Ordinal);
            List<TraceExecutionSnapshotData> active;
            lock (gate) active = entries.Values.Where(x => ids.Contains(x.State.CapabilityId) && !Terminal(x.State.Status))
                .Select(x => Copy(x.State)).ToList();
            foreach (var entry in active) Cancel(entry.ExecutionId, entry.ConversationId);
        }

        public List<TraceExecutionSnapshotData> DrainEvents()
        {
            lock (gate)
            {
                var result = events.ToList();
                events.Clear();
                return result;
            }
        }

        private void Notify(Entry entry)
        {
            if (!entry.Detached || entry.Notified || !Terminal(entry.State.Status)) return;
            entry.Notified = true;
            events.Enqueue(Copy(entry.State));
        }
        public static bool Terminal(string status) => status == "completed" || status == "failed" || status == "cancelled";
        private static string Limit(string value) => (value ?? string.Empty).Length <= 4000 ? value ?? string.Empty : value.Substring(0, 4000);
        private static TraceExecutionSnapshotData Copy(TraceExecutionSnapshotData value) => new TraceExecutionSnapshotData
        {
            ExecutionId = value.ExecutionId, ConversationId = value.ConversationId, CapabilityId = value.CapabilityId,
            BodyId = value.BodyId, GroupId = value.GroupId, Status = value.Status, Sequence = value.Sequence,
            ProgressMs = value.ProgressMs, ConfirmedContent = value.ConfirmedContent, Summary = value.Summary
        };
        public void Dispose()
        {
            List<Entry> all;
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                all = entries.Values.ToList(); entries.Clear(); events.Clear();
            }
            foreach (var entry in all) { entry.Cancellation.Cancel(); entry.Cancellation.Dispose(); }
        }
    }
}
