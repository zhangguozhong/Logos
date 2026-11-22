using System;
using System.IO;

namespace Logos.Storage
{
    // 压缩器和解压器通过流接口访问数组切片；容量来自调用方，因此长度和游标必须保持在分配好的区间内。
    /// <summary>不可扩容的流视图。不拥有缓冲区，Dispose 后流内不保留内容。</summary>
    internal sealed class FixedBufferStream : Stream
    {
        private readonly byte[] buffer;
        private readonly int offset,
            capacity;
        private int position,
            length;

        internal FixedBufferStream(byte[] buffer, int offset, int capacity, bool readable = false)
        {
            this.buffer = buffer;
            this.offset = offset;
            this.capacity = capacity;
            length = readable ? capacity : 0;
            CanRead = readable;
        }

        public override bool CanRead
        {
            get;
        }
        public override bool CanSeek => false;
        public override bool CanWrite => !CanRead;
        public override long Length => length;
        public override long Position
        {
            get => position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] target, int targetOffset, int count)
        {
            int n = Math.Min(count, length - position);
            Buffer.BlockCopy(buffer, offset + position, target, targetOffset, n);
            position += n;
            return n;
        }

        public override int ReadByte()
        {
            return position < length ? buffer[offset + position++] : -1;
        }

        public override void Write(byte[] source, int sourceOffset, int count)
        {
            if (count > capacity - position)
                throw new BufferCapacityException();
            Buffer.BlockCopy(source, sourceOffset, buffer, offset + position, count);
            position += count;
            length = position;
        }

        public override long Seek(long o, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }

    internal sealed class BufferCapacityException : IOException
    {
    }
}
