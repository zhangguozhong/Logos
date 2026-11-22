using System;
using System.Threading;
namespace Logos.Storage
{
    /// <summary>单工作线程的有界存储所有者。构造仅分配；所有 I/O 都属于 Step。</summary>
    public sealed partial class StorageBatchProcessor : IBufferedLogBatchProcessor, IClearMemoryBarrier, ILogDiagnosticsReceiver, ILogAppLifecycleReceiver
    {
        private readonly JournalSession journal;
        private readonly StorageIoMeasurements io;
        private readonly IStorageKeyProvider keys;
        private readonly IJournalTimeAuthority authority;
        private readonly RecordCodec records;
        private readonly BlockCodec blocks;
        private readonly BoundedBlockStaging normal, error;
        private KeyMaterial encodingKey;
        private VerifiedFileHeader encodingHeader;
        private WriteRateLimiter rate;
        private ILogAdmissionController admission;
        private JournalTimeObservation observation;
        private long nextObservation, nextJournal, nextProbe, transitionDeadline, activeDay = -1;
        private long normalRetry, errorRetry, nextErrorSeal;
        private AdmissionReason normalBlocked = AdmissionReason.Accepted, errorBlocked = AdmissionReason.Accepted;
        private ulong outcomeSequence;
        private int ownerThread;
        private bool initialized, transitioning, nextClassError, barrierAttached;
        private volatile bool completed, disposed;
        private bool appendUncertainNormal, appendUncertainError;
        private long nextWake;
        private StorageHealthSnapshot health;
        public long NextWakeTicks => Interlocked.Read(ref nextWake);
        public int FixedWorkspaceBytes => journal.FixedWorkspaceBytes + records.WorkspaceBytes + blocks.WorkspaceBytes
            + normal.WorkspaceBytes + error.WorkspaceBytes + normal.ControlPayloadBytes + error.ControlPayloadBytes
            + (prepared?.WorkspaceBytes ?? 0) + (aggregation?.WorkspaceBytes ?? 0) + (incident?.WorkspaceBytes ?? 0)
            + 2048 // 诊断控制信箱、快照与有限 permit 的保守固定载荷预算。
            + 192 * sizeof(long) + 1024 * sizeof(long) + 1024;
        public LogClassMask ReadyClassMask
        {
            get
            {
                if (disposed || completed || journal.Status != JournalStatus.Ready || encodingKey == null || activeDay < 0
                    || journal.DirectoryInventoryInProgress || outcomeSequence > ulong.MaxValue - OutcomeReserve || (transitioning && transitionExpired))
                    return LogClassMask.None;
                if (prepared != null && (!prepared.IsEmpty || HasReadyDiagnostic(lastNow)))
                    return LogClassMask.None;
                LogClassMask result = LogClassMask.None;
                if ((prepared != null || normal.CanStage) && ClassCanReceive(false))
                    result |= LogClassMask.Normal;
                if ((prepared != null || error.CanStage) && ClassCanReceive(true))
                    result |= LogClassMask.ErrorFatal;
                return result;
            }
        }
        private bool transitionExpired;
        public StorageBatchProcessor(LoggingOptions loggingOptions, FrozenStorageOptions storageOptions, string directory,
            IStorageKeyProvider keys, IJournalTimeAuthority authority = null, IJournalFileSystem fileSystem = null,
            IDirectoryLeaseProvider leases = null, ILogTextSanitizer sanitizer = null)
        {
            if (storageOptions == null)
                throw new ArgumentNullException(nameof(storageOptions));
            if (directory == null)
                throw new ArgumentNullException(nameof(directory));
            this.keys = keys ?? throw new ArgumentNullException(nameof(keys));
            this.authority = authority ?? new ConservativeJournalTimeAuthority();
            try
            {
                records = new RecordCodec(loggingOptions, sanitizer);
                blocks = new BlockCodec(records);
                if (loggingOptions.Diagnostics.Enabled)
                {
                    if (loggingOptions.Diagnostics.AggregationEnabled || loggingOptions.Diagnostics.IncidentEnabled)
                        prepared = new PreparedRecordSlot();
                    if (loggingOptions.Diagnostics.AggregationEnabled)
                        aggregation = new BoundedRepeatAggregation(loggingOptions.EffectiveRecordByteLimit);
                    if (loggingOptions.Diagnostics.IncidentEnabled)
                        incident = new BoundedIncidentContext(loggingOptions.EffectiveRecordByteLimit);
                }
                normal = new BoundedBlockStaging(false, records, blocks);
                error = new BoundedBlockStaging(true, records, blocks);
                io = new StorageIoMeasurements(fileSystem ?? new SystemJournalFileSystem());
                journal = new JournalSession(storageOptions, directory, keys, io, leases);
                if (FixedWorkspaceBytes > StorageRuntimeLimits.MaxPreallocatedWorkspaceBytes)
                    throw new ArgumentException("Aggregate storage workspace exceeds one MiB.", nameof(loggingOptions));
                PublishHealth();
            }
            catch
            {
                prepared?.Dispose();
                aggregation?.Dispose();
                incident?.Dispose();
                error?.Dispose();
                normal?.Dispose();
                blocks?.Dispose();
                records?.Dispose();
                journal?.Dispose();
                throw;
            }
        }
        private void CheckWorker()
        {
            int thread = Thread.CurrentThread.ManagedThreadId;
            if (ownerThread == 0)
                ownerThread = thread;
            else if (ownerThread != thread)
                throw new InvalidOperationException("Storage methods belong to the Core worker.");
            if (disposed)
                throw new ObjectDisposedException(nameof(StorageBatchProcessor));
        }
        public BatchProcessResult ProcessBatch(LogBatchView batch, BatchProcessingContext context)
            => throw new NotSupportedException("Use the buffered Core worker ownership protocol.");
        public BatchTransferResult TransferBatch(LogBatchView batch, WorkerControlContext context)
        {
            CheckWorker();
            if (context.IsCancellationRequested)
                return Pause(TransferPauseReason.Cancel);
            if (context.DeadlineReached)
                return Pause(TransferPauseReason.Budget);
            if (batch.Count != 1)
                throw new ArgumentException("The storage protocol transfers one pinned head.", nameof(batch));
            lastNow = context.NowTicks;
            DiagnosticControlBoundaryChanged();
            LogRecord record = batch[0];
            if (record == null || !record.QuotaDayTag.HasValue || record.QuotaDayTag.Value.DayId != activeDay)
                return Failed(AdmissionReason.StaleQuotaDay);
            bool isError = record.Level >= LogLevel.Error;
            var staging = isError ? error : normal;
            if (completed || journal.Status != JournalStatus.Ready || encodingKey == null || outcomeSequence > ulong.MaxValue - OutcomeReserve
                || (transitioning && transitionExpired) || (prepared == null ? !staging.CanStage : !prepared.IsEmpty || HasReadyDiagnostic(context.NowTicks)) || !ClassCanReceive(isError))
            {
                staging.RequestSeal(BlockSealReason.Backpressure);
                WakeAt(context.NowTicks);
                return Pause(TransferPauseReason.Backpressure);
            }
            if (prepared != null)
            {
                if (!prepared.Prepare(record, records, false, out var prepareFailure))
                    return Failed(prepareFailure == RecordEncodeFailure.SanitizeFailed ? AdmissionReason.RedactionFailed : AdmissionReason.EncodingFailed);
                ObservePreparedRecord(prepared);
                WakeAt(context.NowTicks);
                PublishHealth();
                return new BatchTransferResult(1);
            }
            // 日期身份具有权威性。同日门禁代际变化不会使已接收输入失效。
            StageResult result = staging.TryStage(record, encodingHeader, encodingKey, context.NowTicks, out var failure);
            if (result == StageResult.Staged)
            {
                if (isError && context.NowTicks >= nextErrorSeal)
                {
                    staging.RequestSeal(BlockSealReason.FirstError);
                    nextErrorSeal = Add(context.NowTicks, StorageRuntimeLimits.FaultSyncIntervalTicks);
                }
                RecomputeWake(context.NowTicks);
                PublishHealth();
                return new BatchTransferResult(1);
            }
            if (result == StageResult.Rejected)
                return Failed(failure == RecordEncodeFailure.SanitizeFailed ? AdmissionReason.RedactionFailed : AdmissionReason.EncodingFailed);
            WakeAt(context.NowTicks);
            return Pause(TransferPauseReason.Backpressure);
        }
        private static BatchTransferResult Pause(TransferPauseReason reason) => new BatchTransferResult(0, pauseReason: reason);
        private static BatchTransferResult Failed(AdmissionReason reason) => new BatchTransferResult(0, true, reason);
        public bool TryPeekOutcome(out LogStorageOutcome outcome)
        {
            CheckWorker();
            bool n = normal.TryPeekOutcome(out var a), e = error.TryPeekOutcome(out var b);
            outcome = n && (!e || a.Sequence < b.Sequence) ? a : b;
            bool found = n || e;
            if (prepared != null && prepared.Peek(out var p) && (!found || p.Sequence < outcome.Sequence))
            {
                outcome = p;
                found = true;
            }
            if (aggregation != null && aggregation.Peek(out var g) && (!found || g.Sequence < outcome.Sequence))
            {
                outcome = g;
                found = true;
            }
            return found;
        }
        // Core 对已发布结果的确认是释放对应存储所有权的边界；封块和写盘都可能早于这次调用。
        public bool AckOutcome(ulong sequence)
        {
            CheckWorker();
            bool ack = normal.AckOutcome(sequence) || error.AckOutcome(sequence) || (prepared?.Ack(sequence) ?? false) || (aggregation?.Ack(sequence) ?? false);
            if (ack)
            {
                WakeAt(0);
                PublishHealth();
            }
            return ack;
        }
        private void Publish(BoundedBlockStaging staging, AdmissionReason reason, bool committed = false)
        {
            if (staging.IsEmpty || staging.HasOutcome)
                return;
            var kind = committed ? LogStorageOutcomeKind.Committed : LogStorageOutcomeKind.Dropped;
            if (!staging.BeginResolution(kind))
                return;
            int direct = staging.DirectCount, wire = staging.Count;
            if (direct != 0 && !staging.PublishDirectOutcome(NextOutcomeSequence(), kind, committed ? AdmissionReason.Accepted : reason))
                throw new InvalidOperationException("Reserved outcome publication failed.");
            if (committed)
            {
                committedCount += direct;
                derivedPersistedCount += wire - direct;
            }
            else
                droppedCount += direct;
            for (int i = 0; i < wire; i++)
            {
                var token = staging.TokenAt(i);
                if (!token.IsSynthetic)
                    NotifyRecordTerminal(staging.SequenceAt(i), kind, committed ? AdmissionReason.Accepted : reason, null);
                ResolveToken(token, reason, committed);
            }
            if (direct == 0)
                staging.FinishSyntheticResolution();
        }
        public void CompletePending(WorkerFinalizationReason reason)
        {
            CheckWorker();
            if (completed)
                return;
            completed = true;
            InvalidateDiagnosticBudget();
            AdmissionReason drop = reason == WorkerFinalizationReason.ProcessorFault ? AdmissionReason.StorageFault : AdmissionReason.ShutdownDropped;
            Publish(normal, appendUncertainNormal ? AdmissionReason.UncertainIo : drop);
            Publish(error, appendUncertainError ? AdmissionReason.UncertainIo : drop);
            DropDiagnostic(drop);
            PublishHealth();
        }
        public void Dispose()
        {
            if (disposed)
                return;
            CheckWorker();
            // Journal 最后释放目录所有者；若实际关闭前失败，则保留它。
            prepared?.Dispose();
            aggregation?.Dispose();
            incident?.Dispose();
            normal.Dispose();
            error.Dispose();
            encodingKey?.Dispose();
            encodingKey = null;
            blocks.Dispose();
            records.Dispose();
            journal.Dispose();
            disposed = true;
            PublishHealth();
        }
        private void WakeAt(long ticks) => Interlocked.Exchange(ref nextWake, ticks);
        private static long Add(long value, long delta) => value > long.MaxValue - delta ? long.MaxValue : value + delta;
    }
}
