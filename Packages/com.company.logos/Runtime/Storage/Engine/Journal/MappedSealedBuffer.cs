using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using System.Threading;

namespace Logos.Storage
{
    // 只有持有日志目录租约的 worker 可以读写和释放映射。
    internal sealed class MappedSealedBuffer : IDisposable
    {
        internal const string FileName = ".logos-sealed-v1";
        private const int HeaderSize = 160;
        private const int TargetOffset = 120;
        private const int TagOffset = 88;
        private const int SlotSize = HeaderSize + StorageRuntimeLimits.MaxWireBlockBytes;
        internal const int FileBytes = 2 * SlotSize;
        private const int PublishedV1 = 0x4D534C31;
        private const int Published = 0x4D534C32;
        private static readonly byte[] Domain = { 76, 111, 103, 111, 115, 77, 97, 112, 49 };
        private readonly MemoryMappedFile map;
        private readonly MemoryMappedViewAccessor view;
        private readonly IncrementalHash mac;
        private readonly byte[] header = new byte[HeaderSize];
        private readonly byte[] binding = new byte[40];

        internal MappedSealedBuffer(string path, bool create, KeyMaterial controlKey)
        {
            using (var file = new FileStream(path, create ? FileMode.CreateNew : FileMode.Open,
                FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                if (create)
                {
                    // 先写满零页并完成文件级同步。仅 SetLength 可能留下稀疏区，首次访问映射页时才暴露空间不足。
                    var zero = new byte[4096];
                    for (int bytes = FileBytes; bytes > 0; bytes -= zero.Length)
                        file.Write(zero, 0, Math.Min(bytes, zero.Length));
                    file.Flush(true);
                }
                if (file.Length != FileBytes) throw new InvalidDataException("Invalid sealed map size.");
                map = MemoryMappedFile.CreateFromFile(file, null, FileBytes, MemoryMappedFileAccess.ReadWrite,
                    HandleInheritability.None, false);
                try { view = map.CreateViewAccessor(); }
                catch { map.Dispose(); throw; }
                byte[] material = controlKey.CopyAuthenticationKey();
                try { mac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, material); }
                catch
                {
                    view.Dispose();
                    map.Dispose();
                    throw;
                }
                finally { Wire.Clear(material); }
            }
        }

        private static long Offset(bool error) => error ? SlotSize : 0;
        internal bool HasSlot(bool error) => view.ReadInt32(Offset(error)) != 0;

        // 已绑定且尚待确认的槽优先于未绑定槽，避免另一个块先占用它的目标偏移。
        internal bool RecoverErrorFirst()
        {
            long normal = HasSlot(false) ? view.ReadInt64(TargetOffset) : 0;
            long error = HasSlot(true) ? view.ReadInt64(Offset(true) + TargetOffset) : 0;
            return normal <= 0 || (error > 0 && error <= normal);
        }

        internal void Publish(bool error, ArraySegment<byte> wire, VerifiedFileHeader file, long cleanup,
            long utc, long expires, ReadOnlySpan<byte> sealedDigest)
        {
            long start = Offset(error);
            if (HasSlot(error) || wire.Count < StorageRuntimeLimits.MinWireBlockBytes ||
                wire.Count > StorageRuntimeLimits.MaxWireBlockBytes || utc < 0 || expires < utc || sealedDigest.Length != 32)
                throw new InvalidOperationException("Invalid sealed map publication.");
            Wire.Put32(header, 4, (uint)wire.Count);
            Wire.Put64(header, 8, file.DayId);
            Wire.Put64(header, 16, cleanup);
            Wire.Put64(header, 24, utc);
            Wire.Put64(header, 32, expires);
            Wire.Put32(header, 48, error ? 1U : 0U);
            Buffer.BlockCopy(file.Hash, 0, header, 56, 32);
            // 复用封块凭证已有摘要，热路径不再为映射 HMAC 扫描整个密文块。
            Authenticate(header.AsSpan(4, TagOffset - 4), sealedDigest, header.AsSpan(TagOffset, 32));
            view.WriteArray(start + 4, header, 4, HeaderSize - 4);
            view.WriteArray(start + HeaderSize, wire.Array, wire.Offset, wire.Count);
            Thread.MemoryBarrier();
            view.Write(start, Published);
        }

        internal enum State { Empty, Invalid, Stale, BeforeWindow, Expired, Ready }
        internal delegate JournalAppendResult Action(ReadOnlySpan<byte> wire, long appendOffset);

