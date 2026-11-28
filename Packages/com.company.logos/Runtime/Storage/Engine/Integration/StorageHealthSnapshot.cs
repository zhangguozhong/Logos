namespace Logos.Storage
{
    // 健康快照汇总两类暂存、恢复及刷盘的观测值，宿主读取时不需要打开日志文件或枚举目录。
    /// <summary>不可变数值观察；不含 I/O、密钥、消息或活跃存储引用。</summary>
    public readonly struct StorageHealthSnapshot
    {
        public JournalStatus Status
        {
            get;
        }
        public JournalBlockReason BlockReason
        {
            get;
        }
        public bool Ready => Status == JournalStatus.Ready && !MappedBufferInitializing;
        public bool Recovering => Status == JournalStatus.New || Status == JournalStatus.Recovering ||
            (Status == JournalStatus.Ready && MappedBufferInitializing);
        public bool Blocked => Status == JournalStatus.Blocked || Status == JournalStatus.DirectoryBusy;
        public JournalTimeDecisionKind TimeReason
        {
            get;
        }
        public long? EffectiveDay
        {
            get;
        }
        public long NormalBytes
        {
            get;
        }
        public long ErrorBytes
        {
            get;
        }
        public DirectoryUsage DirectoryUsage
        {
            get;
        }
        public int OwnedRecords
        {
            get;
        }
        public long CommittedRecords
        {
            get;
        }
        public long DiagnosticBudgetRejections
        {
            get;
        }
        public long RepresentedRecords
        {
            get;
        }
        public long DerivedPersistedRecords
        {
            get;
        }
        public long DroppedRecords
        {
            get;
        }
        public ClearStatus ClearStatus
        {
            get;
        }
        public long IoCalls
        {
            get;
        }
        public long WriteCalls
        {
            get;
        }
        public long OrdinaryFlushCalls
        {
            get;
        }
        public long WrittenBytes
        {
            get;
        }
        public int ForceSyncCalls
        {
            get;
        }
        public int ForceSyncSucceeded
        {
            get;
        }
        public int ForceSyncUnsupported
        {
            get;
        }
        public BlockSealStatistics NormalSeals
        {
            get;
        }
        public BlockSealStatistics ErrorSeals
        {
            get;
        }
        public bool MappedBufferActive { get; }
        public bool MappedBufferInitializing { get; }
        public long MappedBlocksPublished { get; }
        public long MappedBytesPublished { get; }
        /// <summary>包含已经写入、仅回收映射槽的情况。</summary>
        public long MappedBlocksRecovered { get; }
        public long MappedBlocksRejected { get; }

        internal StorageHealthSnapshot(JournalSession session, int owned, long committed, long dropped, StorageIoMeasurements io,
            BlockSealStatistics normalSeals, BlockSealStatistics errorSeals, long represented = 0, long derived = 0, long diagnosticBudgetRejections = 0, bool mappedActive = false, bool mappedInitializing = false,
            long mappedPublished = 0, long mappedBytes = 0, long mappedRecovered = 0, long mappedRejected = 0)
        {
            MappedBufferActive = mappedActive;
            MappedBufferInitializing = mappedInitializing;
            MappedBlocksPublished = mappedPublished;
            MappedBytesPublished = mappedBytes;
            MappedBlocksRecovered = mappedRecovered;
            MappedBlocksRejected = mappedRejected;
            DiagnosticBudgetRejections = diagnosticBudgetRejections;
            RepresentedRecords = represented;
            DerivedPersistedRecords = derived;
            NormalSeals = normalSeals;
            ErrorSeals = errorSeals;
            IoCalls = io.Calls;
            WriteCalls = io.WriteCalls;
            OrdinaryFlushCalls = io.FlushCalls;
            WrittenBytes = io.WrittenBytes;
            Status = session.Status;
            BlockReason = session.BlockReason;
            TimeReason = session.TimeDecision.Kind;
            EffectiveDay = session.CurrentEffectiveDay;
            NormalBytes = session.NormalBytes;
            ErrorBytes = session.ErrorBytes;
            DirectoryUsage = session.DirectoryUsage;
            OwnedRecords = owned;
            CommittedRecords = committed;
            DroppedRecords = dropped;
            ClearStatus = session.Clear.Status;
            ForceSyncCalls = session.ForceSyncCalls;
            ForceSyncSucceeded = session.ForceSyncSucceeded;
            ForceSyncUnsupported = session.ForceSyncUnsupported;
        }
    }
    public sealed partial class StorageBatchProcessor
    {
        private readonly object healthGate = new object();
        private long committedCount, droppedCount;
        private int stopSyncPhase;
        private bool stopSyncComplete;
        public StorageHealthSnapshot GetHealthSnapshot()
        {
            lock (healthGate)
                return health;
        }
        private void PublishHealth()
        {
            lock (healthGate)
                health = new StorageHealthSnapshot(journal, OwnedOriginalCount(), committedCount, droppedCount, io, normal.SealStatistics, error.SealStatistics, representedCount, derivedPersistedCount, diagnosticBudgetRejections,
                    mappedBuffer != null && mappedRecoverySlot == 2, mappedRequested && mappedRecoverySlot != 2,
                    mappedBlocksPublished, mappedBytesPublished, mappedBlocksRecovered, mappedBlocksRejected);
        }
    }
}
