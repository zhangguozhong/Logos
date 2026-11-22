using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Logos.Storage
{
    // 文件头、块和状态副本共用这些小端整数与摘要操作，读取偏移和写入宽度必须与各自格式保持一致。
    internal static class Wire
    {
        internal static ushort U16(byte[] b, int o) => (ushort)(b[o] | b[o + 1] << 8);

        internal static uint U32(byte[] b, int o) =>
            (uint)(b[o] | b[o + 1] << 8 | b[o + 2] << 16 | b[o + 3] << 24);

        internal static ulong U64(byte[] b, int o) => U32(b, o) | ((ulong)U32(b, o + 4) << 32);

        internal static long I64(byte[] b, int o) => unchecked((long)U64(b, o));

        internal static void Put16(byte[] b, int o, ushort v)
        {
            b[o] = (byte)v;
            b[o + 1] = (byte)(v >> 8);
        }

        internal static void Put32(byte[] b, int o, uint v)
        {
            for (int i = 0; i < 4; i++)
                b[o + i] = (byte)(v >> (8 * i));
        }

        internal static void Put64(byte[] b, int o, long v) => PutU64(b, o, unchecked((ulong)v));

        internal static void PutU64(byte[] b, int o, ulong v)
        {
            for (int i = 0; i < 8; i++)
                b[o + i] = (byte)(v >> (8 * i));
        }

        internal static byte[] Slice(byte[] b, int o, int n)
        {
            byte[] r = new byte[n];
            Buffer.BlockCopy(b, o, r, 0, n);
            return r;
        }

        internal static byte[] Mac(byte[] key, params byte[][] parts)
        {
            using (var h = new HMACSHA256(key))
            {
                for (int i = 0; i < parts.Length; i++)
                    h.TransformBlock(parts[i], 0, parts[i].Length, null, 0);
                h.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return h.Hash;
            }
        }

        internal static void MacInto(
            byte[] key,
            byte[] output,
            int offset,
            params ArraySegment<byte>[] parts
        )
        {
            using (var h = new HMACSHA256(key))
            {
                foreach (var p in parts)
                    h.TransformBlock(p.Array, p.Offset, p.Count, null, 0);
                h.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                byte[] hash = h.Hash;
                try
                {
                    Buffer.BlockCopy(hash, 0, output, offset, 32);
                }
                finally
                {
                    Clear(hash);
                }
            }
        }

        internal static bool Equal(byte[] a, int ao, byte[] b, int bo, int length)
        {
            int diff = 0;
            for (int i = 0; i < length; i++)
                diff |= a[ao + i] ^ b[bo + i];
            return diff == 0;
        }

        internal static bool Equal(byte[] a, byte[] b)
        {
            if (a.Length != b.Length)
                return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++)
                diff |= a[i] ^ b[i];
            return diff == 0;
        }

        internal static void Clear(byte[] b)
        {
            if (b != null)
                Array.Clear(b, 0, b.Length);
        }
    }
}