        internal unsafe State Read(bool error, VerifiedFileHeader file, long cleanup, long now,
            Action action, out JournalAppendResult result)
        {
            result = JournalAppendResult.NotReady;
            long start = Offset(error);
            int marker = view.ReadInt32(start);
            if (marker == 0) return State.Empty;
            if (marker != Published && marker != PublishedV1) return State.Invalid;
            byte* pointer = null;
            var handle = view.SafeMemoryMappedViewHandle;
            handle.AcquirePointer(ref pointer);
            try
            {
                var source = new ReadOnlySpan<byte>(pointer + view.PointerOffset + start, SlotSize);
                int length = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(4));
                if (length < StorageRuntimeLimits.MinWireBlockBytes || length > StorageRuntimeLimits.MaxWireBlockBytes)
                    return State.Invalid;
                var wire = source.Slice(HeaderSize, length);
                Span<byte> tag = stackalloc byte[32];
                if (marker == PublishedV1)
                    Authenticate(source.Slice(4, TagOffset - 4), wire, tag);
                else
                {
                    Span<byte> digest = stackalloc byte[32];
                    using (var sha = SHA256.Create())
                        if (!sha.TryComputeHash(wire, digest, out int written) || written != digest.Length)
                            return State.Invalid;
                    Authenticate(source.Slice(4, TagOffset - 4), digest, tag);
                }
                if (!CryptographicOperations.FixedTimeEquals(tag, source.Slice(TagOffset, 32)))
                    return State.Invalid;
                if (file == null || BinaryPrimitives.ReadInt64LittleEndian(source.Slice(8)) != file.DayId ||
                    BinaryPrimitives.ReadInt64LittleEndian(source.Slice(16)) != cleanup ||
                    BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(48)) != (error ? 1U : 0U) ||
                    !CryptographicOperations.FixedTimeEquals(source.Slice(56, 32), file.Hash))
                    return State.Stale;
                long created = BinaryPrimitives.ReadInt64LittleEndian(source.Slice(24));
                long expires = BinaryPrimitives.ReadInt64LittleEndian(source.Slice(32));
                long where = BinaryPrimitives.ReadInt64LittleEndian(source.Slice(TargetOffset));
                if (where != 0)
                {
                    Authenticate(source.Slice(TagOffset, 32), source.Slice(TargetOffset, 8), tag);
                    if (where < StorageRuntimeLimits.FileHeaderBytes ||
                        !CryptographicOperations.FixedTimeEquals(tag, source.Slice(TargetOffset + 8, 32)))
                        return State.Invalid;
                }
                else
                {
                    // 目标偏移被清零但仍留有认证字节时，无法证明该槽从未追加。
                    var targetTag = source.Slice(TargetOffset + 8, 32);
                    for (int i = 0; i < targetTag.Length; ++i)
                        if (targetTag[i] != 0)
                            return State.Invalid;
                }
                if (created < 0 || expires < created)
                    return State.Invalid;
                if (now < created) return State.BeforeWindow;
                if (now >= expires) return State.Expired;
                result = action(wire, where == 0 ? -1 : where);
                return State.Ready;
            }
            finally { handle.ReleasePointer(); }
        }

        // 目标绑定独立于已发布的密文认证；先完成绑定，再开始写正式文件。
        internal void BindAppend(bool error, long appendOffset)
        {
            long start = Offset(error);
            view.ReadArray(start + TagOffset, header, TagOffset, 32);
            Wire.Put64(binding, 0, appendOffset);
            Authenticate(header.AsSpan(TagOffset, 32), binding.AsSpan(0, 8), binding.AsSpan(8, 32));
            // 先写非零目标偏移。半写绑定在恢复时因认证失败被拒绝。
            view.WriteArray(start + TargetOffset, binding, 0, binding.Length);
            Thread.MemoryBarrier();
        }

        internal unsafe JournalAppendResult AppendLive(bool error, JournalSession journal, SealedBlockLease lease)
        {
            long start = Offset(error);
            int length = view.ReadInt32(start + 4);
            if (view.ReadInt32(start) != Published || length != lease.Length ||
                length < StorageRuntimeLimits.MinWireBlockBytes || length > StorageRuntimeLimits.MaxWireBlockBytes)
                return JournalAppendResult.InvalidBlock;
            byte* pointer = null;
            var handle = view.SafeMemoryMappedViewHandle;
            handle.AcquirePointer(ref pointer);
            try
            {
                var wire = new ReadOnlySpan<byte>(pointer + view.PointerOffset + start + HeaderSize, length);
                BindAppend(error, journal.NextMappedAppendOffset);
                return journal.TryAppendMapped(lease, wire);
            }
            finally { handle.ReleasePointer(); }
        }

        internal void Retire(bool error, bool durable)
        {
            view.Write(Offset(error), 0);
            if (durable) view.Flush();
        }

        private void Authenticate(ReadOnlySpan<byte> data, ReadOnlySpan<byte> wire, Span<byte> tag)
        {
            mac.AppendData(Domain);
            mac.AppendData(data);
            mac.AppendData(wire);
            if (!mac.TryGetHashAndReset(tag, out int written) || written != tag.Length)
                throw new CryptographicException("Failed to authenticate sealed map.");
        }

        public void Dispose()
        {
            mac.Dispose();
            try { view.Dispose(); }
            finally { map.Dispose(); }
        }
    }
}
