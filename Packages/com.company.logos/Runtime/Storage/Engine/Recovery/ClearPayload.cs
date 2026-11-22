using System;
using System.Security.Cryptography;
using System.Text;

namespace Logos.Storage
{
    // 清理日期集合写入有界载荷区，全局状态保存其长度与摘要；恢复时必须从已发布的意图取回同一组目标。
    internal static class ClearPayload
    {
        internal const int Offset = 8192;
        internal const int Capacity = 16384;
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("LOGOSCL2");

        internal static byte[] Encode(JournalGlobalState global, byte scope, long[] days, int count)
        {
            var b = new byte[64 + count * 8];
            Buffer.BlockCopy(Magic, 0, b, 0, 8);
            Wire.Put16(b, 8, 2);
            Wire.Put16(b, 10, 64);
            Wire.Put32(b, 12, (uint)b.Length);
            Wire.Put64(b, 16, global.CleanupGeneration);
            Buffer.BlockCopy(global.DirectoryId, 0, b, 24, 16);
            b[40] = scope;
            Wire.Put32(b, 44, (uint)count);
            for (int i = 0; i < count; i++)
                Wire.Put64(b, 64 + i * 8, days[i]);
            return b;
        }

        internal static byte[] Hash(byte[] b)
        {
            using (var sha = SHA256.Create())
                return sha.ComputeHash(b);
        }

        internal static long[] Decode(byte[] b, JournalGlobalState g)
        {
            byte[] hash = Hash(b);
            try
            {
                if (
                    b.Length != g.PayloadLength
                    || b.Length != 64 + 8 * g.TargetCount
                    || !Wire.Equal(hash, g.PayloadHash)
                    || !Wire.Equal(b, 0, Magic, 0, 8)
                    || Wire.U16(b, 8) != 2
                    || Wire.U16(b, 10) != 64
                    || Wire.U32(b, 12) != b.Length
                    || Wire.I64(b, 16) != g.CleanupGeneration
                    || !Wire.Equal(b, 24, g.DirectoryId, 0, 16)
                    || b[40] != g.ClearScope
                    || Wire.U32(b, 44) != g.TargetCount
                )
                    throw new JournalBlockedException(JournalBlockReason.StateInvalid);
                for (int i = 41; i < 64; i++)
                    if ((i < 44 || i >= 48) && b[i] != 0)
                        throw new JournalBlockedException(JournalBlockReason.UnsupportedState);
                var result = new long[g.TargetCount];
                for (int i = 0; i < result.Length; i++)
                {
                    result[i] = Wire.I64(b, 64 + i * 8);
                    if (
                        result[i] < 0
                        || result[i] > g.HighestDay
                        || (i > 0 && result[i] <= result[i - 1])
                    )
                        throw new JournalBlockedException(JournalBlockReason.StateInvalid);
                }

                return result;
            }
            finally
            {
                Wire.Clear(hash);
            }
        }
    }
}
