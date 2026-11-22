using System;
using System.IO;

namespace Logos.Storage
{
    /// <summary>一个固定文件，每个槽位两份经过认证的 512 字节副本；不使用临时元数据文件。</summary>
    internal sealed class JournalStateStore : IDisposable
    {
        internal const int DayRegionOffset = 1047552;
        internal const int LegacyFileBytes = 2096128;
        // 固定增加 512 字节阻止旧 SDK 打开；加 owner.lock 正好占满 2 MiB 元数据预算。
        internal const int FileBytes = LegacyFileBytes + 512;
        private readonly Stream stream;
        private readonly IStorageKeyProvider keys;
        private readonly Action verifyOwner;
        private readonly byte[] buffer = new byte[512];
        private readonly byte[] verification = new byte[512];
        internal const int FixedBufferCapacityBytes = 2 * 512;
        internal int FixedBufferPayloadBytes => buffer.Length + verification.Length;
        internal bool LastDayRequiresRepair
        {
            get;
            private set;
        }
        internal bool RequiresGlobalRepair
        {
            get;
            private set;
        }

        internal JournalStateStore(
            Stream stream,
            IStorageKeyProvider keys,
            Action verifyOwner,
            bool create
        )
        {
            this.stream = stream;
            this.keys = keys;
            this.verifyOwner = verifyOwner;
            try
            {
                verifyOwner();
                long existingLength = stream.Length;
                if (!create && existingLength != LegacyFileBytes && existingLength != FileBytes)
                    throw new JournalBlockedException(JournalBlockReason.GlobalStateInvalid);
                if (create || existingLength == LegacyFileBytes)
                {
                    // 先升级并确认，再让 Journal 读取状态或开放上传/清理。
                    stream.SetLength(FileBytes);
                    stream.Flush();
                    if (stream.Length != FileBytes)
                        throw new IOException("State length upgrade could not be verified.");
                }
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        internal const int TransferPageBytes = 131072;
        private static int TransferOffset(int slot, byte[] page)
        {
            if (slot < 0 || slot > 1)
                throw new ArgumentOutOfRangeException(nameof(slot));
            if (page == null || page.Length != TransferPageBytes)
                throw new ArgumentException("One complete transfer page required.", nameof(page));
            return 32768 + slot * TransferPageBytes;
        }
        internal void ReadTransferPage(int slot, byte[] page)
        {
            int offset = TransferOffset(slot, page);
            verifyOwner();
            stream.Position = offset;
            JournalIo.ReadExactly(stream, page, 0, page.Length);
        }
        internal void WriteTransferPage(int slot, byte[] page, int count)
        {
            int offset = TransferOffset(slot, page);
            if (count < 304 || count > TransferPageBytes)
                throw new ArgumentOutOfRangeException(nameof(count));
            verifyOwner();
            stream.Position = offset;
            // 尾部不属于 v3 格式；每次尝试仍只有一次连续写入。
            stream.Write(page, 0, count);
        }
        internal void FlushTransferPage()
        {
            verifyOwner();
            stream.Flush();
        }

        internal JournalGlobalState ReadGlobal()
        {
            var a = ReadGlobalCopy(0);
            var b = ReadGlobalCopy(512);
            if (a == null && b == null)
                throw new JournalBlockedException(JournalBlockReason.GlobalStateInvalid);
            if (a != null && b != null && !Wire.Equal(a.DirectoryId, b.DirectoryId))
                throw new JournalBlockedException(JournalBlockReason.GlobalStateInvalid);
            RequiresGlobalRepair =
                (a != null && a.SchemaVersion != 2)
                || (b != null && b.SchemaVersion != 2)
                || a == null
                || b == null
                || a.HighestDay != b.HighestDay
                || a.TimeConfirmed != b.TimeConfirmed
                || a.CleanupGeneration != b.CleanupGeneration
                || a.FileLimit != b.FileLimit
                || a.ErrorLimit != b.ErrorLimit
                || a.PolicyVersion != b.PolicyVersion
                || a.ClearPhase != b.ClearPhase
                || !SamePayload(a, b);
            if (a == null)
                return b;
            if (b == null)
                return a;
            // 选副本时先保住较新清理代际，同代际优先保留未完成意图；序号独立取最大值，以免修复时倒退。
            ulong sequence = Math.Max(a.Sequence, b.Sequence);
            var latest =
                a.CleanupGeneration != b.CleanupGeneration
                    ? (a.CleanupGeneration > b.CleanupGeneration ? a : b)
                : (a.ClearPhase == 1 || b.ClearPhase == 1) ? (a.ClearPhase == 1 ? a : b)
                : (a.Sequence >= b.Sequence ? a : b);
            latest.Sequence = sequence;
            return latest;
        }

        private JournalGlobalState ReadGlobalCopy(int offset)
        {
            if (!ReadCopy(offset))
                return null;
            string id;
            try
            {
                id = JournalIo.ReadKeyId(buffer, 80, 81);
            }
            catch (StorageFormatException)
            {
                return null;
            }

            using (var key = JournalIo.Acquire(keys, id))
                return JournalStateCodec.DecodeGlobal(buffer, key);
        }

        internal JournalDayState ReadDay(int slot, out bool hadEvidence)
        {
            return ReadDayCore(slot, out hadEvidence, null, 0, 0, out _, out _);
        }

        internal JournalDayState ReadDayForRecovery(
            int slot,
            JournalGlobalState global,
            long confirmedDay,
            int retentionDays,
            out JournalDayState replaced,
            out bool prepared
        )
        {
            return ReadDayCore(
                slot,
                out _,
                global,
                confirmedDay,
                retentionDays,
                out replaced,
                out prepared
            );
        }

        private JournalDayState ReadDayCore(
            int slot,
            out bool hadEvidence,
            JournalGlobalState global,
            long confirmedDay,
            int retentionDays,
            out JournalDayState replaced,
            out bool preparedSlot
        )
        {
            replaced = null;
            preparedSlot = false;
            var a = ReadDayCopy(DayRegionOffset + slot * 1024L, out bool ah);
            var b = ReadDayCopy(DayRegionOffset + slot * 1024L + 512, out bool bh);
            hadEvidence = ah || bh;
            LastDayRequiresRepair =
                hadEvidence
                && (a == null || b == null || a.SchemaVersion != 2 || b.SchemaVersion != 2);
            if (global != null && a != null && b != null)
            {
                // 同一槽位中出现两个日期可能是换日中断。只有新行确为空准备、日期已确认且旧行满足过期复用条件时才能继续。
                var prepared = a.DayId >= b.DayId ? a : b;
                var previous = ReferenceEquals(prepared, a) ? b : a;
                if (
                    prepared.DayId > global.HighestDay
                    && prepared.DayId <= confirmedDay
                    && prepared.DayId % 1024 == slot
                    && IsEmptyPreparation(prepared, global)
                    && (
                        previous.DayId == prepared.DayId
                            ? IsEmptyPreparation(previous, global)
                                && previous.FileLimit == prepared.FileLimit
                                && previous.ErrorLimit == prepared.ErrorLimit
                            : previous.DayId % 1024 == slot
                                && previous.DayId <= global.HighestDay
                                && previous.Sequence < prepared.Sequence
                                && previous.CleanupGeneration <= global.CleanupGeneration
                                && Wire.Equal(previous.DirectoryId, global.DirectoryId)
                                && previous.Tombstone
                                && previous.DeletionKind == 1
                                && previous.DeletionPhase == 2
                                && previous.ExpiryConfirmedDay <= prepared.DayId
                                && RetentionPolicy.IsExpired(
                                    previous.DayId,
                                    prepared.DayId,
                                    retentionDays
                                )
                    )
                )
                {
                    prepared.Sequence = Math.Max(a.Sequence, b.Sequence);
                    replaced = previous.DayId == prepared.DayId ? null : previous;
                    preparedSlot = true;
                    LastDayRequiresRepair = true;
                    return prepared;
                }
            }

            if (a == null)
                return b;
            if (b == null)
                return a;
            if (
                a.DayId != b.DayId
                || a.FileLimit != b.FileLimit
                || a.ErrorLimit != b.ErrorLimit
                || a.PolicyVersion != b.PolicyVersion
                || !Wire.Equal(a.DirectoryId, b.DirectoryId)
            )
                throw new JournalBlockedException(JournalBlockReason.StateInvalid);
            var latest =
                a.CleanupGeneration != b.CleanupGeneration
                    ? (a.CleanupGeneration > b.CleanupGeneration ? a : b)
                    : (a.Sequence >= b.Sequence ? a : b);
            latest.Sequence = Math.Max(a.Sequence, b.Sequence);
            // 两份已认证副本可能在追加后的检查点写入时分叉。合并各项下界，避免较新的副本把已确认字节或删除标记抹掉。
            latest.CommittedEnd = Math.Max(a.CommittedEnd, b.CommittedEnd);
            latest.NormalBytes = Math.Max(a.NormalBytes, b.NormalBytes);
            latest.ErrorBytes = Math.Max(a.ErrorBytes, b.ErrorBytes);
            latest.Exists = a.Exists || b.Exists;
            latest.Tombstone = a.Tombstone || b.Tombstone;
            if (
                a.DeletionPhase > latest.DeletionPhase
                && a.CleanupGeneration == latest.CleanupGeneration
            )
            {
                latest.DeletionKind = a.DeletionKind;
                latest.DeletionPhase = a.DeletionPhase;
                latest.ExpiryConfirmedDay = a.ExpiryConfirmedDay;
                latest.DeletionGeneration = a.DeletionGeneration;
            }

            if (
                b.DeletionPhase > latest.DeletionPhase
                && b.CleanupGeneration == latest.CleanupGeneration
            )
            {
                latest.DeletionKind = b.DeletionKind;
                latest.DeletionPhase = b.DeletionPhase;
                latest.ExpiryConfirmedDay = b.ExpiryConfirmedDay;
                latest.DeletionGeneration = b.DeletionGeneration;
            }

            return latest;
        }

        private static bool IsEmptyPreparation(JournalDayState row, JournalGlobalState global)
        {
            if (
                row.SchemaVersion != 2
                || row.Exists
                || row.Tombstone
                || row.Clean
                || row.Intent != null
                || row.CommittedEnd != 0
                || row.NormalBytes != 0
                || row.ErrorBytes != 0
                || row.DeletionKind != 0
                || row.DeletionPhase != 0
                || row.DeletionGeneration != 0
                || row.ExpiryConfirmedDay != 0
                || row.CleanupGeneration > global.CleanupGeneration
                || !string.IsNullOrEmpty(row.DataKeyId)
                || !Wire.Equal(row.DirectoryId, global.DirectoryId)
            )
                return false;
            foreach (byte value in row.Epoch)
                if (value != 0)
                    return false;
            foreach (byte value in row.HeaderHash)
                if (value != 0)
                    return false;
            return true;
        }

        private JournalDayState ReadDayCopy(long offset, out bool evidence)
        {
            evidence = ReadCopy(offset);
            if (!evidence)
                return null;
            string id;
            try
            {
                id = JournalIo.ReadKeyId(buffer, 169, 170);
            }
            catch (StorageFormatException)
            {
                return null;
            }

            using (var key = JournalIo.Acquire(keys, id))
                return JournalStateCodec.DecodeDay(buffer, key);
        }

        private bool ReadCopy(long offset)
        {
            verifyOwner();
            stream.Position = offset;
            JournalIo.ReadExactly(stream, buffer, 0, 512);
            int bits = 0;
            for (int i = 0; i < 512; i++)
                bits |= buffer[i];
            return bits != 0;
        }

        internal void WriteGlobalBoth(JournalGlobalState state, KeyMaterial key)
        {
            for (int i = 0; i < 2; i++)
            {
                state.SchemaVersion = 2;
                state.Sequence = Next(state.Sequence);
                state.ControlKeyId = key.KeyId;
                JournalStateCodec.EncodeGlobal(state, key, buffer);
                WriteVerified((long)(state.Sequence & 1) * 512);
            }

            RequiresGlobalRepair = false;
        }

        internal void WriteDay(JournalDayState state, KeyMaterial key, bool both = false)
        {
            for (int i = 0; i < (both ? 2 : 1); i++)
            {
                state.SchemaVersion = 2;
                state.Sequence = Next(state.Sequence);
                state.ControlKeyId = key.KeyId;
                JournalStateCodec.EncodeDay(state, key, buffer);
                long offset =
                    DayRegionOffset
                    + (state.DayId % 1024) * 1024
                    + (long)(state.Sequence & 1) * 512;
                WriteVerified(offset);
            }
        }

        private static bool SamePayload(JournalGlobalState a, JournalGlobalState b) =>
            a.PayloadLength == b.PayloadLength
            && a.PayloadGeneration == b.PayloadGeneration
            && a.ClearScope == b.ClearScope
            && a.TargetCount == b.TargetCount
            && Wire.Equal(a.PayloadHash, b.PayloadHash);

        internal bool VerifyClearIntentBoth(JournalGlobalState expected)
        {
            var a = ReadGlobalCopy(0);
            var b = ReadGlobalCopy(512);
            return a != null
                && b != null
                && a.ClearPhase == 1
                && b.ClearPhase == 1
                && a.CleanupGeneration == expected.CleanupGeneration
                && b.CleanupGeneration == expected.CleanupGeneration
                && Wire.Equal(a.DirectoryId, expected.DirectoryId)
                && Wire.Equal(b.DirectoryId, expected.DirectoryId)
                && SamePayload(a, expected)
                && SamePayload(b, expected);
        }

        internal bool VerifyCompletedBoth(long generation)
        {
            var a = ReadGlobalCopy(0);
            var b = ReadGlobalCopy(512);
            return a != null
                && b != null
                && a.CleanupGeneration == generation
                && b.CleanupGeneration == generation
                && a.ClearPhase == 2
                && b.ClearPhase == 2
                && a.PayloadLength == 0
                && b.PayloadLength == 0;
        }

        internal bool VerifyDeletionBoth(JournalDayState row, byte phase)
        {
            long start = DayRegionOffset + (row.DayId % 1024) * 1024;
            var a = ReadDayCopy(start, out _);
            var b = ReadDayCopy(start + 512, out _);
            return a != null
                && b != null
                && a.DayId == row.DayId
                && b.DayId == row.DayId
                && a.CleanupGeneration == row.CleanupGeneration
                && b.CleanupGeneration == row.CleanupGeneration
                && a.DeletionKind == row.DeletionKind
                && b.DeletionKind == row.DeletionKind
                && a.DeletionPhase >= phase
                && b.DeletionPhase >= phase
                && a.Tombstone
                && b.Tombstone
                && a.NormalBytes >= row.NormalBytes
                && b.NormalBytes >= row.NormalBytes
                && a.ErrorBytes >= row.ErrorBytes
                && b.ErrorBytes >= row.ErrorBytes;
        }

        internal void WritePayload(byte[] payload)
        {
            verifyOwner();
            if (payload.Length > ClearPayload.Capacity)
                throw new JournalBlockedException(JournalBlockReason.MetadataLimit);
            var check = new byte[payload.Length];
            try
            {
                stream.Position = ClearPayload.Offset;
                stream.Write(payload, 0, payload.Length);
                stream.Flush();
                stream.Position = ClearPayload.Offset;
                JournalIo.ReadExactly(stream, check, 0, check.Length);
                if (!Wire.Equal(payload, check))
                    throw new IOException("Payload verification failed.");
            }
            finally
            {
                Wire.Clear(check);
            }
        }

        internal long[] ReadPayload(JournalGlobalState state)
        {
            verifyOwner();
            if (state.PayloadLength < 64 || state.PayloadLength > 8256)
                throw new JournalBlockedException(JournalBlockReason.StateInvalid);
            var bytes = new byte[state.PayloadLength];
            try
            {
                stream.Position = ClearPayload.Offset;
                JournalIo.ReadExactly(stream, bytes, 0, bytes.Length);
                return ClearPayload.Decode(bytes, state);
            }
            finally
            {
                Wire.Clear(bytes);
            }
        }

        internal void ReadReserved(int offset, byte[] destination)
        {
            verifyOwner();
            if (offset < 1024 || offset > DayRegionOffset - destination.Length)
                throw new ArgumentOutOfRangeException(nameof(offset));
            stream.Position = offset;
            JournalIo.ReadExactly(stream, destination, 0, destination.Length);
        }

        internal void WriteReservedVerified(int offset, byte[] bytes, byte[] check)
        {
            verifyOwner();
            if (
                offset < 1024
                || offset > DayRegionOffset - bytes.Length
                || check.Length != bytes.Length
            )
                throw new ArgumentOutOfRangeException(nameof(offset));
            stream.Position = offset;
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
            stream.Position = offset;
            JournalIo.ReadExactly(stream, check, 0, check.Length);
            if (!Wire.Equal(bytes, check))
                throw new IOException("Reserved metadata verification failed.");
        }

        internal bool VerifyGlobalControlKeyBoth(string keyId)
        {
            var a = ReadGlobalCopy(0);
            var b = ReadGlobalCopy(512);
            return a != null && b != null && a.ControlKeyId == keyId && b.ControlKeyId == keyId;
        }

        internal bool VerifyDayControlKeyBoth(int slot, string keyId)
        {
            long start = DayRegionOffset + slot * 1024L;
            var a = ReadDayCopy(start, out bool ah);
            var b = ReadDayCopy(start + 512, out bool bh);
            if (!ah && !bh)
                return true;
            return a != null && b != null && a.ControlKeyId == keyId && b.ControlKeyId == keyId;
        }

        private static ulong Next(ulong value)
        {
            if (value == ulong.MaxValue)
                throw new JournalBlockedException(JournalBlockReason.MetadataLimit);
            return value + 1;
        }

        // 副本写入后读回核对；发布步骤需要区分确实可读的状态和仅执行过写入调用的状态。
        private void WriteVerified(long offset)
        {
            verifyOwner();
            stream.Position = offset;
            stream.Write(buffer, 0, 512);
            stream.Flush();
            stream.Position = offset;
            JournalIo.ReadExactly(stream, verification, 0, 512);
            if (!Wire.Equal(buffer, verification))
                throw new IOException("State verification failed.");
        }

        internal Stream OwnedStream => stream;
        internal bool ResourcesClosed => JournalIo.IsClosed(stream);

        public void Dispose()
        {
            try
            {
                stream.Dispose();
            }
            finally
            {
                Wire.Clear(buffer);
                Wire.Clear(verification);
            }
        }
    }
}
