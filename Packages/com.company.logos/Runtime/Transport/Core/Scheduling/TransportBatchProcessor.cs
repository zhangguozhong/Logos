using System;
using System.Threading;
using Logos.Storage;
namespace Logos.Transport
{
    // 组合运行于原 Core 工作线程。Storage 仍是唯一 Journal 所有者。
    internal sealed class TransportBatchProcessor : IBufferedLogBatchProcessor, ILogDiagnosticsReceiver, ILogAppLifecycleReceiver, IClearMemoryBarrier
    {
        private readonly StorageBatchProcessor storage;
        internal TransferStateStore Store
        {
            get;
        }
        internal TransferCoordinator Coordinator
        {
            get;
        }
        private readonly JournalSession journal;
        private IMonotonicClock clock;
        private long nextWake, releaseAt, lastRelease = -1;
        private bool reserveTransport, transportClosed, disposed;
        private volatile bool clearRequested;
        private volatile bool completed;
        private volatile TransferControlMailbox controls;
        private readonly object snapshotGate = new object();
        private TransferSnapshot snapshot;
        internal void BindControls(TransferControlMailbox mailbox)
        {
            if (controls != null)
                throw new InvalidOperationException("Controls already bound.");
            controls = mailbox;
            if (completed)
                mailbox.CancelQueued(TransferControlStatus.StoppedBeforeExecution, true);
            else if (clearRequested)
                mailbox.CancelQueued(TransferControlStatus.ClearedBeforeExecution, false);
        }
        internal TransferSnapshot GetTransferSnapshot()
        {
            lock (snapshotGate)
                return snapshot;
        }
        internal StorageHealthSnapshot GetStorageHealthSnapshot() => storage.GetHealthSnapshot();
        private void PublishSnapshot()
        {
            var value = Coordinator.CapturePublicSnapshot();
            lock (snapshotGate)
                snapshot = value;
        }
        private void RunOneControl(WorkerControlContext context)
        {
            var mailbox = controls;
            if (context.StopRequested)
            {
                mailbox?.CancelQueued(TransferControlStatus.StoppedBeforeExecution, true);
                Coordinator.RequestStop();
                return;
            }
            if (mailbox == null || !mailbox.TryBegin(out var command))
                return;
            TransferControlStatus status;
            if (command.Release)
                status = Coordinator.Release(command.Handle);
            else
                status = AdmissionStatus(Coordinator.SubmitManual(command.Request));
            mailbox.Complete(command.Ticket, status);
        }
        private static TransferControlStatus AdmissionStatus(TransferAdmission admission)
        {
            switch (admission)
            {
                case TransferAdmission.Accepted:
                    return TransferControlStatus.AcceptedInMemory;
                case TransferAdmission.Existing:
                    return TransferControlStatus.Existing;
                case TransferAdmission.Busy:
                    return TransferControlStatus.Busy;
                case TransferAdmission.Conflict:
                    return TransferControlStatus.Conflict;
                case TransferAdmission.Unauthorized:
                    return TransferControlStatus.Unauthorized;
                case TransferAdmission.NotReady:
                    return TransferControlStatus.NotReady;
                default:
                    return TransferControlStatus.Invalid;
            }
        }
        internal TransportBatchProcessor(StorageBatchProcessor storage, IStorageKeyProvider keys, IUploadService service, TransferPolicy policy, string installation, string destination)
        {
            this.storage = storage ?? throw new ArgumentNullException(nameof(storage));
            journal = storage.TransferJournal;
            Store = new TransferStateStore(journal, keys, installation, destination);
            Coordinator = new TransferCoordinator(journal, Store, new TransferAccessController(), service, keys, policy, installation, destination);
            storage.AttachAuxiliaryBarrier(this);
        }
        public LogClassMask ReadyClassMask => storage.ReadyClassMask;
        public long NextWakeTicks => (controls?.HasQueued ?? false) ? 0 : Math.Min(Interlocked.Read(ref nextWake), storage.NextWakeTicks);
        internal void WakeControl() => Interlocked.Exchange(ref nextWake, 0);
        public BatchProcessResult ProcessBatch(LogBatchView batch, BatchProcessingContext context) => storage.ProcessBatch(batch, context);
        public BatchTransferResult TransferBatch(LogBatchView batch, WorkerControlContext context) => storage.TransferBatch(batch, context);
        public bool TryPeekOutcome(out LogStorageOutcome outcome) => storage.TryPeekOutcome(out outcome);
        public bool AckOutcome(ulong sequence) => storage.AckOutcome(sequence);
        public void BindDiagnostics(ILogDiagnosticsSource source) => storage.BindDiagnostics(source);
        public void RequestBackgroundSeal()
        {
            Coordinator.SetBackground(true);
            storage.RequestBackgroundSeal();
            Interlocked.Exchange(ref nextWake, 0);
        }
        public void RequestControlCheck()
        {
            Coordinator.SetBackground(false);
            storage.RequestControlCheck();
            Interlocked.Exchange(ref nextWake, 0);
        }
        public void CloseTargets(ClearScope scope)
        {
            clearRequested = true;
            controls?.CancelQueued(TransferControlStatus.ClearedBeforeExecution, false);
            Coordinator.CloseForClear();
            Store.CloseForClear();
        }
        // 来自 Storage 清理步骤的回调：仅检查证明，绝不重入 I/O。
        // 清理同时等待协调器释放内存和状态库完成持久屏障，任一侧尚未完成都会推迟清理。
        public bool ClearOwnedMemory(long cleanupGeneration) => Coordinator.ClearMemoryReady && Store.ClearBarrierReady;
        public WorkerControlResult Step(WorkerControlContext context)
        {
            if (disposed)
                throw new ObjectDisposedException(nameof(TransportBatchProcessor));
            // 消耗先前唤醒。此 Step 期间的发布或生命周期唤醒保持为零，
            // 不能被基于更早观察计算的延迟覆盖。
            Interlocked.Exchange(ref nextWake, long.MaxValue);
            clock = context.Clock;
            if (context.StopRequested)
            {
                controls?.CancelQueued(TransferControlStatus.StoppedBeforeExecution, true);
                Coordinator.RequestStop();
            }
            long start = context.NowTicks;
            bool eligible = !completed && !context.StopRequested && !clearRequested && journal.Status == JournalStatus.Ready;
            var local = reserveTransport && eligible ? context.LimitDeadline(Add(start, Math.Max(1, context.RemainingBudgetTicks / 4))) : context;
            var result = storage.Step(local);
            bool ran = false;
            if (!completed && !context.DeadlineReached && !context.IsCancellationRequested)
            {
                if (context.StopRequested)
                {
                    TryCloseTransport(context.NowTicks);
                    ran = true;
                }
                else if (!transportClosed)
                {
                    var journalContext = JournalStepContext.FromWorker(context);
                    if (!clearRequested)
                        RunOneControl(context);
                    if (!context.DeadlineReached)
                        Coordinator.Step(journalContext, context.NowTicks);
                    ran = true;
                    if (clearRequested && Coordinator.ClearMemoryReady && !context.DeadlineReached)
                    {
                        if (journal.Clear.IsActive)
                        {
                            if (Store.RequestClearReset(true))
                                Store.Step(journalContext, context.NowTicks);
                        }
                        else if (journal.Clear.Status == ClearStatus.Completed)
                        {
                            Store.Step(journalContext, context.NowTicks);
                            if (Store.Status == TransferStoreStatus.Ready && Store.Sequence > 1 && Store.Durable.CleanupGeneration == journal.CleanupGeneration)
                            {
                                clearRequested = false;
                                Coordinator.ResumeAfterClear();
                                controls?.ResumeAfterClear();
                            }
                        }
                    }
                }
            }
            reserveTransport = eligible && (!ran || context.DeadlineReached);
            PublishSnapshot();
            long delay = transportClosed ? long.MaxValue : Coordinator.NextDelayTicks100ns(context.NowTicks);
            if (!context.StopRequested && !clearRequested && (controls?.HasQueued ?? false))
                delay = 0;
            if (context.StopRequested && !transportClosed)
                delay = Math.Max(0, releaseAt - context.NowTicks);
            long computedWake = Math.Min(storage.NextWakeTicks, Add(context.NowTicks, delay));
            // 只有尚未被其他线程唤醒时才发布计算出的时间，避免覆盖计算期间到达的授权更新。
            Interlocked.CompareExchange(ref nextWake, computedWake, long.MaxValue);
            return new WorkerControlResult(result.Progressed || ran, result.DrainComplete && transportClosed);
        }
        private bool TryCloseTransport(long now)
        {
            if (transportClosed)
                return true;
            if (now < releaseAt || (now == long.MaxValue && lastRelease == long.MaxValue))
                return false;
            lastRelease = now;
            releaseAt = Add(now, TransferPolicy.PollTicks);
            transportClosed = Coordinator.TryDispose(now);
            return transportClosed;
        }
        public void CompletePending(WorkerFinalizationReason reason)
        {
            completed = true;
            controls?.CancelQueued(TransferControlStatus.StoppedBeforeExecution, true);
            Coordinator.CompletePending();
            Store.CompletePending();
            storage.CompletePending(reason);
            PublishSnapshot();
        }
        public void Dispose()
        {
            if (disposed)
                return;
            if (!TryCloseTransport(clock?.TimestampTicks ?? 0))
                throw new InvalidOperationException("Transport resources remain owned.");
            Store.Dispose();
            storage.Dispose();
            disposed = true;
            PublishSnapshot();
        }
        private static long Add(long now, long delay) => delay == long.MaxValue || now > long.MaxValue - delay ? long.MaxValue : now + delay;
    }
}
