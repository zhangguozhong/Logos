using System;

namespace Logos.Storage
{
    internal enum StageResult
    {
        Staged, Rejected, Backpressure
    }

    /// <summary>一个类别由工作线程独占的明文或已封装线格式槽位。借用编解码器，不保留输入记录、头或密钥。</summary>
    internal sealed class BoundedBlockStaging : IDisposable
    {
        private enum SlotState
        {
            Empty, Staging, Sealed, SealFailed, OutcomePending
        }
        private readonly byte[] slot = new byte[StorageRuntimeLimits.MaxWireBlockBytes];
        private readonly StorageOutcomeLedger ledger = new StorageOutcomeLedger();
        private readonly RecordCodec records;
        private readonly BlockCodec blocks;
        private readonly bool isErrorClass;
        private BlockMetadata metadata;
        private SealedBlockLease sealedLease;
        private long slotGeneration;
        private SlotState state;
        private int plainLength, sealedLength;
        private long firstRecordTicks, pendingDeadlineTicks;
        private bool sealRequested, disposed;
        private BlockSealReason sealReason;
        private BlockSealStatistics sealStatistics;
        private int sealedPlainLength;
        internal BlockSealStatistics SealStatistics => sealStatistics;
        private bool writeWaitPaused, writeWaitWasValid;
        private long writeWaitPauseStarted;

        internal BoundedBlockStaging(bool isErrorClass, RecordCodec records, BlockCodec blocks)
        {
            this.records = records ?? throw new ArgumentNullException(nameof(records));
            this.blocks = blocks ?? throw new ArgumentNullException(nameof(blocks));
            this.isErrorClass = isErrorClass;
        }

        internal int Count => ledger.Count;
        internal int DirectCount => ledger.DirectCount;
        internal bool IsSynthetic
        {
            get;
            private set;
        }
        internal long SequenceAt(int index) => ledger.SequenceAt(index);
        internal StorageOwnershipToken TokenAt(int index) => ledger.TokenAt(index);
        private bool resolving;
        internal bool IsSealed => state == SlotState.Sealed;
        internal bool HasOutcome => state == SlotState.OutcomePending;
        internal bool IsEmpty => state == SlotState.Empty;
        internal bool CanStage => !disposed && slotGeneration < long.MaxValue && (state == SlotState.Empty || state == SlotState.Staging) && ledger.CanAdd;
        internal int WorkspaceBytes => slot.Length + ledger.WorkspaceBytes + 608;
        // 保守预算包括此槽位和元数据的数值及引用字段；CLR 对象头另计。
        internal int ControlPayloadBytes => ledger.ControlPayloadBytes + 608;
        internal int PlainLength => plainLength;
        internal int SealedLength => sealedLength;
        internal long FirstRecordTicks => firstRecordTicks;
        internal long PendingDeadlineTicks => pendingDeadlineTicks;
        internal bool SealRequested => sealRequested;
        // 借用持续到确认或 Dispose。协调器不得修改它，也不得越过该边界继续保留。
        internal ArraySegment<byte> SealedBytes => new ArraySegment<byte>(slot, 0, sealedLength);
        internal SealedBlockLease SealedLease => sealedLease;

        internal bool TryBorrowLease(SealedBlockLease lease, long generation, out ArraySegment<byte> bytes)
        {
            bytes = default;
            if (disposed || state != SlotState.Sealed || generation != slotGeneration
                || !ReferenceEquals(lease, sealedLease))
                return false;
            bytes = new ArraySegment<byte>(slot, 0, sealedLength);
            return true;
        }

