using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Logos.Storage
{
    // 文件头把目录身份、日志日、当日配额和密钥标识绑定到同一份认证数据。恢复旧文件时从这里取回原策略。
    public static class FileHeaderCodec
    {
        internal static readonly byte[] Domain = Encoding.ASCII.GetBytes(
            "Logos.Storage/File/v1\0"
        );
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("LOGOSF01");

        public static VerifiedFileHeader Create(
            long day,
            long f,
            long e,
            long policy,
            byte[] directoryId,
            KeyMaterial key
        )
        {
            if (
                day < 0
                || day > DateTime.MaxValue.Ticks / TimeSpan.TicksPerDay
                || policy < 0
                || f <= 0
                || e >= f
                || f - e < 896
                || e < 384
                || directoryId == null
                || directoryId.Length != 16
                || key == null
            )
                throw new ArgumentException("Invalid file policy.");
            byte[] b = new byte[512];
            Buffer.BlockCopy(Magic, 0, b, 0, 8);
            Wire.Put16(b, 8, 1);
            Wire.Put16(b, 10, 512);
            Wire.Put32(b, 12, 1);
            Wire.Put64(b, 16, day);
            Wire.Put64(b, 24, f);
            Wire.Put64(b, 32, e);
            Wire.Put64(b, 40, policy);
            Buffer.BlockCopy(directoryId, 0, b, 48, 16);
            b[64] = (byte)key.KeyId.Length;
            Encoding.ASCII.GetBytes(key.KeyId, 0, key.KeyId.Length, b, 65);
            b[129] = 1;
            byte[] mac = key.CopyAuthenticationKey();
            try
            {
                var tag = Wire.Mac(mac, Domain, Wire.Slice(b, 0, 480));
                Buffer.BlockCopy(tag, 0, b, 480, 32);
            }
            finally
            {
                Array.Clear(mac, 0, mac.Length);
            }
            return new VerifiedFileHeader(b);
        }

        public static VerifiedFileHeader Verify(byte[] bytes, KeyMaterial key) => Verify(bytes, key, null);

        // 首次解析时 parsed 为空；复验入口由 VerifiedFileHeader 自身提供其私有的原始字节。
        internal static VerifiedFileHeader Verify(byte[] bytes, KeyMaterial key, VerifiedFileHeader parsed)
        {
            if (bytes == null || bytes.Length != 512 || key == null)
                throw new StorageFormatException();
            byte[] mac = key.CopyAuthenticationKey();
            try
            {
                if (
                    !Wire.Equal(
                        Wire.Mac(mac, Domain, Wire.Slice(bytes, 0, 480)),
                        Wire.Slice(bytes, 480, 32)
                    )
                )
                    throw new StorageAuthenticationException();
            }
            finally
            {
                Array.Clear(mac, 0, mac.Length);
            }
            if (
                !Wire.Equal(Wire.Slice(bytes, 0, 8), Magic)
                || Wire.U16(bytes, 8) != 1
                || Wire.U16(bytes, 10) != 512
                || Wire.U32(bytes, 12) != 1
                || bytes[129] != 1
                || bytes[64] < 1
                || bytes[64] > 64
            )
                throw new StorageFormatException();
            for (int i = 65; i < 65 + bytes[64]; i++)
                if (bytes[i] < 0x21 || bytes[i] > 0x7e)
                    throw new StorageFormatException();
            for (int i = 65 + bytes[64]; i < 129; i++)
                if (bytes[i] != 0)
                    throw new StorageFormatException();
            for (int i = 130; i < 480; i++)
                if (bytes[i] != 0)
                    throw new StorageFormatException();
            var h = parsed ?? new VerifiedFileHeader(bytes);
            if (
                h.KeyId != key.KeyId
                || h.DayId < 0
                || h.DayId > DateTime.MaxValue.Ticks / TimeSpan.TicksPerDay
                || h.PolicyVersion < 0
                || h.MaxDailyFileBytes <= 0
                || h.ErrorFatalReservedBytesPerDay < 384
                || h.ErrorFatalReservedBytesPerDay >= h.MaxDailyFileBytes
                || h.MaxDailyFileBytes - h.ErrorFatalReservedBytesPerDay < 896
            )
                throw new StorageFormatException();
            return h;
        }
    }
}
