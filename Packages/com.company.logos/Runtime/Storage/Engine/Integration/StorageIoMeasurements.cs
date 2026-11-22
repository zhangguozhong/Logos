using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
namespace Logos.Storage
{
    // 工作线程独占计数器覆盖数据、状态和目录操作。线格式速率仅限制数据尝试。
    // 流包装器在工作线程上分别记录写入、普通刷新和强制刷盘。一次提交可能包含多次状态及数据 I/O。
    internal sealed class StorageIoMeasurements : IJournalFileSystem, IJournalDurabilityFileSystem
    {
        private readonly IJournalFileSystem inner;
        internal bool SupportsDurability => inner is IJournalDurabilityFileSystem;
        internal long Calls, WriteCalls, FlushCalls, WrittenBytes;
        internal StorageIoMeasurements(IJournalFileSystem inner)
        {
            this.inner = inner;
        }
        public bool Exists(string path)
        {
            Calls++;
            return inner.Exists(path);
        }
        public JournalEntryInfo GetEntryInfo(string path)
        {
            Calls++;
            return inner.GetEntryInfo(path);
        }
        public void DeleteFile(string path)
        {
            Calls++;
            inner.DeleteFile(path);
        }
        public Stream Open(string path, FileMode mode, FileAccess access)
        {
            Calls++;
            return new MeasuredStream(this, inner.Open(path, mode, access));
        }
        public IEnumerable<string> EnumerateEntries(string path)
        {
            Calls++;
            return new MeasuredEntries(this, inner.EnumerateEntries(path));
        }
        public JournalForceSyncResult ForceSync(Stream stream)
        {
            if (!(inner is IJournalDurabilityFileSystem capability))
                return JournalForceSyncResult.Unsupported;
            Calls++;
            return capability.ForceSync(stream is MeasuredStream measured ? measured.Inner : stream);
        }
        private sealed class MeasuredEntries : IEnumerable<string>
        {
            private readonly StorageIoMeasurements owner;
            private readonly IEnumerable<string> inner;
            internal MeasuredEntries(StorageIoMeasurements owner, IEnumerable<string> inner)
            {
                this.owner = owner;
                this.inner = inner;
            }
            public IEnumerator<string> GetEnumerator() => new MeasuredEnumerator(owner, inner.GetEnumerator());
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }
        private sealed class MeasuredEnumerator : IEnumerator<string>
        {
            private readonly StorageIoMeasurements owner;
            private readonly IEnumerator<string> inner;
            internal MeasuredEnumerator(StorageIoMeasurements owner, IEnumerator<string> inner)
            {
                this.owner = owner;
                this.inner = inner;
            }
            public string Current => inner.Current;
            object IEnumerator.Current => Current;
            public bool MoveNext()
            {
                owner.Calls++;
                return inner.MoveNext();
            }
            public void Reset() => inner.Reset();
            public void Dispose()
            {
                owner.Calls++;
                inner.Dispose();
            }
        }
        private sealed class MeasuredStream : Stream
        {
            private readonly StorageIoMeasurements owner;
            internal readonly Stream Inner;
            internal MeasuredStream(StorageIoMeasurements owner, Stream inner)
            {
                this.owner = owner;
                Inner = inner;
            }
            public override bool CanRead => Inner.CanRead;
            public override bool CanWrite => Inner.CanWrite;
            public override bool CanSeek => Inner.CanSeek;
            public override long Length
            {
                get
                {
                    owner.Calls++;
                    return Inner.Length;
                }
            }
            public override long Position
            {
                get => Inner.Position;
                set => Inner.Position = value;
            }
            public override int Read(byte[] buffer, int offset, int count)
            {
                owner.Calls++;
                return Inner.Read(buffer, offset, count);
            }
            public override void Write(byte[] buffer, int offset, int count)
            {
                owner.Calls++;
                owner.WriteCalls++;
                Inner.Write(buffer, offset, count);
                owner.WrittenBytes += count;
            }
            public override void Flush()
            {
                owner.Calls++;
                owner.FlushCalls++;
                Inner.Flush();
            }
            public override long Seek(long offset, SeekOrigin origin)
            {
                owner.Calls++;
                return Inner.Seek(offset, origin);
            }
            public override void SetLength(long length)
            {
                owner.Calls++;
                Inner.SetLength(length);
            }
            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    owner.Calls++;
                    Inner.Dispose();
                }
                base.Dispose(disposing);
            }
        }
    }
}