        internal StageResult TryStage(LogRecord record, VerifiedFileHeader header, KeyMaterial key, long nowTicks, out RecordEncodeFailure failure)
        {
            Check();
            failure = RecordEncodeFailure.None;
            if (!CanStage)
            {
                RequestSeal(BlockSealReason.Backpressure);
                return StageResult.Backpressure;
            }
            if (record == null || header == null || key == null)
            {
                failure = RecordEncodeFailure.InvalidRecord;
                return StageResult.Rejected;
            }
            if (isErrorClass != (record.Level >= LogLevel.Error)
                || (record.QuotaDayTag.HasValue && record.QuotaDayTag.Value.DayId != header.DayId)
                || (metadata != null && !Wire.Equal(metadata.FileHeaderHash, header.Hash)))
            {
                RequestSeal(BlockSealReason.IdentityBoundary);
                return StageResult.Backpressure;
            }

            int contextBytes = metadata?.ContextBytes ?? record.Context.BlockSnapshotBytes;
            int offset = state == SlotState.Empty ? 4 : plainLength;
            bool transferred = false;
            try
            {
                if (!records.TryEncode(record, slot, offset, StorageRuntimeLimits.MaxPlainBlockBytes - offset, out int written, out failure))
                {
                    if (failure == RecordEncodeFailure.DestinationTooSmall)
                    {
                        if (IsEmpty)
                        {
                            failure = RecordEncodeFailure.RecordTooLarge;
                            return StageResult.Rejected;
                        }
                        failure = RecordEncodeFailure.None;
                        RequestSeal(BlockSealReason.ByteCapacity);
                        return StageResult.Backpressure;
                    }
                    return StageResult.Rejected;
                }
                if (written > StorageRuntimeLimits.MaxPlainBlockBytes - offset - contextBytes)
                {
                    if (IsEmpty)
                    {
                        failure = RecordEncodeFailure.RecordTooLarge;
                        return StageResult.Rejected;
                    }
                    RequestSeal(BlockSealReason.ByteCapacity);
                    return StageResult.Backpressure;
                }
                if (metadata == null)
                {
                    metadata = blocks.CreateBlockMetadata(header, key, record);
                }
                else if (!blocks.TryIncludeStagedRecord(metadata, record))
                {
                    RequestSeal(BlockSealReason.IdentityBoundary);
                    return StageResult.Backpressure;
                }
                // 编码前已检查容量和状态；工作线程独占保证此次添加不会失败。
                if (!ledger.TryAdd(record.Sequence, record.Level, written))
                    throw new InvalidOperationException("Ownership ledger changed during staging.");
                if (state == SlotState.Empty)
                    firstRecordTicks = nowTicks;
                plainLength = offset + written;
                Wire.Put16(slot, 0, (ushort)(record.DiagnosticMetadata != null || (offset > 4 && Wire.U16(slot, 0) == 2) ? 2 : 1));
                Wire.Put16(slot, 2, (ushort)ledger.Count);
                state = SlotState.Staging;
                if (ledger.Count == StorageRuntimeLimits.MaxRecordsPerBlock)
                    RequestSeal(BlockSealReason.RecordCapacity);
                else if (plainLength + contextBytes == StorageRuntimeLimits.MaxPlainBlockBytes)
                    RequestSeal(BlockSealReason.ByteCapacity);
                transferred = true;
                return StageResult.Staged;
            }
            finally
            {
                // TryEncode 可能已写入敏感的部分记录；清空整个空闲尾部的工作量有界。
                if (!transferred)
                    Array.Clear(slot, offset, slot.Length - offset);
            }
        }

        internal void RequestSeal(BlockSealReason reason = BlockSealReason.Explicit)
        {
            Check();
            if (state == SlotState.Staging && !sealRequested)
            {
                sealRequested = true;
                sealReason = reason == BlockSealReason.None ? BlockSealReason.Explicit : reason;
            }
        }

        internal bool IsSealDue(long nowTicks)
        {
            Check();
            bool due = state == SlotState.Staging && (sealRequested
                || (nowTicks >= firstRecordTicks && unchecked((ulong)nowTicks - (ulong)firstRecordTicks) >= (ulong)StorageRuntimeLimits.SealWaitTicks));
            // 时间探测只标注原因，不把探测变成永久请求，保留原有时钟边界语义。
            if (due && !sealRequested)
                sealReason = BlockSealReason.Time;
            return due;
        }

        internal bool TrySeal(VerifiedFileHeader header, KeyMaterial key, long pendingDeadlineTicks, out BlockSealFailure failure)
            => TrySeal(header, key, pendingDeadlineTicks, 0, out failure);

