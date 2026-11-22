using System;
using System.IO;
using System.Security.Cryptography;

namespace Logos.Storage
{
    /// <summary>每次 Step 最多一个文件、一个已认证块。调用者在每个单元之前检查截止时间。</summary>
    internal sealed class JournalRecovery : IDisposable
    {
        private readonly Stream stream;
        private readonly KeyMaterial key;
        private readonly BlockCodec codec;
        private readonly byte[] scratch;
        private readonly JournalDayState checkpoint;
        private readonly Action verifyOwner;
        private long position = 512;
        internal VerifiedFileHeader Header
        {
            get;
        }
        internal long NormalBytes
        {
            get;
            private set;
        }
        = 512;
        internal long ErrorBytes
        {
            get;
            private set;
        }
        internal long ConfirmedEnd => position;
        internal bool Complete
        {
            get;
            private set;
        }
        internal byte[] LastEpoch
        {
            get;
        }
        = new byte[16];
        internal bool AuthenticatedBlockLastStep
        {
            get;
            private set;
        }

        internal JournalRecovery(
            Stream stream,
            KeyMaterial key,
            VerifiedFileHeader header,
            BlockCodec codec,
            byte[] scratch,
            JournalDayState checkpoint,
            Action verifyOwner
        )
        {
            this.stream = stream;
            this.key = key;
            Header = header;
            this.codec = codec;
            this.scratch = scratch;
            this.checkpoint = checkpoint;
            this.verifyOwner = verifyOwner;
        }

        internal void Step()
        {
            AuthenticatedBlockLastStep = false;
            if (Complete)
                return;
            verifyOwner();
            long physicalEnd = stream.Length;
            var intent = checkpoint?.Intent;
            if (
                intent != null
                && position == intent.Start
                && physicalEnd < intent.ExpectedEnd
                && physicalEnd >= intent.Start
                && intent.Start == intent.PreviousCommittedEnd
                && checkpoint.CommittedEnd <= intent.Start
                && intent.DayId == Header.DayId
                && intent.CleanupGeneration == checkpoint.CleanupGeneration
                && IsIncompleteIntentTail(intent, physicalEnd)
            )
            {
                try
                {
                    verifyOwner();
                    stream.SetLength(position);
                    stream.Flush();
                    physicalEnd = position;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    throw new JournalBlockedException(JournalBlockReason.TruncateFailed);
                }
            }

            // 到达物理文件尾后，再与已知提交下界对账。缺失已确认的字节必须阻塞，不能用较小的扫描结果覆盖检查点。
            if (position == physicalEnd)
            {
                if (
                    checkpoint != null
                    && (
                        checkpoint.CommittedEnd > position
                        || checkpoint.NormalBytes > NormalBytes
                        || checkpoint.ErrorBytes > ErrorBytes
                    )
                )
                    throw new JournalBlockedException(JournalBlockReason.KnownCommitMissing);
                if (
                    NormalBytes > Header.MaxDailyFileBytes - Header.ErrorFatalReservedBytesPerDay
                    || ErrorBytes > Header.ErrorFatalReservedBytesPerDay
                )
                    throw new JournalBlockedException(JournalBlockReason.PolicyMismatch);
                Complete = true;
                return;
            }

            if (physicalEnd - position < 256)
                throw new JournalBlockedException(JournalBlockReason.IncompleteTail);
            stream.Position = position;
            JournalIo.ReadExactly(stream, scratch, 0, 256);
            uint declared = Wire.U32(scratch, 12);
            if (declared < BlockCodec.MinPhysicalWireBlockBytes || declared > scratch.Length)
                throw new JournalBlockedException(JournalBlockReason.BlockInvalid);
            int length = (int)declared;
            if (physicalEnd - position < length)
                throw new JournalBlockedException(JournalBlockReason.IncompleteTail);
            JournalIo.ReadExactly(stream, scratch, 256, length - 256);
            DecodedBlock decoded;
            try
            {
                decoded = codec.Decode(Header, key, scratch, 0, length);
            }
            catch (StorageFormatException)
            {
                throw new JournalBlockedException(JournalBlockReason.BlockInvalid);
            }

            try
            {
                if (intent != null && position == intent.Start)
                {
                    byte[] hash;
                    using (var sha = SHA256.Create())
                        hash = sha.ComputeHash(scratch, 0, length);
                    bool matches =
                        intent.ExpectedEnd == position + length
                        && Wire.Equal(hash, intent.WireHash)
                        && Wire.Equal(decoded.Epoch, intent.Epoch)
                        && decoded.Sequence == intent.BlockSequence
                        && decoded.IsErrorClass == intent.IsErrorClass;
                    Wire.Clear(hash);
                    if (!matches)
                        throw new JournalBlockedException(JournalBlockReason.BlockInvalid);
                }

                Buffer.BlockCopy(decoded.Epoch, 0, LastEpoch, 0, 16);
                if (decoded.IsErrorClass)
                    ErrorBytes += length;
                else
                    NormalBytes += length;
                position += length;
                AuthenticatedBlockLastStep = true;
            }
            finally
            {
                Array.Clear(decoded.Records, 0, decoded.Records.Length);
                Wire.Clear(decoded.Epoch);
                Array.Clear(scratch, 0, length);
            }
        }

        private bool IsIncompleteIntentTail(AppendIntent intent, long physicalEnd)
        {
            long available = physicalEnd - position;
            if (available < 256)
                return true;
            stream.Position = position;
            JournalIo.ReadExactly(stream, scratch, 0, 256);
            uint declared = Wire.U32(scratch, 12);
            if (declared < 320 || declared > 65856)
                throw new JournalBlockedException(JournalBlockReason.BlockInvalid);
            // 即使已认证意图的范围更大，也不能授权删除完整的已声明块。
            if (declared <= available)
                return false;
            if (declared != intent.ExpectedEnd - intent.Start)
                throw new JournalBlockedException(JournalBlockReason.BlockInvalid);
            return true;
        }

        internal bool ResourcesClosed => JournalIo.IsClosed(stream);

        public void Dispose()
        {
            try
            {
                stream.Dispose();
            }
            finally
            {
                key.Dispose();
                Wire.Clear(LastEpoch);
            }
        }
    }
}
