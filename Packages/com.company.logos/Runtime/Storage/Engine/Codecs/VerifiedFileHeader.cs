using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Logos.Storage
{
    // 私有原始字节用于再次验签；外部需要完整头时取得副本，避免修改原始字节后继续沿用缓存的解析结果。
    public sealed class VerifiedFileHeader
    {
        private readonly byte[] bytes;
        public long DayId
        {
            get;
        }
        public long MaxDailyFileBytes
        {
            get;
        }
        public long ErrorFatalReservedBytesPerDay
        {
            get;
        }
        public long PolicyVersion
        {
            get;
        }
        public string KeyId
        {
            get;
        }
        internal byte[] Hash
        {
            get;
        }
        internal byte[] DirectoryId
        {
            get;
        }

        internal VerifiedFileHeader(byte[] b)
        {
            bytes = (byte[])b.Clone();
            DayId = Wire.I64(b, 16);
            MaxDailyFileBytes = Wire.I64(b, 24);
            ErrorFatalReservedBytesPerDay = Wire.I64(b, 32);
            PolicyVersion = Wire.I64(b, 40);
            KeyId = Encoding.ASCII.GetString(b, 65, b[64]);
            DirectoryId = Wire.Slice(b, 48, 16);
            using (var sha = SHA256.Create())
                Hash = sha.ComputeHash(bytes);
        }

        // 自有字节从未交给调用者；重新认证原字节，沿用它们已解析的字段。
        internal void Reverify(KeyMaterial key) => FileHeaderCodec.Verify(bytes, key, this);

        public byte[] CopyBytes() => (byte[])bytes.Clone();
    }
}