        internal bool TrySeal(VerifiedFileHeader header, KeyMaterial key, long pendingDeadlineTicks,
            long writeGeneration, out BlockSealFailure failure)
        {
            Check();
            failure = BlockSealFailure.InvalidInput;
            if (state != SlotState.Staging || slotGeneration == long.MaxValue)
                return false;
            bool success = false;
            try
            {
                if (!blocks.TrySeal(header, key, metadata, slot, 0, plainLength, slot, 0, slot.Length, out int written, out failure))
                    return false;
                sealedLength = written;
                slotGeneration++;
                sealedLease = new SealedBlockLease(this, slotGeneration, header, key, writeGeneration,
                    slot, written, isErrorClass);
                this.pendingDeadlineTicks = pendingDeadlineTicks;
                // 压缩线格式可能远短于明文。清除封装输出末尾之外的所有旧明文字节。
                Array.Clear(slot, written, slot.Length - written);
                state = SlotState.Sealed;
                sealedPlainLength = plainLength + metadata.ContextBytes;
                sealStatistics.RecordSeal(sealReason, ledger.Count, sealedPlainLength, written);
                success = true;
                return true;
            }
            finally
            {
                plainLength = 0;
                sealRequested = false;
                if (!success)
                {
                    sealStatistics.RecordFailure();
                    sealedLease?.Invalidate();
                    sealedLease = null;
                    Array.Clear(slot, 0, slot.Length);
                    sealedLength = 0;
                    state = SlotState.SealFailed;
                }
            }
        }

        internal bool PublishOutcome(ulong sequence, LogStorageOutcomeKind kind, AdmissionReason reason)
        {
            Check();
            if (resolving || ledger.DirectCount == 0 || (kind == LogStorageOutcomeKind.Committed && state != SlotState.Sealed))
                return false;
            // 旧内部调用同样先验证 ledger，再执行一次性封块统计。
            if (!ledger.TryPublish(sequence, kind, reason))
                return false;
            // 发布后账本和槽位要等本批结果得到确认才可复用，以免下一次封块覆盖尚待交付的结果。
            if (!BeginResolution(kind))
                throw new InvalidOperationException("Resolution state changed.");
            state = SlotState.OutcomePending;
            return true;
        }

        // 先冻结并撤销借用，再通知有界所有者；通知后才允许清理合成槽。
        internal bool BeginResolution(LogStorageOutcomeKind kind)
        {
            if (resolving || IsEmpty || HasOutcome || (kind == LogStorageOutcomeKind.Committed && !IsSealed))
                return false;
            resolving = true;
            if (kind == LogStorageOutcomeKind.Committed)
                sealStatistics.RecordCommit(ledger.Count, sealedPlainLength, sealedLength);
            sealedLease?.Invalidate();
            return true;
        }
        internal bool PublishDirectOutcome(ulong sequence, LogStorageOutcomeKind kind, AdmissionReason reason)
        {
            if (!resolving || !ledger.TryPublish(sequence, kind, reason))
                return false;
            state = SlotState.OutcomePending;
            return true;
        }
        internal void FinishSyntheticResolution()
        {
            if (!resolving || ledger.DirectCount != 0)
                throw new InvalidOperationException("Direct ownership remains.");
            ledger.Clear();
            ClearSlot();
        }
        internal StageResult TryStagePrepared(PreparedRecordSlot prepared, VerifiedFileHeader header, KeyMaterial key, long now)
        {
            Check();
            var record = prepared.Descriptor;
            bool synthetic = prepared.Token.IsSynthetic;
            if (!CanStage || resolving || (state != SlotState.Empty && IsSynthetic != synthetic))
            {
                RequestSeal(BlockSealReason.Backpressure);
                return StageResult.Backpressure;
            }
            if (record.QuotaDayTag?.DayId != header.DayId || isErrorClass != (record.Level >= LogLevel.Error)
                || (metadata != null && !Wire.Equal(metadata.FileHeaderHash, header.Hash)))
            {
                RequestSeal(BlockSealReason.IdentityBoundary);
                return StageResult.Backpressure;
            }
            int contextBytes = metadata?.ContextBytes ?? record.Context.BlockSnapshotBytes;
            int offset = state == SlotState.Empty ? 4 : plainLength;
            if (prepared.Length > StorageRuntimeLimits.MaxPlainBlockBytes - contextBytes - offset)
            {
                RequestSeal(BlockSealReason.ByteCapacity);
                return StageResult.Backpressure;
            }
            if (metadata == null)
                metadata = blocks.CreateBlockMetadata(header, key, record);
            else if (!blocks.TryIncludeStagedRecord(metadata, record))
            {
                RequestSeal(BlockSealReason.IdentityBoundary);
                return StageResult.Backpressure;
            }
            Buffer.BlockCopy(prepared.Bytes, 0, slot, offset, prepared.Length);
            if (!ledger.TryAdd(record.Sequence, record.Level, prepared.Length, prepared.Token))
                throw new InvalidOperationException("Prepared ledger changed.");
            if (state == SlotState.Empty)
                firstRecordTicks = now;
            plainLength = offset + prepared.Length;
            Wire.Put16(slot, 0, (ushort)(record.DiagnosticMetadata != null || (offset > 4 && Wire.U16(slot, 0) == 2) ? 2 : 1));
            Wire.Put16(slot, 2, (ushort)ledger.Count);
            state = SlotState.Staging;
            IsSynthetic = synthetic;
            if (synthetic || ledger.Count == StorageRuntimeLimits.MaxRecordsPerBlock)
                RequestSeal(BlockSealReason.RecordCapacity);
            return StageResult.Staged;
        }

