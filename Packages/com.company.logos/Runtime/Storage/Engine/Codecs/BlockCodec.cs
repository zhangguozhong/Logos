using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Logos.Storage
{
    /// <summary>工作线程独占的有界编解码器。三个固定 64 KiB 工作缓冲区，不保留记录引用。</summary>
    public sealed class BlockCodec : IDisposable
    {
        private readonly RecordCodec records;
        private readonly IStorageEntropy entropy;
        private readonly Action<DecodeOperation> beforeDecodeOperation;
        private readonly Func<SHA256> createContextHash;
        // 编解码器由单个工作线程独占；只复用非密钥摘要资源，不缓存上下文或校验结果。
        private SHA256 contextHash;
        private readonly byte[] compressed = new byte[65536],
            payload = new byte[65536],
            plaintext = new byte[65536],
            headerScratch = new byte[256],
            contextScratch = new byte[2048],
            epoch = new byte[16];
        private bool epochReady,
            exhausted,
            disposed;
        private ulong sequence;
        internal static readonly byte[] Domain = Encoding.ASCII.GetBytes(
            "Logos.Storage/Block/v1\0"
        );
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("LOGOSB01"),
            FooterMagic = Encoding.ASCII.GetBytes("LOGOSEND");
        // 域分隔常量跨调用复用，不保留密钥或会话密码状态。
        private static readonly byte[] AuthorizationDomain = Encoding.ASCII.GetBytes("Logos.Storage/Authorization/v1\0"),
            AppSessionDomain = Encoding.ASCII.GetBytes("Logos.Storage/AppSession/v1\0"),
            UnityVisitDomain = Encoding.ASCII.GetBytes("Logos.Storage/UnityVisit/v1\0"),
            UnityInstanceDomain = Encoding.ASCII.GetBytes("Logos.Storage/UnityInstance/v1\0");
        public const int MinPhysicalWireBlockBytes = 320;
        public int WorkspaceBytes => 3 * 65536 + 256 + 2048 + 16;

        public BlockCodec(RecordCodec records)
            : this(records, new CryptoEntropy())
        {
        }

        internal BlockCodec(
            RecordCodec records,
            IStorageEntropy entropy,
            Action<DecodeOperation> beforeDecodeOperation = null,
            Func<SHA256> createContextHash = null
        )
        {
            this.records = records ?? throw new ArgumentNullException(nameof(records));
            this.entropy = entropy ?? throw new ArgumentNullException(nameof(entropy));
            this.beforeDecodeOperation = beforeDecodeOperation;
            this.createContextHash = createContextHash ?? SHA256.Create;
        }

        // 新协议容器获得独立 epoch，不重新分配编解码工作区。
        internal void BeginIndependentContainer()
        {
            Check();
            if (exhausted)
                throw new System.Security.Cryptography.CryptographicException("Codec crypto unavailable.");
            Wire.Clear(epoch);
            epochReady = false;
            sequence = 0;
        }

        public BlockMetadata CreateBlockMetadata(
            VerifiedFileHeader header,
            KeyMaterial key,
            LogRecord record
        )
        {
            Check();
            CheckFile(header, key);
            if (record == null)
                throw new ArgumentNullException(nameof(record));
            var metadata = new BlockMetadata(header, record, Fingerprint(record.Context));
            byte[] mac = key.CopyAuthenticationKey();
            try
            {
                FillDigests(metadata.Digests, header, record.Context, mac);
                return metadata;
            }
            finally
            {
                Wire.Clear(mac);
                Wire.Clear(contextScratch);
            }
        }

        /// <summary>先检查上下文一致性，再推进摘要。调用者仅在 TryEncode 成功后调用。</summary>
        public bool TryIncludeRecord(BlockMetadata metadata, LogRecord record)
        {
            Check();
            if (
                metadata == null
                || record == null
                || metadata.RecordCount >= 256
                || metadata.IsErrorClass != (record.Level >= LogLevel.Error)
                || metadata.Context.DiagnosticUserId != record.Context.DiagnosticUserId
                || !Equals(metadata.Context.Device, record.Context.Device)
                || metadata.IdentityContextVersion != record.Context.IdentityContextVersion
            )
                return false;
            byte[] fingerprint = Fingerprint(record.Context);
            try
            {
                if (!Wire.Equal(fingerprint, metadata.Fingerprint))
                    return false;
            }
            finally
            {
                Wire.Clear(fingerprint);
                Wire.Clear(contextScratch);
            }
            IncludeBounds(metadata, record);
            return true;
        }

        // 仅供独占暂存槽使用，首条已验证并生成指纹。同一不可变快照只需推进块统计。
        internal bool TryIncludeStagedRecord(BlockMetadata metadata, LogRecord record)
        {
            if (!ReferenceEquals(metadata.Context, record.Context)) return TryIncludeRecord(metadata, record);
            Check();
            if (metadata.RecordCount >= 256 || metadata.IsErrorClass != (record.Level >= LogLevel.Error)) return false;
            IncludeBounds(metadata, record);
            return true;
        }

        private static void IncludeBounds(BlockMetadata metadata, LogRecord record)
        {
            metadata.RecordCount++;
            metadata.MinimumUtcTicks = Math.Min(
                metadata.MinimumUtcTicks,
                record.UtcTimestamp.Ticks
            );
            metadata.MaximumUtcTicks = Math.Max(
                metadata.MaximumUtcTicks,
                record.UtcTimestamp.Ticks
            );
            metadata.MinimumSequence = Math.Min(metadata.MinimumSequence, record.Sequence);
            metadata.MaximumSequence = Math.Max(metadata.MaximumSequence, record.Sequence);
        }

        private byte[] Fingerprint(LogContext c)
        {
            int at = 0;
            Wire.Put64(contextScratch, at, c.IdentityContextVersion);
            at += 8;
            PutContext(c.AppSessionId, ref at);
            PutContext(c.UnityInstanceId, ref at);
            PutContext(c.UnityVisitId, ref at);
            if (contextHash == null)
                contextHash = createContextHash() ?? throw new CryptographicException("Context hash unavailable.");
            try
            {
                // 每条记录仍重新计算完整原始 UTF-16 摘要，不能携带上次计算的中间状态。
                contextHash.Initialize();
                return contextHash.ComputeHash(contextScratch, 0, at);
            }
            catch
            {
                var failed = contextHash;
                contextHash = null;
                try
                {
                    failed.Dispose();
                }
                finally
                {
                    Wire.Clear(contextScratch);
                }
                throw;
            }
        }

        private void PutContext(string id, ref int at)
        {
            // 边界比较使用线格式替换之前原始 UTF-16 代码单元的相等关系。
            Wire.Put32(contextScratch, at, (uint)id.Length);
            at += 4;
            for (int i = 0; i < id.Length; i++)
            {
                Wire.Put16(contextScratch, at, id[i]);
                at += 2;
            }
        }

        private void FillDigests(byte[] target, VerifiedFileHeader h, LogContext c, byte[] mac)
        {
            using (var hmac = new HMACSHA256(mac))
            {
                FillDomainDigests(target, h, c, hmac);
            }
        }

        private void FillDomainDigests(byte[] target, VerifiedFileHeader h, LogContext c, HMACSHA256 hmac)
        {
            Wire.Put64(contextScratch, 0, c.IdentityContextVersion);
            Digest(target, 0, AuthorizationDomain, h, hmac, 8);
            int n = Encoding.UTF8.GetBytes(
                c.AppSessionId,
                0,
                c.AppSessionId.Length,
                contextScratch,
                0
            );
            Digest(target, 32, AppSessionDomain, h, hmac, n);
            n = Encoding.UTF8.GetBytes(c.UnityVisitId, 0, c.UnityVisitId.Length, contextScratch, 0);
            Digest(target, 64, UnityVisitDomain, h, hmac, n);
            n = Encoding.UTF8.GetBytes(
                c.UnityInstanceId,
                0,
                c.UnityInstanceId.Length,
                contextScratch,
                0
            );
            Digest(target, 96, UnityInstanceDomain, h, hmac, n);
        }

        private void Digest(
            byte[] target,
            int offset,
            byte[] domain,
            VerifiedFileHeader h,
            HMACSHA256 hmac,
            int length
        )
        {
            // 四个域各自计算完整 HMAC，只在本次建块调用中共用密钥调度。
            hmac.Initialize();
            hmac.TransformBlock(domain, 0, domain.Length, null, 0);
            hmac.TransformBlock(h.DirectoryId, 0, h.DirectoryId.Length, null, 0);
            hmac.TransformBlock(contextScratch, 0, length, null, 0);
            hmac.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            byte[] hash = hmac.Hash;
            try
            {
                Buffer.BlockCopy(hash, 0, target, offset, 32);
            }
            finally
            {
                Wire.Clear(hash);
            }
        }

        /// <summary>将已暂存的 schema 正文封装到调用者的待处理槽位。槽位可别名：压缩完成后才开始输出。</summary>
        public bool TrySeal(
            VerifiedFileHeader h,
            KeyMaterial key,
            BlockMetadata m,
            byte[] plain,
            int plainOffset,
            int plainLength,
            byte[] destination,
            int offset,
            int capacity,
            out int written,
            out BlockSealFailure failure
        )
        {
            Check();
            written = 0;
            failure = BlockSealFailure.InvalidInput;
            if (
                plain == null
                || destination == null
                || m == null
                || plainOffset < 0
                || plainLength < 79
                || plainLength > 65536
                || plainOffset > plain.Length - plainLength
                || offset < 0
                || capacity < 0
                || offset > destination.Length - capacity
                || m.RecordCount < 1
                || m.RecordCount > 256
                || (Wire.U16(plain, plainOffset) != 1 && Wire.U16(plain, plainOffset) != 2)
                || Wire.U16(plain, plainOffset + 2) != m.RecordCount
                || m.DayId != h?.DayId
                || m.PolicyVersion != h.PolicyVersion
                || !Wire.Equal(m.FileHeaderHash, h.Hash)
            )
                return false;
            if (exhausted)
            {
                failure = BlockSealFailure.CryptoUnavailable;
                return false;
            }
            CheckFile(h, key);
            // 压缩失败时输出长度未知，保守清空整块；正常输出只需清除写过的前缀。
            int compressedToClear = compressed.Length, payloadToClear = 0;
            byte[] enc = null,
                mac = null;
            try
            {
                // 写入别名目标前，始终先将选定载荷复制到自有工作区。
                if (m.ContextBytes != 0)
                {
                    if (m.ContextBytes > plaintext.Length - plainLength)
                        return false;
                    Wire.Put16(plaintext, 0, 3);
                    Wire.Put16(plaintext, 2, (ushort)m.RecordCount);
                    RecordCodec.WriteBlockContext(m.Context, plaintext);
                    Buffer.BlockCopy(plain, plainOffset + 4, plaintext, 4 + m.ContextBytes, plainLength - 4);
                    plain = plaintext;
                    plainOffset = 0;
                    plainLength += m.ContextBytes;
                }
                int packedLength = TryCompress(plain, plainOffset, plainLength);
                // 只有压缩后更小时才选用压缩正文。块的配额与写入令牌按最终带填充、头尾和认证码的长度算。
                bool deflate = packedLength > 0 && packedLength < plainLength;
                int selectedLength = deflate ? packedLength : plainLength;
                if (packedLength > 0) compressedToClear = packedLength;
                byte[] cipherInput = compressed;
                if (!deflate)
                {
                    // 未压缩明文可能与目标数组重叠，先复制到专有缓冲区再写块头。
                    payloadToClear = selectedLength;
                    Buffer.BlockCopy(plain, plainOffset, payload, 0, selectedLength);
                    cipherInput = payload;
                }
                int cipherLength = (selectedLength / 16 + 1) * 16;
                int wireLength = 256 + cipherLength + 48;
                if (wireLength > capacity)
                {
                    failure = BlockSealFailure.DestinationTooSmall;
                    return false;
                }
                if (!epochReady)
                {
                    FillEntropy(epoch);
                    epochReady = true;
                }
                ulong id = sequence;
                if (sequence == ulong.MaxValue)
                    exhausted = true;
                else
                    sequence++;
                enc = key.CopyEncryptionKey();
                mac = key.CopyAuthenticationKey();
                Array.Clear(destination, offset, 256);
                Buffer.BlockCopy(Magic, 0, destination, offset, 8);
                Wire.Put16(destination, offset + 8, 1);
                Wire.Put16(destination, offset + 10, 256);
                Wire.Put32(destination, offset + 12, (uint)wireLength);
                Wire.Put32(destination, offset + 16, (uint)plainLength);
                Wire.Put32(destination, offset + 20, (uint)selectedLength);
                destination[offset + 24] = (byte)(m.IsErrorClass ? 1 : 0);
                destination[offset + 25] = (byte)(deflate ? 1 : 0);
                destination[offset + 26] = 1;
                Wire.Put64(destination, offset + 28, h.DayId);
                Wire.Put64(destination, offset + 36, h.PolicyVersion);
                Buffer.BlockCopy(epoch, 0, destination, offset + 44, 16);
                Wire.PutU64(destination, offset + 60, id);
                WriteMetadata(destination, offset, m);
                byte[] iv = new byte[16];
                FillEntropy(iv);
                Buffer.BlockCopy(iv, 0, destination, offset + 104, 16);
                using (var aes = Aes.Create())
                {
                    aes.KeySize = 256;
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;
                    aes.Key = enc;
                    aes.IV = iv;
                    using (var t = aes.CreateEncryptor())
                    {
                        int full = selectedLength / 16 * 16,
                            n = 0;
                        if (full > 0)
                            n = t.TransformBlock(cipherInput, 0, full, destination, offset + 256);
                        byte[] tail = t.TransformFinalBlock(cipherInput, full, selectedLength - full);
                        try
                        {
                            Buffer.BlockCopy(tail, 0, destination, offset + 256 + n, tail.Length);
                            if (n + tail.Length != cipherLength)
                                throw new CryptographicException();
                        }
                        finally
                        {
                            Wire.Clear(tail);
                        }
                    }
                }
                int footer = offset + wireLength - 16;
                Array.Clear(destination, footer, 16);
                Buffer.BlockCopy(FooterMagic, 0, destination, footer, 8);
                Wire.Put32(destination, footer + 8, (uint)wireLength);
                Wire.MacInto(
                    mac,
                    destination,
                    offset + 256 + cipherLength,
                    new ArraySegment<byte>(Domain),
                    new ArraySegment<byte>(h.Hash),
                    new ArraySegment<byte>(destination, offset, 256),
                    new ArraySegment<byte>(destination, offset + 256, cipherLength),
                    new ArraySegment<byte>(destination, footer, 16)
                );
                written = wireLength;
                failure = BlockSealFailure.None;
                return true;
            }
            catch (CryptographicException)
            {
                exhausted = true;
                failure = BlockSealFailure.CryptoUnavailable;
                if (capacity > 0)
                    Array.Clear(
                        destination,
                        offset,
                        Math.Min(capacity, StorageRuntimeLimits.MaxWireBlockBytes)
                    );
                return false;
            }
            finally
            {
                Wire.Clear(enc);
                Wire.Clear(mac);
                Array.Clear(compressed, 0, compressedToClear);
                if (ReferenceEquals(plain, plaintext))
                    Array.Clear(plaintext, 0, plainLength);
                if (payloadToClear != 0) Array.Clear(payload, 0, payloadToClear);
            }
        }

        private void FillEntropy(byte[] buffer)
        {
            try
            {
                entropy.Fill(buffer);
            }
            catch (Exception)
            {
                throw new CryptographicException("Storage entropy unavailable.");
            }
        }

        // 压缩只影响块的线格式。读取时须同时遵守声明的明文长度和固定工作区上限。
        private int TryCompress(byte[] source, int offset, int count)
        {
            try
            {
                using (var stream = new FixedBufferStream(compressed, 0, compressed.Length))
                {
                    using (var zip = new DeflateStream(stream, CompressionLevel.Fastest, true))
                        zip.Write(source, offset, count);
                    return (int)stream.Length;
                }
            }
            catch (BufferCapacityException)
            {
                return 0;
            }
        }

        private static void WriteMetadata(byte[] b, int offset, BlockMetadata m)
        {
            Wire.Put16(b, offset + 68, (ushort)m.RecordCount);
            Wire.Put64(b, offset + 72, m.MinimumUtcTicks);
            Wire.Put64(b, offset + 80, m.MaximumUtcTicks);
            Wire.Put64(b, offset + 88, m.MinimumSequence);
            Wire.Put64(b, offset + 96, m.MaximumSequence);
            Buffer.BlockCopy(m.Digests, 0, b, offset + 120, 96);
            Wire.Put64(b, offset + 216, m.IdentityContextVersion);
            Buffer.BlockCopy(m.Digests, 96, b, offset + 224, 32);
        }

        public DecodedBlock Decode(VerifiedFileHeader h, KeyMaterial key, byte[] wire) =>
            Decode(h, key, wire, 0, wire?.Length ?? 0);

        public DecodedBlock Decode(
            VerifiedFileHeader h,
            KeyMaterial key,
            byte[] wire,
            int offset,
            int length
        )
        {
            Check();
            CheckFile(h, key);
            if (
                wire == null
                || offset < 0
                || length < MinPhysicalWireBlockBytes
                || length > 65856
                || offset > wire.Length - length
                || (length - 304) % 16 != 0
            )
                throw new StorageFormatException();
            int cipherLength = length - 304;
            byte[] enc = null,
                mac = key.CopyAuthenticationKey();
            try
            {
                Wire.MacInto(
                    mac,
                    contextScratch,
                    0,
                    new ArraySegment<byte>(Domain),
                    new ArraySegment<byte>(h.Hash),
                    new ArraySegment<byte>(wire, offset, 256),
                    new ArraySegment<byte>(wire, offset + 256, cipherLength),
                    new ArraySegment<byte>(wire, offset + length - 16, 16)
                );
                // 认证范围包括所属文件头的摘要、块头、密文和尾部。全部匹配后才能解密，防止同密钥下的块被移用到另一个文件。
                if (!Wire.Equal(contextScratch, 0, wire, offset + 256 + cipherLength, 32))
                    throw new StorageAuthenticationException();
                Buffer.BlockCopy(wire, offset, headerScratch, 0, 256);
                ValidateHeader(headerScratch, h, wire, offset + length - 16, length, cipherLength);
                beforeDecodeOperation?.Invoke(DecodeOperation.Decrypt);
                enc = key.CopyEncryptionKey();
                int payloadLength;
                try
                {
                    using (var aes = Aes.Create())
                    {
                        aes.KeySize = 256;
                        aes.Mode = CipherMode.CBC;
                        aes.Padding = PaddingMode.PKCS7;
                        aes.Key = enc;
                        aes.IV = Wire.Slice(headerScratch, 104, 16);
                        using (var t = aes.CreateDecryptor())
                        {
                            int n = t.TransformBlock(
                                wire,
                                offset + 256,
                                cipherLength - 16,
                                payload,
                                0
                            );
                            byte[] tail = t.TransformFinalBlock(
                                wire,
                                offset + 256 + cipherLength - 16,
                                16
                            );
                            try
                            {
                                if (n + tail.Length > payload.Length)
                                    throw new StorageFormatException();
                                Buffer.BlockCopy(tail, 0, payload, n, tail.Length);
                                payloadLength = n + tail.Length;
                            }
                            finally
                            {
                                Wire.Clear(tail);
                            }
                        }
                    }
                }
                catch (CryptographicException)
                {
                    throw new StorageFormatException();
                }
                if (payloadLength != Wire.U32(headerScratch, 20))
                    throw new StorageFormatException();
                int plainLength = (int)Wire.U32(headerScratch, 16);
                if (headerScratch[25] == 0)
                    Buffer.BlockCopy(payload, 0, plaintext, 0, plainLength);
                else
                    Inflate(payloadLength, plainLength);
                LogRecord[] decoded = records.Decode(plaintext, 0, plainLength);
                if (decoded.Length != Wire.U16(headerScratch, 68))
                    throw new StorageFormatException();
                var expected = CreateBlockMetadata(h, key, decoded[0]);
                for (int i = 1; i < decoded.Length; i++)
                    if (!TryIncludeRecord(expected, decoded[i]))
                        throw new StorageFormatException();
                Array.Clear(contextScratch, 0, 256);
                WriteMetadata(contextScratch, 0, expected);
                if (
                    (decoded[0].Level >= LogLevel.Error ? 1 : 0) != headerScratch[24]
                    || !Wire.Equal(contextScratch, 68, headerScratch, 68, 36)
                    || !Wire.Equal(contextScratch, 120, headerScratch, 120, 136)
                )
                    throw new StorageFormatException();
                return new DecodedBlock(decoded, headerScratch, length);
            }
            finally
            {
                Wire.Clear(enc);
                Wire.Clear(mac);
                Wire.Clear(payload);
                Wire.Clear(plaintext);
                Wire.Clear(contextScratch);
                Wire.Clear(headerScratch);
            }
        }

        private void Inflate(int packedLength, int plainLength)
        {
            beforeDecodeOperation?.Invoke(DecodeOperation.Inflate);
            try
            {
                using (var stream = new FixedBufferStream(payload, 0, packedLength, true))
                using (var zip = new DeflateStream(stream, CompressionMode.Decompress))
                {
                    int at = 0;
                    while (at < plainLength)
                    {
                        int n = zip.Read(plaintext, at, plainLength - at);
                        if (n == 0)
                            throw new StorageFormatException();
                        at += n;
                    }
                    // 刚好读满声明长度还需确认解压流结束，否则超出声明的输出会被误认为有效。
                    if (zip.ReadByte() != -1)
                        throw new StorageFormatException();
                }
            }
            catch (InvalidDataException)
            {
                throw new StorageFormatException();
            }
            catch (IOException)
            {
                throw new StorageFormatException();
            }
        }

        private static void ValidateHeader(
            byte[] b,
            VerifiedFileHeader h,
            byte[] wire,
            int footer,
            int wireLength,
            int cipherLength
        )
        {
            uint plain = Wire.U32(b, 16),
                packed = Wire.U32(b, 20);
            if (
                !Wire.Equal(b, 0, Magic, 0, 8)
                || Wire.U16(b, 8) != 1
                || Wire.U16(b, 10) != 256
                || Wire.U32(b, 12) != wireLength
                || plain < 79
                || plain > 65536
                || packed < 1
                || packed > 65536
                || cipherLength != (packed / 16 + 1) * 16
                || b[24] > 1
                || b[25] > 1
                || b[26] != 1
                || b[27] != 0
                || Wire.I64(b, 28) != h.DayId
                || Wire.I64(b, 36) != h.PolicyVersion
                || Wire.U16(b, 68) < 1
                || Wire.U16(b, 68) > 256
                || Wire.U16(b, 70) != 0
                || Wire.I64(b, 216) < 0
                || !Wire.Equal(wire, footer, FooterMagic, 0, 8)
                || Wire.U32(wire, footer + 8) != wireLength
                || Wire.U32(wire, footer + 12) != 0
                || (b[25] == 0 && plain != packed)
                || (b[25] == 1 && packed >= plain)
            )
                throw new StorageFormatException();
        }

        private static void CheckFile(VerifiedFileHeader h, KeyMaterial key)
        {
            if (h == null || key == null)
                throw new StorageFormatException();
            h.Reverify(key);
        }

        private void Check()
        {
            if (disposed)
                throw new ObjectDisposedException(nameof(BlockCodec));
        }

        public void Dispose()
        {
            if (disposed)
                return;
            Wire.Clear(compressed);
            Wire.Clear(payload);
            Wire.Clear(plaintext);
            Wire.Clear(headerScratch);
            Wire.Clear(contextScratch);
            Wire.Clear(epoch);
            disposed = true;
            var hash = contextHash;
            contextHash = null;
            hash?.Dispose();
        }

        // 仅供测试便利：生产路径暂存 TryEncode 记录后，在同一个有界类别槽位中调用 TrySeal。
        internal byte[] Encode(
            VerifiedFileHeader h,
            KeyMaterial key,
            IReadOnlyList<LogRecord> input
        )
        {
            if (input == null || input.Count < 1 || input.Count > 256)
                throw new StorageFormatException();
            var metadata = CreateBlockMetadata(h, key, input[0]);
            for (int i = 1; i < input.Count; i++)
                if (!TryIncludeRecord(metadata, input[i]))
                    throw new StorageFormatException();
            byte[] plain = records.Encode(input),
                wire = new byte[65856];
            try
            {
                int written;
                BlockSealFailure failure;
                if (
                    !TrySeal(
                        h,
                        key,
                        metadata,
                        plain,
                        0,
                        plain.Length,
                        wire,
                        0,
                        wire.Length,
                        out written,
                        out failure
                    )
                )
                    throw new StorageFormatException();
                return Wire.Slice(wire, 0, written);
            }
            finally
            {
                Wire.Clear(plain);
                Wire.Clear(wire);
            }
        }
    }
}
