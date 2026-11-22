using System;
using System.IO;
using System.Text;

namespace Logos.Storage
{
    // 流读取可能返回短读，协议字段必须读满指定长度后再解释；密钥取得失败会转成可识别的阻塞原因。
    internal static class JournalIo
    {
        internal static bool IsClosed(Stream stream)
        {
            if (stream == null)
                return true;
            try
            {
                return !stream.CanRead && !stream.CanWrite && !stream.CanSeek;
            }
            catch
            {
                return false;
            }
        }

        internal static void ReadExactly(Stream stream, byte[] target, int offset, int count)
        {
            while (count > 0)
            {
                int n = stream.Read(target, offset, count);
                if (n == 0)
                    throw new EndOfStreamException();
                offset += n;
                count -= n;
            }
        }

        // 认证前，不可信的有界 ID 仅用于选择 provider 密钥。
        internal static string ReadKeyId(
            byte[] bytes,
            int lengthOffset,
            int valueOffset,
            bool allowEmpty = false
        )
        {
            int n = bytes[lengthOffset];
            if (n > 64 || (!allowEmpty && n == 0))
                throw new StorageFormatException();
            for (int i = 0; i < n; i++)
                if (bytes[valueOffset + i] < 0x21 || bytes[valueOffset + i] > 0x7e)
                    throw new StorageFormatException();
            return Encoding.ASCII.GetString(bytes, valueOffset, n);
        }

        internal static void WriteKeyId(
            byte[] bytes,
            int lengthOffset,
            int valueOffset,
            string keyId
        )
        {
            bytes[lengthOffset] = (byte)(keyId?.Length ?? 0);
            if (keyId != null)
                Encoding.ASCII.GetBytes(keyId, 0, keyId.Length, bytes, valueOffset);
        }

        internal static KeyMaterial Acquire(IStorageKeyProvider provider, string id)
        {
            KeyAcquireResult result;
            try
            {
                result = id == null ? provider.TryAcquireCurrent() : provider.TryAcquire(id);
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                throw new JournalBlockedException(JournalBlockReason.KeyUnavailable);
            }

            if (
                result == null
                || result.Status != KeyAcquireStatus.Acquired
                || result.Material == null
            )
                throw new JournalBlockedException(JournalBlockReason.KeyUnavailable);
            if (id != null && result.Material.KeyId != id)
            {
                result.Material.Dispose();
                throw new JournalBlockedException(JournalBlockReason.KeyUnavailable);
            }

            return result.Material;
        }
    }

    internal sealed class JournalBlockedException : Exception
    {
        internal JournalBlockReason Reason
        {
            get;
        }

        internal JournalBlockedException(JournalBlockReason reason)
        {
            Reason = reason;
        }
    }
}
