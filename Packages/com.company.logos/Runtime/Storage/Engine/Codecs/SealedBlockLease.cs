using System;
using System.Security.Cryptography;

namespace Logos.Storage
{
    /// <summary>单次内部封块凭证。摘要绑定封块时的字节；不接受外部的已验证标志。</summary>
    internal sealed class SealedBlockLease
    {
        private readonly BoundedBlockStaging owner;
        private readonly long slotGeneration;
        private readonly byte[] headerHash;
        private readonly byte[] sealedHash;
        private readonly byte[] keyFingerprint;
        private bool consumed;
        internal long Day
        {
            get;
        }
        internal long WriteGeneration
        {
            get;
        }
        internal int Length
        {
            get;
        }
        internal bool IsErrorClass
        {
            get;
        }

        internal SealedBlockLease(BoundedBlockStaging owner, long slotGeneration,
            VerifiedFileHeader header, KeyMaterial key, long writeGeneration, byte[] wire, int length, bool isErrorClass)
        {
            this.owner = owner;
            this.slotGeneration = slotGeneration;
            headerHash = (byte[])header.Hash.Clone();
            keyFingerprint = key.CopyMaterialFingerprint();
            Day = header.DayId;
            WriteGeneration = writeGeneration;
            Length = length;
            IsErrorClass = isErrorClass;
            using (var sha = SHA256.Create())
                sealedHash = sha.ComputeHash(wire, 0, length);
        }

        // 取得字节时向原槽位出示自身和封块代际，借此识别已经释放或已被下一块复用的缓冲区。
        internal bool TryConsume(out ArraySegment<byte> bytes)
        {
            bytes = default;
            if (consumed)
                return false;
            // 失败也结束本次凭证；只能通过新的封块取得下一代凭证。
            consumed = true;
            return owner.TryBorrowLease(this, slotGeneration, out bytes);
        }

        internal bool MatchesHeader(VerifiedFileHeader header) =>
            header != null && Day == header.DayId && Wire.Equal(headerHash, header.Hash);

        internal bool MatchesKey(KeyMaterial key) => key != null && key.MatchesMaterialFingerprint(keyFingerprint);

        internal bool TryAuthenticate(byte[] wire, int offset, int length, out byte[] hash)
        {
            hash = null;
            if (length != Length)
                return false;
            using (var sha = SHA256.Create())
                hash = sha.ComputeHash(wire, offset, length);
            if (Wire.Equal(hash, sealedHash))
                return true;
            Wire.Clear(hash);
            hash = null;
            return false;
        }

        internal ReadOnlySpan<byte> SealedDigest => sealedHash;

        internal bool TryAuthenticateMapped(ReadOnlySpan<byte> wire, out byte[] hash)
        {
            hash = null;
            if (wire.Length != Length)
                return false;
            Span<byte> measured = stackalloc byte[32];
            using (var sha = SHA256.Create())
                if (!sha.TryComputeHash(wire, measured, out int written) || written != measured.Length)
                    return false;
            if (!CryptographicOperations.FixedTimeEquals(measured, sealedHash))
                return false;
            hash = measured.ToArray();
            return true;
        }

        internal void Invalidate()
        {
            consumed = true;
            Wire.Clear(headerHash);
            Wire.Clear(sealedHash);
            Wire.Clear(keyFingerprint);
        }
    }
}
