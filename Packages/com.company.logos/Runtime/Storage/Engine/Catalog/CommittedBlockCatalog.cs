using System;
using System.IO;

namespace Logos.Storage
{
    internal interface IJournalSourceReadAuthorization
    {
        bool TryConsume(long upperBoundBytes);
    }
    internal sealed class JournalSourceCreditException : Exception
    {
    }

    /// <summary>每会话一个有界流式游标。捕获终点和代际；不存在全量块索引。</summary>
    public sealed class CommittedBlockCatalog : IDisposable
    {
        private readonly Stream stream;
        private readonly long committedEnd;
        private readonly KeyMaterial key;
        private readonly VerifiedFileHeader header;
        private readonly BlockCodec codec;
        private readonly byte[] scratch;
        private readonly Action validate;
        private readonly Action checkThread;
        private readonly Action released;
        private bool disposed;
        internal IJournalSourceReadAuthorization ReadAuthorization
        {
            get;
            set;
        }
        public long ConfirmedEnd
        {
            get;
        }
        internal VerifiedFileHeader Header => header;
        public long CleanupGeneration
        {
            get;
        }
        public long Position
        {
            get;
            private set;
        }
        = 512;

        internal CommittedBlockCatalog(
            Stream stream,
            KeyMaterial key,
            VerifiedFileHeader header,
            BlockCodec codec,
            byte[] scratch,
            long end,
            long committedEnd,
            long cleanupGeneration,
            Action validate,
            Action checkThread,
            Action released
        )
        {
            this.stream = stream;
            this.key = key;
            this.header = header;
            this.codec = codec;
            this.scratch = scratch;
            ConfirmedEnd = end;
            this.committedEnd = committedEnd;
            CleanupGeneration = cleanupGeneration;
            this.validate = validate;
            this.checkThread = checkThread;
            this.released = released;
        }

        // 恢复游标时沿用已认证的块边界；随后读取仍受该游标创建时捕获的提交终点约束。
        internal void ResumeAuthenticatedBoundary(long offset)
        {
            ValidateLease();
            if (offset < 512 || offset > ConfirmedEnd)
                throw new JournalSnapshotBoundaryException();
            Position = offset;
        }

        internal void ValidateLease()
        {
            if (disposed)
                throw new ObjectDisposedException(nameof(CommittedBlockCatalog));
            validate();
        }

        /// <summary>最多返回一个经过认证的块。调用者拥有有界解码结果。</summary>
        public bool TryReadNext(out DecodedBlock block) => TryReadNext(long.MaxValue, out block, out _, out _);

        // 仅提供有界读取；不向传输层暴露任意偏移 I/O。
        internal bool TryReadNext(long sourceBudget, out DecodedBlock block, out int bytesRead, out bool budgetExhausted)
        {
            bytesRead = 0;
            budgetExhausted = false;
            if (disposed)
                throw new ObjectDisposedException(nameof(CommittedBlockCatalog));
            validate();
            block = null;
            if (Position == ConfirmedEnd)
                return false;
            if (ConfirmedEnd - Position < 256)
            {
                if (ConfirmedEnd < committedEnd)
                    throw new JournalSnapshotBoundaryException();
                throw new StorageFormatException();
            }
            try
            {
                if (sourceBudget < 256)
                {
                    budgetExhausted = true;
                    return false;
                }
                if (ReadAuthorization != null && !ReadAuthorization.TryConsume(256))
                    throw new JournalSourceCreditException();
                stream.Position = Position;
                JournalIo.ReadExactly(stream, scratch, 0, 256);
                bytesRead = 256;
                uint declared = Wire.U32(scratch, 12);
                if (
                    declared < 320
                    || declared > scratch.Length
                    || declared > committedEnd - Position
                )
                    throw new StorageFormatException();
                // 先以实际已提交源范围限制源声明。
                // 请求的截止范围更短，不能证明源块损坏。
                if (declared > ConfirmedEnd - Position)
                    throw new JournalSnapshotBoundaryException();
                int length = (int)declared;
                if (length > sourceBudget)
                {
                    budgetExhausted = true;
                    return false;
                }
                if (ReadAuthorization != null && !ReadAuthorization.TryConsume(length - 256))
                    throw new JournalSourceCreditException();
                JournalIo.ReadExactly(stream, scratch, 256, length - 256);
                bytesRead = length;
                block = codec.Decode(header, key, scratch, 0, length);
                Position += length;
                return true;
            }
            finally
            {
                Wire.Clear(scratch);
            }
        }

        public void Dispose()
        {
            if (disposed)
                return;
            checkThread();
            bool closed = false;
            try
            {
                stream.Dispose();
                closed = true;
            }
            finally
            {
                try
                {
                    key.Dispose();
                }
                finally
                {
                    if (closed || JournalIo.IsClosed(stream))
                    {
                        disposed = true;
                        released();
                    }
                }
            }
        }
    }

    // 仅调用者提供的捕获快照未落在块边界上；不据此认定源损坏。
    internal sealed class JournalSnapshotBoundaryException : Exception
    {
    }
}
