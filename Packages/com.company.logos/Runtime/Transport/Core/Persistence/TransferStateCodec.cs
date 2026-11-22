using System;
using System.Security.Cryptography;
using System.Text;
using Logos.Storage;
namespace Logos.Transport
{
    internal enum StatePageStatus
    {
        Zero, BadFormat, BadMac, MissingKey, Supported, AuthenticatedUnknown, BindingMismatch
    }
    internal sealed class StatePageResult
    {
        internal StatePageStatus Status
        {
            get;
        }
        // 仅使用已认证引用；绝不使用不可信的有界密钥定位信息。
        internal string KeyId
        {
            get;
        }
        internal TransferState State
        {
            get;
        }
        internal ulong Sequence
        {
            get;
        }
        internal long CleanupGeneration
        {
            get;
        }
        internal bool ClearReset
        {
            get;
        }
        internal int Version
        {
            get;
        }
        // 只在完整认证并验证支持格式后发布有效范围。
        internal int RecordBytes
        {
            get;
        }
        internal StatePageResult(StatePageStatus status, TransferState state = null, ulong sequence = 0, long cleanupGeneration = 0, bool clearReset = false, string keyId = null, int version = 0, int recordBytes = 0)
        {
            Version = version;
            RecordBytes = recordBytes;
            KeyId = keyId;
            Status = status;
            State = state;
            Sequence = sequence;
            CleanupGeneration = cleanupGeneration;
            ClearReset = clearReset;
        }
    }
    // 不访问文件。一个返回页（131072）、一个明文工作区（98304）、
    // 加密 CBC 临时区不超过 98320；解密直接写零初始化的逻辑明文工作区，
    // FinalBlock 尾部不超过 32 字节。密钥副本 64、IV 16 和 MAC 32 字节；运行时还会分配。
    // 这些仅是有界载荷分配，不代表 SDK 堆总量或零 GC。
    internal sealed partial class TransferStateCodec
    {
        internal const int PageBytes = 131072, PlainBytes = 98304, CipherBytes = 98320, TagOffset = 131040;
        internal const int CurrentVersion = 4;
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("LOGOST01"), Domain = Encoding.ASCII.GetBytes("Logos.Transport/State/v1\0");
        private readonly IStorageKeyProvider keys;
        private readonly byte[] directory, installationHash, destinationHash;
        private readonly Action<byte[]> entropy;
        private readonly Action beforeDecrypt;
        private readonly string installationId, destinationId;
        internal TransferStateCodec(IStorageKeyProvider keys, byte[] directory, string installation, string destination, Action<byte[]> entropy = null, Action beforeDecrypt = null)
        {
            if (keys == null || directory == null || directory.Length != 16)
                throw new ArgumentException("Explicit keys and directory identity required.");
            TransferValidation.Id(installation, nameof(installation));
            TransferValidation.Id(destination, nameof(destination));
            installationId = installation;
            destinationId = destination;
            this.keys = keys;
            this.directory = (byte[])directory.Clone();
            this.entropy = entropy;
            this.beforeDecrypt = beforeDecrypt;
            using (var sha = SHA256.Create())
            {
                installationHash = sha.ComputeHash(Encoding.ASCII.GetBytes(installation));
                destinationHash = sha.ComputeHash(Encoding.ASCII.GetBytes(destination));
            }
        }
        private static KeyMaterial Acquired(KeyAcquireResult result) => result != null && result.Status == KeyAcquireStatus.Acquired && result.Material != null ? result.Material : null;
        private KeyMaterial AcquireRequested(string id)
        {
            KeyMaterial key;
            try
            {
                key = Acquired(keys.TryAcquire(id));
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                return null;
            }
            if (key != null && key.KeyId != id)
            {
                key.Dispose();
                return null;
            }
            return key;
        }
        private void FillIv(byte[] iv)
        {
            if (entropy != null)
                entropy(iv);
            else
                using (var rng = RandomNumberGenerator.Create())
                    rng.GetBytes(iv);
        }
        internal byte[] Encode(TransferState state, ulong sequence, bool clearReset = false)
        {
            var page = new byte[PageBytes];
            EncodeInto(state, sequence, page, clearReset);
            return page;
        }
        // 发布器复用一个固定页面数组完成编码、写入、读回和恢复。
        internal int EncodeInto(TransferState state, ulong sequence, byte[] page, bool clearReset = false)
        {
            if (page == null || page.Length != PageBytes)
                throw new ArgumentException("One complete page required.", nameof(page));
            if (state == null || sequence == 0 || (clearReset && (sequence != 1 || !state.IsReset)))
                throw new ArgumentException("Invalid state sequence or clear reset.");
            ValidateStateBindings(state);
            Array.Clear(page, 0, page.Length);
            byte[] plain = null, enc = null, mac = null, iv = new byte[16], cipher = null;
            try
            {
                using (var key = Acquired(keys.TryAcquireCurrent()))
                {
                    if (key == null)
                        throw new InvalidOperationException("Transfer state key unavailable.");
                    plain = EncodePayload(state);
                    // 仅省略可确定重建的零后缀，PKCS7 在裁剪之后执行。
                    int plainLength = plain.Length;
                    while (plainLength > 256 && plain[plainLength - 1] == 0)
                        plainLength--;
                    int cipherLength = 16 * (plainLength / 16 + 1);
                    int recordBytes = 256 + cipherLength + 32;
                    enc = key.CopyEncryptionKey();
                    mac = key.CopyAuthenticationKey();
                    FillIv(iv);
                    Buffer.BlockCopy(Magic, 0, page, 0, 8);
                    Wire.Put16(page, 8, CurrentVersion);
                    Wire.Put16(page, 10, 256);
                    Wire.Put32(page, 12, (uint)recordBytes);
                    Wire.PutU64(page, 16, sequence);
                    Buffer.BlockCopy(directory, 0, page, 24, 16);
                    Buffer.BlockCopy(installationHash, 0, page, 40, 32);
                    Buffer.BlockCopy(destinationHash, 0, page, 72, 32);
                    page[104] = (byte)key.KeyId.Length;
                    Encoding.ASCII.GetBytes(key.KeyId, 0, key.KeyId.Length, page, 105);
                    page[169] = 1;
                    Buffer.BlockCopy(iv, 0, page, 176, 16);
                    Wire.Put32(page, 192, (uint)plainLength);
                    Wire.Put32(page, 196, (uint)cipherLength);
                    Wire.Put32(page, 200, 256);
                    Wire.Put64(page, 204, state.CleanupGeneration);
                    Wire.Put32(page, 212, clearReset ? 1u : 0u);
                    using (var aes = Aes.Create())
                    {
                        aes.KeySize = 256;
                        aes.Mode = CipherMode.CBC;
                        aes.Padding = PaddingMode.PKCS7;
                        aes.Key = enc;
                        aes.IV = iv;
                        using (var transform = aes.CreateEncryptor())
                            cipher = transform.TransformFinalBlock(plain, 0, plainLength);
                    }
                    if (cipher.Length != cipherLength)
                        throw new CryptographicException();
                    Buffer.BlockCopy(cipher, 0, page, 256, cipherLength);
                    Wire.MacInto(mac, page, recordBytes - 32, new ArraySegment<byte>(Domain), new ArraySegment<byte>(page, 0, recordBytes - 32));
                    return recordBytes;
                }
            }
            catch
            {
                Wire.Clear(page);
                throw;
            }
            finally
            {
                Wire.Clear(plain);
                Wire.Clear(enc);
                Wire.Clear(mac);
                Wire.Clear(iv);
                Wire.Clear(cipher);
            }
        }
        // 恢复时区分全零页、缺少密钥和认证失败，调用方据此决定是否可以初始化或修复副本。
        internal StatePageResult Decode(byte[] page)
        {
            if (page == null || page.Length != PageBytes)
                return new StatePageResult(StatePageStatus.BadFormat);
            bool zero = true;
            for (int i = 0; i < page.Length; i++)
                zero &= page[i] == 0;
            if (zero)
                return new StatePageResult(StatePageStatus.Zero);
            byte[] enc = null, mac = null, tag = null, iv = null, plain = null, finalPlain = null;
            try
            {
                // 总长只用于有界定位认证码；认证成功前不采信任何业务字段。
                uint untrustedBytes = Wire.U32(page, 12);
                if (untrustedBytes < 304 || untrustedBytes > PageBytes)
                    return new StatePageResult(StatePageStatus.BadFormat);
                int recordBytes = (int)untrustedBytes, tagOffset = recordBytes - 32;
                int length = page[104];
                if (length < 1 || length > 64)
                    return new StatePageResult(StatePageStatus.BadFormat);
                for (int i = 105; i < 105 + length; i++)
                    if (page[i] < 33 || page[i] > 126)
                        return new StatePageResult(StatePageStatus.BadFormat);
                string id = Encoding.ASCII.GetString(page, 105, length);
                using (var key = AcquireRequested(id))
                {
                    if (key == null)
                        return new StatePageResult(StatePageStatus.MissingKey);
                    mac = key.CopyAuthenticationKey();
                    using (var h = new HMACSHA256(mac))
                    {
                        h.TransformBlock(Domain, 0, Domain.Length, null, 0);
                        h.TransformFinalBlock(page, 0, tagOffset);
                        tag = h.Hash;
                    }
                    if (!Wire.Equal(tag, 0, page, tagOffset, 32))
                        return new StatePageResult(StatePageStatus.BadMac);
                    if (!Wire.Equal(page, 0, Magic, 0, 8))
                        return new StatePageResult(StatePageStatus.BadFormat);
                    int version = Wire.U16(page, 8);
                    if ((version < 1 || version > CurrentVersion) || page[169] != 1)
                        return new StatePageResult(StatePageStatus.AuthenticatedUnknown, keyId: id);
                    if (Wire.U16(page, 10) != 256 || Wire.U64(page, 16) == 0 || Wire.U32(page, 200) != 256 || Wire.I64(page, 204) < 0 || Wire.U32(page, 212) > 1)
                        throw new ArgumentException("Invalid page header.");
                    uint encodedPlain = Wire.U32(page, 192), encodedCipher = Wire.U32(page, 196);
                    if (version < 3)
                    {
                        if (recordBytes != PageBytes || encodedPlain != PlainBytes || encodedCipher != CipherBytes)
                            throw new ArgumentException("Invalid legacy page length.");
                    }
                    else if (encodedPlain < 256 || encodedPlain > PlainBytes || encodedCipher != 16 * (encodedPlain / 16 + 1) || recordBytes != 288 + encodedCipher)
                        throw new ArgumentException("Invalid compact page length.");
                    int plainLength = (int)encodedPlain, cipherLength = (int)encodedCipher;
                    Zero(page, 105 + length, 64 - length);
                    Zero(page, 170, 6);
                    Zero(page, 216, 40);
                    if (version < 3)
                        Zero(page, 98576, 32464);
                    if (!Wire.Equal(page, 24, directory, 0, 16) || !Wire.Equal(page, 40, installationHash, 0, 32) || !Wire.Equal(page, 72, destinationHash, 0, 32))
                        return new StatePageResult(StatePageStatus.BindingMismatch, keyId: id);
                    enc = key.CopyEncryptionKey();
                    iv = Wire.Slice(page, 176, 16);
                    beforeDecrypt?.Invoke();
                    plain = new byte[PlainBytes];
                    using (var aes = Aes.Create())
                    {
                        aes.KeySize = 256;
                        aes.Mode = CipherMode.CBC;
                        aes.Padding = PaddingMode.PKCS7;
                        aes.Key = enc;
                        aes.IV = iv;
                        using (var transform = aes.CreateDecryptor())
                        {
                            // CBC 可能保留最后一个块以检查 padding；只让 FinalBlock 分配有界尾部。
                            int prefix = cipherLength - 16;
                            int written = transform.TransformBlock(page, 256, prefix, plain, 0);
                            finalPlain = transform.TransformFinalBlock(page, 256 + prefix, 16);
                            if (finalPlain.Length > 32 || written + finalPlain.Length != plainLength)
                                throw new ArgumentException("Invalid plaintext length.");
                            Buffer.BlockCopy(finalPlain, 0, plain, written, finalPlain.Length);
                        }
                    }
                    if (Wire.U16(plain, 8) < 1 || Wire.U16(plain, 8) > CurrentVersion)
                        return new StatePageResult(StatePageStatus.AuthenticatedUnknown, keyId: id);
                    if (Wire.U16(plain, 8) != version)
                        throw new ArgumentException("Page/payload version mismatch.");
                    var state = DecodePayload(plain);
                    ValidateStateBindings(state);
                    bool reset = Wire.U32(page, 212) == 1;
                    if (state.CleanupGeneration != Wire.I64(page, 204) || (reset && (Wire.U64(page, 16) != 1 || !state.IsReset || (version == 1 && !state.UnknownUsage))))
                        throw new ArgumentException("Invalid clear reset.");
                    return new StatePageResult(StatePageStatus.Supported, state, Wire.U64(page, 16), state.CleanupGeneration, reset, id, version, recordBytes);
                }
            }
            catch (ArgumentException)
            {
                return new StatePageResult(StatePageStatus.BadFormat);
            }
            catch (OverflowException)
            {
                return new StatePageResult(StatePageStatus.BadFormat);
            }
            catch (CryptographicException)
            {
                return new StatePageResult(StatePageStatus.BadFormat);
            }
            finally
            {
                Wire.Clear(enc);
                Wire.Clear(mac);
                Wire.Clear(tag);
                Wire.Clear(finalPlain);
                Wire.Clear(iv);
                Wire.Clear(plain);
            }
        }
        private void ValidateStateBindings(TransferState state)
        {
            for (int i = 0; i < 4; i++)
            {
                var t = state.Slot(i);
                if (t != null && (t.Request.InstallationId != installationId || t.Request.DestinationId != destinationId))
                    throw new ArgumentException("Task identity differs from state page binding.");
            }
        }
        internal static void Zero(byte[] bytes, int offset, int count)
        {
            for (int i = offset; i < offset + count; i++)
                if (bytes[i] != 0)
                    throw new ArgumentException("Nonzero reserved bytes.");
        }
        internal static string ReadId(byte[] b, int offset)
        {
            int n = b[offset];
            if (n < 1 || n > 128)
                throw new ArgumentException("Invalid ID length.");
            for (int i = offset + 1; i < offset + 1 + n; i++)
                if (b[i] < 32 || b[i] > 126)
                    throw new ArgumentException("Invalid ASCII ID.");
            Zero(b, offset + 1 + n, 128 - n);
            return TransferValidation.Id(Encoding.ASCII.GetString(b, offset + 1, n), "id");
        }
        private static void PutId(byte[] b, int offset, string id)
        {
            b[offset] = (byte)id.Length;
            Encoding.ASCII.GetBytes(id, 0, id.Length, b, offset + 1);
        }
    }
}