        internal void PauseWriteWait(long now)
        {
            Check();
            if (!IsSealed || writeWaitPaused)
                return;
            writeWaitPaused = true;
            writeWaitWasValid = now < pendingDeadlineTicks;
            writeWaitPauseStarted = now;
        }
        internal void ResumeWriteWait(long now)
        {
            Check();
            if (!writeWaitPaused)
                return;
            writeWaitPaused = false;
            // 过期块永不恢复有效；时钟回退不会增加写令牌等待额度。
            if (!IsSealed || !writeWaitWasValid || now <= writeWaitPauseStarted)
                return;
            ulong elapsed = unchecked((ulong)now - (ulong)writeWaitPauseStarted);
            long delta = elapsed > long.MaxValue ? long.MaxValue : (long)elapsed;
            pendingDeadlineTicks = pendingDeadlineTicks > long.MaxValue - delta ? long.MaxValue : pendingDeadlineTicks + delta;
        }

        internal bool TryPeekOutcome(out LogStorageOutcome outcome)
        {
            Check();
            return ledger.TryPeek(out outcome);
        }

        internal bool AckOutcome(ulong sequence)
        {
            Check();
            if (!ledger.Ack(sequence))
                return false;
            ClearSlot();
            return true;
        }

        private void ClearSlot()
        {
            sealedLease?.Invalidate();
            sealedLease = null;
            // 引用身份与代际同时检查；清理后旧凭证不能借用复用的槽位。
            if (slotGeneration < long.MaxValue)
                slotGeneration++;
            // 代际耗尽后永久停止接收，但清零和 Dispose 仍必须完成。
            Array.Clear(slot, 0, slot.Length);
            if (metadata != null)
            {
                Wire.Clear(metadata.Digests);
                Wire.Clear(metadata.Fingerprint);
                Wire.Clear(metadata.FileHeaderHash);
                metadata.RecordCount = 0;
                metadata.MinimumSequence = metadata.MaximumSequence = metadata.MinimumUtcTicks = metadata.MaximumUtcTicks = 0;
                metadata = null;
            }
            plainLength = sealedLength = sealedPlainLength = 0;
            sealReason = BlockSealReason.None;
            firstRecordTicks = pendingDeadlineTicks = 0;
            sealRequested = writeWaitPaused = writeWaitWasValid = false;
            writeWaitPauseStarted = 0;
            state = SlotState.Empty;
            resolving = IsSynthetic = false;
        }

        private void Check()
        {
            if (disposed)
                throw new ObjectDisposedException(nameof(BoundedBlockStaging));
        }

        public void Dispose()
        {
            if (disposed)
                return;
            ClearSlot();
            ledger.Dispose();
            disposed = true;
        }
    }
}
