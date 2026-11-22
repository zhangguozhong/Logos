using System;
using System.Diagnostics;
using System.Threading;

namespace Logos.Storage
{
    // 取消请求只通知游标停止使用旧代际；删除还需等到底层流确实关闭并归还登记槽位。
    /// <summary>四个协作式所有者资源。取消本身绝不会释放资源。</summary>
    public sealed class ReadLeaseRegistry
    {
        private readonly ReadLease[] slots = new ReadLease[4];
        public int FixedBufferPayloadBytes => slots.Length * IntPtr.Size;
        private int ownerThread;
        public int ActiveCount
        {
            get;
            private set;
        }

        public ReadLease Register(IDisposable resource, long generation, long openedTimestamp)
        {
            CheckThread();
            if (resource == null || generation < 0)
                throw new ArgumentOutOfRangeException();
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i] != null)
                    continue;
                var lease = new ReadLease(this, i, resource, generation, openedTimestamp);
                slots[i] = lease;
                ActiveCount++;
                return lease;
            }

            throw new InvalidOperationException("The four bounded read leases are in use.");
        }

        public void RequestExpired(long timestamp)
        {
            CheckThread();
            foreach (var slot in slots)
                if (
                    slot != null
                    && timestamp >= slot.OpenedTimestamp
                    && timestamp - slot.OpenedTimestamp >= 5 * Stopwatch.Frequency
                )
                    slot.IsCancellationRequested = true;
        }

        public void InvalidateGeneration(long current)
        {
            CheckThread();
            foreach (var slot in slots)
                if (slot != null && slot.CleanupGeneration != current)
                    slot.IsCancellationRequested = true;
        }

        public void CancelAll()
        {
            CheckThread();
            foreach (var slot in slots)
                if (slot != null)
                    slot.IsCancellationRequested = true;
        }

        /// <summary>每步尝试一个资源；失败时保留槽位并阻止删除。</summary>
        public bool CloseCancelled(JournalStepContext context)
        {
            CheckThread();
            foreach (var slot in slots)
            {
                if (context.ShouldYield)
                    return false;
                if (slot == null || !slot.IsCancellationRequested)
                    continue;
                try
                {
                    slot.Resource.Dispose();
                    slot.ReleaseAfterClosed();
                }
                catch
                {
                    return false;
                }

                break;
            }

            foreach (var slot in slots)
                if (slot != null && slot.IsCancellationRequested)
                    return false;
            return true;
        }

        private void CheckThread()
        {
            int thread = Thread.CurrentThread.ManagedThreadId;
            if (ownerThread == 0)
                ownerThread = thread;
            else if (thread != ownerThread)
                throw new InvalidOperationException("Read resources belong to one owner worker.");
        }

        public sealed class ReadLease
        {
            private readonly ReadLeaseRegistry registry;
            private readonly int index;
            internal readonly IDisposable Resource;
            internal long OpenedTimestamp
            {
                get;
            }
            public long CleanupGeneration
            {
                get;
            }
            public bool IsCancellationRequested
            {
                get;
                internal set;
            }

            internal ReadLease(
                ReadLeaseRegistry registry,
                int index,
                IDisposable resource,
                long generation,
                long opened
            )
            {
                this.registry = registry;
                this.index = index;
                Resource = resource;
                CleanupGeneration = generation;
                OpenedTimestamp = opened;
            }

            internal void ReleaseAfterClosed()
            {
                registry.CheckThread();
                if (!ReferenceEquals(registry.slots[index], this))
                    return;
                registry.slots[index] = null;
                registry.ActiveCount--;
            }
        }
    }
}
