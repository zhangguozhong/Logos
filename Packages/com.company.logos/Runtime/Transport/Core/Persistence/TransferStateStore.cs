using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using Logos.Storage;
namespace Logos.Transport
{
    internal enum TransferStoreFailureKind
    {
        None, IoResynchronization, AuthenticatedRecovery
    }
    public enum TransferStoreStatus
    {
        WaitingJournal, Loading, Ready, Saving, Blocked, Disposed
    }
    internal readonly struct TransferSaveContext
    {
        internal DateTime TrustedUtc
        {
            get;
        }
        internal long Total
        {
            get;
        }
        internal long Error
        {
            get;
        }
        internal bool FreshAuthorization
        {
            get;
        }
        internal TransferSaveContext(DateTime utc, long total, long error, bool fresh)
        {
            TransferValidation.Utc(utc, nameof(utc));
            if (total <= 0 || error <= 0 || error >= total)
                throw new ArgumentException("Finite positive network budgets required.");
            TrustedUtc = utc;
            Total = total;
            Error = error;
            FreshAuthorization = fresh;
        }
    }
    // 所有入口都属于原 Journal 工作线程。不拥有数据流或租约。
    // 一个页面数组；编解码器另用逻辑明文 98304、密文 98320 字节及加密临时区。
    // 解密直接写入零填充的逻辑明文，FinalBlock 另有不超过 32 字节尾部。
    // 恢复或下界合并可能同时保留持久状态、两个解码状态和合并候选：
    // 最多 16 条有界记录和 4 个不可变正文。克隆时的临时 CopyBody
    // 使正文数组载荷的保守上界达到 5 乘 67424 字节，独立于编解码器的
    // 明文、密文和 A 导出器工作区。这些是物理副本，
    // 不表示逻辑上仅一个待处理项，也不代表托管或原生堆总量。
    internal sealed partial class TransferStateStore : IDisposable
    {
        private readonly JournalSession journal;
        private readonly IStorageKeyProvider keys;
        private readonly string installation, destination;
        private const long IoRetryDelayTicks100ns = 30 * TimeSpan.TicksPerSecond;
        private long ioRetryDeadline;
        private bool ioRetrySaturated;
        internal TransferStoreFailureKind FailureKind
        {
            get;
            private set;
        }
        private readonly MetadataPageLimiter limiter = new MetadataPageLimiter(0);
        private TransferStateCodec codec;
        private byte[] page = new byte[TransferStateCodec.PageBytes];
        private StatePageResult first, second, selected;
        private byte[] firstHash, secondHash, expectedHash;
        private TransferState candidate;
        private int phase, ownerThread, selectedSlot, publishingVersion = TransferStateCodec.CurrentVersion, publishingBytes;
        private bool stopped, resetRequested, clearReset, controlWrite, clearEpochResetDurable, clearGate;
        // 归一化失败或重新加载期间保留；绝不授权重建损坏页面。
        private long normalizationEpoch = -1;
        private ulong publishingSequence, highestIssuedSequence;
        private long sequenceEpoch = -1;
        private long floorDay = -1, floorU, floorV, floorRevoked = -1, floorThrottle;
        internal long KnownGlobalThrottleUtc => floorThrottle;
        private ulong normalFloor, errorFloor;
        private readonly ulong[] knownSlotGenerations = new ulong[4];
        private readonly long[] knownSourceUsed = new long[4], knownEncodedUsed = new long[4];
        private readonly int[] knownBatches = new int[4];
        private long knownTasksEpoch = -1;
        private ulong knownPendingGeneration;
        private byte[] knownPendingHash;
        private int knownPendingAttempts;
        private int sourceReservationSlot = -1;
        private long sourceReservationAmount;
        private SourceReadGrant activeGrant, issuedGrant, sourceReservationExisting;
        private bool attemptReady, attemptReservationPending;
        internal TransferStoreStatus Status
        {
            get;
            private set;
        }
        = TransferStoreStatus.WaitingJournal;
        internal TransferState Durable
        {
            get;
            private set;
        }
        internal ulong Sequence
        {
            get;
            private set;
        }
        internal string BlockReason
        {
            get;
            private set;
        }
        internal string StateKey0
        {
            get;
            private set;
        }
        internal string StateKey1
        {
            get;
            private set;
        }
        private bool referencesKnown0, referencesKnown1;
        internal bool PageReferencesKnown => referencesKnown0 && referencesKnown1;
        internal string PendingExportKey0
        {
            get;
            private set;
        }
        internal string PendingExportKey1
        {
            get;
            private set;
        }
        private bool clearBarrierVerified, clearOperationsClosed;
        private long clearBarrierGeneration = -1, clearOperationGeneration = -1;
        private bool HasCurrentClearAuthorization => clearOperationsClosed
            && journal.TransferRegionAvailable && journal.CleanupGeneration == clearOperationGeneration
            && journal.TransferClearProofInMemory;
        // 这是当前 phase2 意图的实时证明，绝不是历史完成标记。
        internal bool ClearBarrierReady
        {
            get
            {
                if (Status == TransferStoreStatus.Disposed)
                    return false;
                CheckOwner();
                if (!clearBarrierVerified)
                    return false;
                if (Status == TransferStoreStatus.Ready && HasCurrentClearAuthorization
                    && clearBarrierGeneration == clearOperationGeneration && Durable != null
                    && Durable.CleanupGeneration == clearBarrierGeneration && Durable.IsReset && Sequence == 1)
                    return true;
                RevokeClearAuthorization();
                return false;
            }
        }
        private void RevokeClearAuthorization()
        {
            clearBarrierVerified = clearOperationsClosed = resetRequested = false;
            clearBarrierGeneration = clearOperationGeneration = -1;
        }
        internal TransferStateStore(JournalSession journal, IStorageKeyProvider keys, string installation, string destination)
        {
            this.journal = journal ?? throw new ArgumentNullException(nameof(journal));
            this.keys = keys ?? throw new ArgumentNullException(nameof(keys));
            this.installation = TransferValidation.Id(installation, nameof(installation));
            this.destination = TransferValidation.Id(destination, nameof(destination));
        }
        private void CheckOwner()
        {
            int current = Thread.CurrentThread.ManagedThreadId;
            if (ownerThread == 0)
                ownerThread = current;
            else if (ownerThread != current)
                throw new InvalidOperationException("Transfer state belongs to the original worker.");
            if (Status == TransferStoreStatus.Disposed)
                throw new ObjectDisposedException(nameof(TransferStateStore));
        }
        private static byte[] Hash(byte[] bytes, int count)
        {
            using (var sha = SHA256.Create())
                return sha.ComputeHash(bytes, 0, count);
        }
        private static bool Equal(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length)
                return false;
            int n = 0;
            for (int i = 0; i < a.Length; i++)
                n |= a[i] ^ b[i];
            return n == 0;
        }
        private long lastStepTicks;
        private void Block(string reason)
        {
            FailureKind = TransferStoreFailureKind.AuthenticatedRecovery;
            long now = Math.Max(0, lastStepTicks);
            ioRetrySaturated = now > long.MaxValue - IoRetryDelayTicks100ns;
            ioRetryDeadline = ioRetrySaturated ? long.MaxValue : now + IoRetryDelayTicks100ns;
            RevokeClearAuthorization();
            BlockReason = reason;
            Status = TransferStoreStatus.Blocked;
            candidate = null;
            first = second = selected = null;
            InvalidateGrant();
            attemptReady = false;
        }
        private void BeginLoad()
        {
            Status = TransferStoreStatus.Loading;
            phase = 0;
            candidate = null;
            first = second = selected = null;
            sourceReservationSlot = -1;
            sourceReservationAmount = 0;
            sourceReservationExisting = null;
            attemptReady = attemptReservationPending = false;
            InvalidateGrant();
            RevokeClearAuthorization();
            referencesKnown0 = referencesKnown1 = false;
        }
        // Core 单调时钟域中的纯内存截止时间，单位 100 ns。清理或重载请求
        // 不能擦除或延长它。无效的回退样本不改变锚点。
        private long IoRetryDelay(long now)
        {
            if (FailureKind == TransferStoreFailureKind.None)
                return 0;
            // 无法表示完整剩余区间：安全拒绝，避免零延迟循环。
            if (ioRetrySaturated)
                return IoRetryDelayTicks100ns;
            now = Math.Max(0, now);
            return now >= ioRetryDeadline ? 0 : Math.Min(IoRetryDelayTicks100ns, ioRetryDeadline - now);
        }
        private bool CanRecoverBlocked => !clearGate || (!journal.Clear.IsActive && journal.Clear.Status == ClearStatus.Completed
            && Durable != null && Durable.CleanupGeneration == journal.CleanupGeneration && Durable.IsReset
            && ((Sequence == 1 && normalizationEpoch == journal.CleanupGeneration) || (Sequence >= 2 && Durable.UnknownUsage)));
        internal long NextDelayTicks100ns(long nowTicks100ns)
        {
            CheckOwner();
            if (stopped || !journal.TransferRegionAvailable || (Durable == null && journal.Clear.IsActive && !resetRequested) || (clearGate && journal.Clear.IsActive && !HasCurrentClearAuthorization) || (Status == TransferStoreStatus.Blocked && !CanRecoverBlocked) || Status == TransferStoreStatus.Ready || Status == TransferStoreStatus.WaitingJournal)
                return long.MaxValue;
            long ioDelay = IoRetryDelay(nowTicks100ns);
            if (ioDelay > 0)
                return ioDelay;
            return Status == TransferStoreStatus.Saving && (phase == 11 || phase == 15) ? limiter.NextDelayTicks100ns(nowTicks100ns, controlWrite) : 0;
        }
        internal bool RequestRecoveryRetry(long nowTicks100ns)
        {
            CheckOwner();
            if (stopped || Status != TransferStoreStatus.Blocked || !CanRecoverBlocked || IoRetryDelay(nowTicks100ns) > 0)
                return false;
            BeginLoad();
            FailureKind = TransferStoreFailureKind.None;
            BlockReason = null;
            return true;
        }
        internal void Step(JournalStepContext context, long nowTicks100ns)
        {
            CheckOwner();
            if (stopped || context.ShouldYield || context.StopRequested)
                return;
            if (!journal.TransferRegionAvailable)
                return;
            lastStepTicks = Math.Max(lastStepTicks, Math.Max(0, nowTicks100ns));
            if (Status == TransferStoreStatus.Blocked && !resetRequested && !RequestRecoveryRetry(nowTicks100ns))
                return;
            if (Durable == null && journal.Clear.IsActive && !resetRequested)
                return;
            if (clearGate && journal.Clear.IsActive && !HasCurrentClearAuthorization)
                return;
            if (IoRetryDelay(nowTicks100ns) > 0)
                return;
            journal.CheckTransferOwner();
            if (FailureKind != TransferStoreFailureKind.None)
            {
                FailureKind = TransferStoreFailureKind.None;
                BlockReason = null;
            }
            try
            {
                if (codec == null)
                {
                    codec = new TransferStateCodec(keys, journal.CopyTransferDirectoryId(), installation, destination);
                    if (Status == TransferStoreStatus.WaitingJournal)
                        BeginLoad();
                    return;
                }
                if (Status == TransferStoreStatus.WaitingJournal)
                {
                    BeginLoad();
                    return;
                }
                if (Status == TransferStoreStatus.Loading)
                {
                    LoadStep();
                    return;
                }
                if (Status == TransferStoreStatus.Saving)
                {
                    SaveStep(nowTicks100ns);
                    return;
                }
                if (Status == TransferStoreStatus.Ready && clearEpochResetDurable && journal.Clear.Status == ClearStatus.Completed)
                {
                    BeginNormalizeReset();
                    return;
                }
                if (Status == TransferStoreStatus.Ready && journal.CleanupGeneration != Durable.CleanupGeneration)
                {
                    InvalidateGrant();
                    attemptReady = false;
                    Block("Journal cleanup generation changed; verified clear required.");
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                BeginLoad();
                BlockReason = "IO requires authenticated resynchronization.";
                FailureKind = TransferStoreFailureKind.IoResynchronization;
                long now = Math.Max(0, nowTicks100ns);
                ioRetrySaturated = now > long.MaxValue - IoRetryDelayTicks100ns;
                ioRetryDeadline = ioRetrySaturated ? long.MaxValue : now + IoRetryDelayTicks100ns;
            }
            catch (Exception ex) when (ex is CryptographicException || ex is ArgumentException || ex is InvalidOperationException)
            {
                if (page == null)
                    page = new byte[TransferStateCodec.PageBytes];
                Block("State codec or transition unavailable: " + ex.GetType().Name);
            }
        }
        private bool HasClearAuthority => resetRequested && HasCurrentClearAuthorization;
        private void LoadStep()
        {
            switch (phase)
            {
                case 0:
                    journal.ReadTransferPage(0, page);
                    phase = 1;
                    return;
                case 1:
                    first = codec.Decode(page);
                    firstHash = first.Status == StatePageStatus.Supported ? Hash(page, first.RecordBytes) : null;
                    SetReferences(0, first);
                    phase = 2;
                    return;
                case 2:
                    journal.ReadTransferPage(1, page);
                    phase = 3;
                    return;
                case 3:
                    second = codec.Decode(page);
                    secondHash = second.Status == StatePageStatus.Supported ? Hash(page, second.RecordBytes) : null;
                    SetReferences(1, second);
                    phase = 4;
                    return;
                case 4:
                    SelectPages();
                    return;
                case 5:
                    journal.ReadTransferPage(selectedSlot, page);
                    phase = 6;
                    return;
                case 6:
                    if (!Equal(Hash(page, selected.RecordBytes), expectedHash))
                    {
                        BeginLoad();
                        return;
                    }
                    // 采用完全一致的已认证密文。两个修复副本均使用原始 IV。
                    candidate = selected.State;
                    publishingVersion = selected.Version;
                    publishingBytes = selected.RecordBytes;
                    publishingSequence = selected.Sequence;
                    RememberSequence(candidate.CleanupGeneration, publishingSequence);
                    clearReset = selected.ClearReset;
                    controlWrite = clearReset || candidate.RevokedThrough > floorRevoked;
                    first = second = selected = null;
                    phase = 11;
                    Status = TransferStoreStatus.Saving;
                    return;
            }
        }
        private static bool Fatal(StatePageResult p) => p.Status == StatePageStatus.MissingKey || p.Status == StatePageStatus.AuthenticatedUnknown || p.Status == StatePageStatus.BindingMismatch;
        // 选择恢复源时要同时检查两个副本。较旧的有效页也可能包含仍需保留的限流下限。
        private void SelectPages()
        {
            bool a = first.Status == StatePageStatus.Supported, b = second.Status == StatePageStatus.Supported;
            long generation = journal.CleanupGeneration;
            if (a)
                floorThrottle = Math.Max(floorThrottle, first.State.GlobalThrottleUtc);
            if (b)
                floorThrottle = Math.Max(floorThrottle, second.State.GlobalThrottleUtc);
            if (HasClearAuthority)
            {
                bool ar = a && first.CleanupGeneration == generation && first.ClearReset, br = b && second.CleanupGeneration == generation && second.ClearReset;
                if (ar || br)
                {
                    if (ar && br && !Equal(firstHash, secondHash))
                    {
                        Block("Conflicting current clear reset pages.");
                        return;
                    }
                    selectedSlot = ar ? 0 : 1;
                    selected = ar ? first : second;
                    expectedHash = ar ? firstHash : secondHash;
                    phase = 5;
                    return;
                }
                // 当前重置 epoch 始终从序列 1 开始，绝不继承旧序列。
                var known = KnownClearState(a, b);
                if (a)
                    ObserveFloor(first.State);
                if (b)
                    ObserveFloor(second.State);
                if (known != null)
                    known = MergeKnownFloors(known);
                candidate = known == null
                    ? new TransferState(networkDay: journal.TransferEffectiveDay, originalU: floorU, originalV: floorV, normalUsed: normalFloor, errorUsed: errorFloor, cleanupGeneration: generation, revokedThrough: floorRevoked, unknownUsage: true, globalThrottleUtc: floorThrottle)
                    : new TransferState(known.NetworkDay, known.OriginalU, known.OriginalV, known.NormalUsed, known.ErrorUsed, generation, known.RevokedThrough, known.UnknownUsage, globalThrottleUtc: known.GlobalThrottleUtc);
                first = second = null;
                sequenceEpoch = generation;
                highestIssuedSequence = 1;
                publishingSequence = 1;
                clearReset = controlWrite = true;
                phase = 10;
                Status = TransferStoreStatus.Saving;
                return;
            }
            // 存留且经过认证的当前 epoch 转换，只能在 Journal 完成后
            // 完成其空 seq2 转换。没有这种重置页面的历史 Completed
            // 绝不授权重建损坏存储。
            bool currentResetA = a && first.CleanupGeneration == generation && first.ClearReset;
            bool currentResetB = b && second.CleanupGeneration == generation && second.ClearReset;
            if (journal.TransferClearCompleted && (currentResetA || currentResetB) &&
                first.Status != StatePageStatus.BindingMismatch && second.Status != StatePageStatus.BindingMismatch &&
                !(a && !first.ClearReset && first.CleanupGeneration == generation) && !(b && !second.ClearReset && second.CleanupGeneration == generation))
            {
                if (currentResetA && currentResetB && !Equal(firstHash, secondHash))
                {
                    Block("Conflicting current reset pages.");
                    return;
                }
                selected = currentResetA ? first : second;
                var empty = MergeKnownFloors(selected.State);
                if (empty == null || !empty.IsReset)
                {
                    Block("Current reset cannot preserve known floors.");
                    return;
                }
                Durable = empty;
                Sequence = 1;
                ObserveFloor(empty);
                first = second = selected = null;
                BeginNormalizeReset();
                return;
            }
            // 任一页缺少密钥、绑定不符或采用未知的已认证格式时，旧副本无法证明自己是最新状态。
            if (Fatal(first) || Fatal(second))
            {
                Block("Missing key, unknown authenticated schema or identity mismatch.");
                return;
            }
            if (!a && !b)
            {
                if (first.Status == StatePageStatus.Zero && second.Status == StatePageStatus.Zero && floorDay < 0 && floorRevoked < 0 && highestIssuedSequence == 0)
                {
                    Durable = new TransferState(cleanupGeneration: generation);
                    Sequence = 0;
                    first = second = null;
                    Status = TransferStoreStatus.Ready;
                    return;
                }
                Block("Nonzero state has no authenticated recovery proof.");
                return;
            }
            selectedSlot = !b || (a && (first.CleanupGeneration > second.CleanupGeneration || (first.CleanupGeneration == second.CleanupGeneration && first.Sequence >= second.Sequence))) ? 0 : 1;
            selected = selectedSlot == 0 ? first : second;
            expectedHash = selectedSlot == 0 ? firstHash : secondHash;
            if (a && b && first.CleanupGeneration == second.CleanupGeneration && first.Sequence == second.Sequence && !Equal(firstHash, secondHash))
            {
                Block("Same sequence has different ciphertext.");
                return;
            }
            RememberSequence(selected.CleanupGeneration, selected.Sequence);
            if (selected.CleanupGeneration != generation)
            {
                Block("Transfer/Journal cleanup binding differs.");
                return;
            }
            var merged = MergeKnownFloors(selected.State);
            if (merged == null)
            {
                Block("Authenticated recovery cannot preserve known floors.");
                return;
            }
            if (!SameState(merged, selected.State))
            {
                if (Math.Max(selected.Sequence, highestIssuedSequence) == ulong.MaxValue)
                {
                    Block("State sequence exhausted during resync.");
                    return;
                }
                candidate = merged;
                publishingSequence = Math.Max(selected.Sequence, highestIssuedSequence) + 1;
                RememberSequence(merged.CleanupGeneration, publishingSequence);
                clearReset = false;
                controlWrite = merged.RevokedThrough > selected.State.RevokedThrough;
                first = second = selected = null;
                phase = 10;
                Status = TransferStoreStatus.Saving;
                return;
            }
            ObserveFloor(selected.State);
            if (a && b && Equal(firstHash, secondHash))
            {
                Durable = selected.State;
                Sequence = selected.Sequence;
                bool reset = selected.ClearReset;
                bool migrate = selected.Version < TransferStateCodec.CurrentVersion;
                first = second = selected = null;
                if (reset)
                {
                    BeginNormalizeReset();
                    return;
                }
                if (migrate)
                {
                    BeginMigration();
                    return;
                }
                Status = TransferStoreStatus.Ready;
                return;
            }
            phase = 5;
        }
        // 每页分别写入、刷盘、回读并验证。第一份成功后再处理第二份，使中途断电可从有效副本恢复。
        private void SaveStep(long now)
        {
            if (clearReset && !HasCurrentClearAuthorization)
            {
                BeginLoad();
                return;
            }
            switch (phase)
            {
                case 10:
                    publishingVersion = TransferStateCodec.CurrentVersion;
                    publishingBytes = codec.EncodeInto(candidate, publishingSequence, page, clearReset);
                    expectedHash = Hash(page, publishingBytes);
                    phase = 11;
                    return;
                case 11:
                    if (!limiter.TryTake(now, controlWrite))
                        return;
                    referencesKnown0 = false;
                    journal.WriteTransferPage(0, page, publishingBytes);
                    phase = 12;
                    return;
                case 12:
                    journal.FlushTransferPage();
                    phase = 13;
                    return;
                case 13:
                    journal.ReadTransferPage(0, page);
                    phase = 14;
                    return;
                case 14:
                    VerifyReadback(0);
                    phase = 15;
                    return;
                case 15:
                    if (!limiter.TryTake(now, controlWrite))
                        return;
                    referencesKnown1 = false;
                    journal.WriteTransferPage(1, page, publishingBytes);
                    phase = 16;
                    return;
                case 16:
                    journal.FlushTransferPage();
                    phase = 17;
                    return;
                case 17:
                    journal.ReadTransferPage(1, page);
                    phase = 18;
                    return;
                case 18:
                    VerifyReadback(1);
                    Publish();
                    return;
            }
        }
        private void SetReferences(int slot, StatePageResult result)
        {
            string export = null;
            bool known = result.Status == StatePageStatus.Zero || result.Status == StatePageStatus.Supported;
            if (result.State?.Pending != null)
                known = result.State.Pending.TryGetExportKeyReference(out export);
            if (slot == 0)
            {
                StateKey0 = result.KeyId;
                PendingExportKey0 = export;
                referencesKnown0 = known;
            }
            else
            {
                StateKey1 = result.KeyId;
                PendingExportKey1 = export;
                referencesKnown1 = known;
            }
        }
        private void VerifyReadback(int slot)
        {
            if (!Equal(Hash(page, publishingBytes), expectedHash))
                throw new IOException("Transfer page readback differs.");
            var result = codec.Decode(page);
            if (result.Status != StatePageStatus.Supported || result.Sequence != publishingSequence || result.CleanupGeneration != candidate.CleanupGeneration || result.ClearReset != clearReset || result.Version != publishingVersion || result.RecordBytes != publishingBytes)
                throw new IOException("Transfer page readback authentication failed.");
            SetReferences(slot, result);
        }
        // 两个目标页都通过回读后才发布候选状态，下游才能据此领取读取额度或发送资格。
        private void Publish()
        {
            if (clearReset && (!HasCurrentClearAuthorization || candidate.CleanupGeneration != clearOperationGeneration))
            {
                BeginLoad();
                return;
            }
            Durable = candidate;
            candidate = null;
            Sequence = publishingSequence;
            ObserveFloor(Durable);
            if (clearReset)
            {
                RememberSequence(Durable.CleanupGeneration, 1);
                InvalidateGrant();
                attemptReady = false;
                clearBarrierGeneration = Durable.CleanupGeneration;
                clearBarrierVerified = true;
                clearEpochResetDurable = true;
                resetRequested = false;
                Status = TransferStoreStatus.Ready;
                return;
            }
            if (sourceReservationSlot >= 0)
            {
                var row = Durable.Slot(sourceReservationSlot);
                if (sourceReservationExisting != null && ReferenceEquals(sourceCredits[sourceReservationSlot], sourceReservationExisting) && sourceReservationExisting.Valid)
                    sourceReservationExisting.AddCredit(sourceReservationAmount);
                else
                    sourceCredits[sourceReservationSlot] = new SourceReadGrant(this, sourceReservationSlot, row, sourceReservationAmount);
                issuedGrant = sourceCredits[sourceReservationSlot];
                sourceReservationExisting = null;
                sourceReservationSlot = -1;
                sourceReservationAmount = 0;
            }
            if (attemptReservationPending && Durable.Pending?.Phase == PendingPhase.AttemptReserved && Durable.GlobalThrottleUtc >= floorThrottle)
                attemptReady = true;
            attemptReservationPending = false;
            if (publishingVersion < TransferStateCodec.CurrentVersion)
            {
                BeginMigration();
                return;
            }
            normalizationEpoch = -1;
            if (!Durable.UnknownUsage)
                clearGate = false;
            Status = TransferStoreStatus.Ready;
        }
        private TransferState KnownClearState(bool a, bool b)
        {
            if (Fatal(first) || Fatal(second))
                return null;
            if (a && b && first.CleanupGeneration == second.CleanupGeneration && first.Sequence == second.Sequence && !Equal(firstHash, secondHash))
                return null;
            if (!a && !b)
                return first.Status == StatePageStatus.Zero && second.Status == StatePageStatus.Zero && floorDay < 0 && floorRevoked < 0 && highestIssuedSequence == 0 ? new TransferState() : null;
            var best = !b || (a && (first.CleanupGeneration > second.CleanupGeneration || (first.CleanupGeneration == second.CleanupGeneration && first.Sequence >= second.Sequence))) ? first : second;
            return MergeKnownFloors(best.State);
        }
        private void BeginMigration()
        {
            if (Math.Max(Sequence, highestIssuedSequence) == ulong.MaxValue)
            {
                Block("State sequence exhausted during legacy migration.");
                return;
            }
            candidate = MergeKnownFloors(Durable);
            if (candidate == null)
            {
                Block("Migration cannot preserve known floors.");
                return;
            }
            publishingSequence = Math.Max(Sequence, highestIssuedSequence) + 1;
            RememberSequence(candidate.CleanupGeneration, publishingSequence);
            clearReset = false;
            controlWrite = false;
            phase = 10;
            Status = TransferStoreStatus.Saving;
        }
        private void BeginNormalizeReset()
        {
            normalizationEpoch = Durable.CleanupGeneration;
            RevokeClearAuthorization();
            RememberSequence(Durable.CleanupGeneration, Sequence);
            if (highestIssuedSequence == ulong.MaxValue)
            {
                Block("State sequence exhausted during empty reset transition.");
                return;
            }
            clearEpochResetDurable = false;
            candidate = MergeKnownFloors(Durable);
            if (candidate == null)
            {
                Block("Clear transition cannot preserve known floors.");
                return;
            }
            publishingSequence = Math.Max(1, highestIssuedSequence) + 1;
            RememberSequence(Durable.CleanupGeneration, publishingSequence);
            clearReset = false;
            controlWrite = true;
            phase = 10;
            Status = TransferStoreStatus.Saving;
        }
        private void RememberSequence(long generation, ulong sequence)
        {
            if (sequenceEpoch != generation)
            {
                sequenceEpoch = generation;
                highestIssuedSequence = sequence;
            }
            else
                highestIssuedSequence = Math.Max(highestIssuedSequence, sequence);
        }
        private TransferState MergeKnownFloors(TransferState s)
        {
            if (floorDay > s.NetworkDay && (floorU <= 0 || floorV <= 0))
                return null;
            if (floorDay == s.NetworkDay && s.OriginalU != floorU && floorDay >= 0)
                return null;
            if (floorDay == s.NetworkDay && s.OriginalV != floorV && floorDay >= 0)
                return null;
            var rows = Rows(s);
            var pending = s.Pending;
            for (int i = 0; i < 4; i++)
            {
                var row = rows[i];
                if (row == null || row.Generation != knownSlotGenerations[i])
                    continue;
                rows[i] = new TaskSlot(row.Request, row.Generation, row.Revision, row.CleanupGeneration, row.State, row.AutoError, row.MaxRecords, Math.Max(row.GeneratedBatches, knownBatches[i]), Math.Max(row.SourceUsed, knownSourceUsed[i]), Math.Max(row.EncodedUsed, knownEncodedUsed[i]), row.Bookmark, row.Attempts, row.Splits, row.Reason, row.RetryUtc, row.IdentityCoverageGap);
            }
            if (pending != null && pending.SlotGeneration == knownPendingGeneration && Equal(pending.CopyHash(), knownPendingHash) && pending.Attempts < knownPendingAttempts)
            {
                pending = new PendingBatch(PendingPhase.AttemptReserved, pending.IsError, knownPendingAttempts, pending.Splits, pending.RecordCount, pending.Slot, pending.SlotGeneration, pending.Revision, pending.CleanupGeneration, pending.RetryUtc, pending.Before, pending.After, pending.CopyBody());
                rows[pending.Slot] = CopyTask(rows[pending.Slot], state: TransferTaskState.Pending, attempts: knownPendingAttempts);
            }
            long revoked = Math.Max(s.RevokedThrough, floorRevoked);
            for (int i = 0; i < 4; i++)
                if (rows[i] != null && rows[i].Revision <= revoked)
                {
                    if (pending?.Slot == i)
                        pending = null;
                    rows[i] = CopyTask(rows[i], state: TransferTaskState.Canceled);
                }
            bool sameDay = s.NetworkDay == floorDay, newerFloor = floorDay > s.NetworkDay;
            return new TransferState(newerFloor ? floorDay : s.NetworkDay, newerFloor ? floorU : s.OriginalU, newerFloor ? floorV : s.OriginalV, newerFloor ? normalFloor : sameDay ? Math.Max(s.NormalUsed, normalFloor) : s.NormalUsed, newerFloor ? errorFloor : sameDay ? Math.Max(s.ErrorUsed, errorFloor) : s.ErrorUsed, s.CleanupGeneration, revoked, s.UnknownUsage, s.NextSlotGeneration, rows, pending, s.AutoBookmark, s.AutoTaskCounter, Math.Max(s.GlobalThrottleUtc, floorThrottle));
        }
        private bool MeetsFloor(TransferState s) => s.GlobalThrottleUtc >= floorThrottle && s.RevokedThrough >= floorRevoked && (floorDay < 0 || s.NetworkDay > floorDay || (s.NetworkDay == floorDay && s.OriginalU == floorU && s.OriginalV == floorV && s.NormalUsed >= normalFloor && s.ErrorUsed >= errorFloor));
        internal void ObserveGlobalThrottle(long trustedUtcTicks)
        {
            CheckOwner();
            if (trustedUtcTicks < 0 || trustedUtcTicks > DateTime.MaxValue.Ticks)
                throw new ArgumentOutOfRangeException(nameof(trustedUtcTicks));
            if (trustedUtcTicks <= floorThrottle)
                return;
            floorThrottle = trustedUtcTicks;
            attemptReady = false;
        }
        // 记录进程已见的用量与撤销进度，后续磁盘恢复时用它们检查是否发生回退。
        private void ObserveFloor(TransferState s)
        {
            floorThrottle = Math.Max(floorThrottle, s.GlobalThrottleUtc);
            if (s.CleanupGeneration > knownTasksEpoch)
            {
                knownTasksEpoch = s.CleanupGeneration;
                Array.Clear(knownSlotGenerations, 0, 4);
                Array.Clear(knownSourceUsed, 0, 4);
                Array.Clear(knownEncodedUsed, 0, 4);
                Array.Clear(knownBatches, 0, 4);
                knownPendingGeneration = 0;
                knownPendingHash = null;
                knownPendingAttempts = 0;
            }
            for (int i = 0; i < 4; i++)
            {
                var row = s.Slot(i);
                if (row == null || s.CleanupGeneration != knownTasksEpoch)
                    continue;
                if (row.Generation > knownSlotGenerations[i])
                {
                    knownSlotGenerations[i] = row.Generation;
                    knownSourceUsed[i] = row.SourceUsed;
                    knownEncodedUsed[i] = row.EncodedUsed;
                    knownBatches[i] = row.GeneratedBatches;
                }
                else if (row.Generation == knownSlotGenerations[i])
                {
                    knownSourceUsed[i] = Math.Max(knownSourceUsed[i], row.SourceUsed);
                    knownEncodedUsed[i] = Math.Max(knownEncodedUsed[i], row.EncodedUsed);
                    knownBatches[i] = Math.Max(knownBatches[i], row.GeneratedBatches);
                }
            }
            if (s.Pending != null && s.CleanupGeneration == knownTasksEpoch)
            {
                var hash = s.Pending.CopyHash();
                if (knownPendingGeneration != s.Pending.SlotGeneration || !Equal(hash, knownPendingHash))
                {
                    knownPendingGeneration = s.Pending.SlotGeneration;
                    knownPendingHash = hash;
                    knownPendingAttempts = s.Pending.Attempts;
                }
                else
                    knownPendingAttempts = Math.Max(knownPendingAttempts, s.Pending.Attempts);
            }
            floorRevoked = Math.Max(floorRevoked, s.RevokedThrough);
            if (s.NetworkDay > floorDay)
            {
                floorDay = s.NetworkDay;
                floorU = s.OriginalU;
                floorV = s.OriginalV;
                normalFloor = s.NormalUsed;
                errorFloor = s.ErrorUsed;
            }
            else if (s.NetworkDay == floorDay)
            {
                floorU = s.OriginalU;
                floorV = s.OriginalV;
                normalFloor = Math.Max(normalFloor, s.NormalUsed);
                errorFloor = Math.Max(errorFloor, s.ErrorUsed);
            }
        }
        internal void CloseForClear()
        {
            CheckOwner();
            RevokeClearAuthorization();
            normalizationEpoch = -1;
            clearGate = true;
            clearEpochResetDurable = false;
            InvalidateGrant();
            attemptReady = false;
            if (Status == TransferStoreStatus.Saving)
                BeginLoad();
        }
        internal void CompletePending()
        {
            CheckOwner();
            RevokeClearAuthorization();
            stopped = true;
            InvalidateGrant();
            attemptReady = false;
        }
        public void Dispose()
        {
            if (Status == TransferStoreStatus.Disposed)
                return;
            CheckOwner();
            RevokeClearAuthorization();
            stopped = true;
            InvalidateGrant();
            Array.Clear(page ?? Array.Empty<byte>(), 0, page?.Length ?? 0);
            page = null;
            candidate = Durable = null;
            first = second = selected = null;
            Status = TransferStoreStatus.Disposed;
        }
    }
}
