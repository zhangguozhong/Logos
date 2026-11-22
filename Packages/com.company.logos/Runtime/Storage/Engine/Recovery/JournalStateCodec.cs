using System;
using System.Text;

namespace Logos.Storage
{
    // 日副本和全局副本使用各自的格式标记及认证域，解码还要检查版本、保留位和策略值，避免将未知布局解释成旧状态。
    internal static class JournalStateCodec
    {
        private static readonly byte[] GlobalMagic = Encoding.ASCII.GetBytes("LOGOSS01");
        private static readonly byte[] DayMagic = Encoding.ASCII.GetBytes("LOGOSD01");
        private static readonly byte[] GlobalDomain = Encoding.ASCII.GetBytes(
            "Logos.Storage/State/v1\0"
        );
        private static readonly byte[] DayDomain = Encoding.ASCII.GetBytes(
            "Logos.Storage/Day/v1\0"
        );

        internal static void EncodeGlobal(JournalGlobalState s, KeyMaterial key, byte[] b)
        {
            Begin(b, GlobalMagic, s.Sequence);
            Wire.Put32(b, 12, 1);
            Buffer.BlockCopy(s.DirectoryId, 0, b, 24, 16);
            Wire.Put64(b, 40, s.HighestDay);
            Wire.Put64(b, 48, s.CleanupGeneration);
            Wire.Put64(b, 56, s.FileLimit);
            Wire.Put64(b, 64, s.ErrorLimit);
            Wire.Put64(b, 72, s.PolicyVersion);
            JournalIo.WriteKeyId(b, 80, 81, key.KeyId);
            b[145] = s.ClearPhase;
            b[146] = s.TimeConfirmed ? (byte)1 : (byte)0;
            b[147] = s.ClearScope;
            Wire.Put32(b, 148, (uint)s.PayloadLength);
            Wire.Put64(b, 152, s.PayloadGeneration);
            Buffer.BlockCopy(s.PayloadHash, 0, b, 160, 32);
            Wire.Put32(b, 192, (uint)s.TargetCount);
            Sign(b, key, GlobalDomain);
        }

        internal static JournalGlobalState DecodeGlobal(byte[] b, KeyMaterial key)
        {
            if (!Authenticate(b, key, GlobalDomain))
                return null;
            CheckPrefix(b, GlobalMagic);
            if (Wire.U32(b, 12) != 1)
                throw new JournalBlockedException(JournalBlockReason.UnsupportedState);
            ushort version = Wire.U16(b, 8);
            if (version == 1)
                CheckZeros(b, 145, 480);
            else
            {
                if (b[146] > 1 || b[145] > 2 || b[147] > 1)
                    throw new JournalBlockedException(JournalBlockReason.UnsupportedState);
                CheckZeros(b, 196, 480);
                if (b[145] != 1)
                {
                    CheckZeros(b, 147, 196);
                }
                else if (
                    Wire.U32(b, 192) > 1024
                    || Wire.U32(b, 148) != 64 + Wire.U32(b, 192) * 8
                    || Wire.I64(b, 152) != Wire.I64(b, 48)
                )
                    throw new JournalBlockedException(JournalBlockReason.StateInvalid);
            }

            var s = new JournalGlobalState
            {
                SchemaVersion = version,
                TimeConfirmed = version == 1 || b[146] == 1,
                ClearPhase = version == 2 ? b[145] : (byte)0,
                ClearScope = version == 2 ? b[147] : (byte)0,
                PayloadLength = version == 2 ? (int)Wire.U32(b, 148) : 0,
                PayloadGeneration = version == 2 ? Wire.I64(b, 152) : 0,
                PayloadHash = version == 2 ? Wire.Slice(b, 160, 32) : new byte[32],
                TargetCount = version == 2 ? (int)Wire.U32(b, 192) : 0,
                Sequence = Wire.U64(b, 16),
                DirectoryId = Wire.Slice(b, 24, 16),
                HighestDay = Wire.I64(b, 40),
                CleanupGeneration = Wire.I64(b, 48),
                FileLimit = Wire.I64(b, 56),
                ErrorLimit = Wire.I64(b, 64),
                PolicyVersion = Wire.I64(b, 72),
                ControlKeyId = JournalIo.ReadKeyId(b, 80, 81),
            };
            CheckKeyPadding(b, 80, 81);
            CheckPolicy(
                s.HighestDay,
                s.FileLimit,
                s.ErrorLimit,
                s.PolicyVersion,
                s.CleanupGeneration
            );
            return s;
        }

        internal static void EncodeDay(JournalDayState s, KeyMaterial key, byte[] b)
        {
            Begin(b, DayMagic, s.Sequence);
            Wire.Put32(
                b,
                12,
                (uint)((s.Exists ? 1 : 0) | (s.Tombstone ? 2 : 0) | (s.Clean ? 4 : 0))
            );
            Wire.Put64(b, 24, s.DayId);
            Wire.Put64(b, 32, s.FileLimit);
            Wire.Put64(b, 40, s.ErrorLimit);
            Wire.Put64(b, 48, s.PolicyVersion);
            Wire.Put64(b, 56, s.CleanupGeneration);
            Wire.Put64(b, 64, s.CommittedEnd);
            Wire.Put64(b, 72, s.NormalBytes);
            Wire.Put64(b, 80, s.ErrorBytes);
            Buffer.BlockCopy(s.Epoch, 0, b, 88, 16);
            JournalIo.WriteKeyId(b, 104, 105, s.DataKeyId);
            JournalIo.WriteKeyId(b, 169, 170, key.KeyId);
            if (s.Intent != null)
            {
                var i = s.Intent;
                b[234] = 1;
                b[235] = (byte)(i.IsErrorClass ? 1 : 0);
                Wire.Put64(b, 240, i.CleanupGeneration);
                Wire.Put64(b, 248, i.DayId);
                Wire.Put64(b, 256, i.Start);
                Wire.Put64(b, 264, i.ExpectedEnd);
                Wire.Put64(b, 272, i.PreviousCommittedEnd);
                Buffer.BlockCopy(i.Epoch, 0, b, 280, 16);
                Wire.PutU64(b, 296, i.BlockSequence);
                Buffer.BlockCopy(i.WireHash, 0, b, 304, 32);
            }

            Buffer.BlockCopy(s.HeaderHash, 0, b, 336, 32);
            Buffer.BlockCopy(s.DirectoryId, 0, b, 368, 16);
            b[384] = s.DeletionKind;
            b[385] = s.DeletionPhase;
            Wire.Put64(b, 392, s.ExpiryConfirmedDay);
            Wire.Put64(b, 400, s.DeletionGeneration);
            Sign(b, key, DayDomain);
        }

        internal static JournalDayState DecodeDay(byte[] b, KeyMaterial key)
        {
            if (!Authenticate(b, key, DayDomain))
                return null;
            CheckPrefix(b, DayMagic);
            uint flags = Wire.U32(b, 12);
            if (flags > 7 || b[234] > 1 || b[235] > 1)
                throw new JournalBlockedException(JournalBlockReason.UnsupportedState);
            CheckZeros(b, 236, 240);
            ushort version = Wire.U16(b, 8);
            if (version == 1)
                CheckZeros(b, 384, 480);
            else
            {
                CheckZeros(b, 386, 392);
                CheckZeros(b, 408, 480);
                if (
                    b[384] > 2
                    || b[385] > 2
                    || ((b[384] == 0) != (b[385] == 0))
                    || (b[384] == 2 && Wire.I64(b, 400) != Wire.I64(b, 56))
                    || (b[384] == 1 && Wire.I64(b, 392) < Wire.I64(b, 24))
                )
                    throw new JournalBlockedException(JournalBlockReason.StateInvalid);
            }

            CheckKeyPadding(b, 104, 105);
            CheckKeyPadding(b, 169, 170);
            var s = new JournalDayState
            {
                SchemaVersion = version,
                DeletionKind = version == 2 ? b[384] : (byte)0,
                DeletionPhase = version == 2 ? b[385] : (byte)0,
                ExpiryConfirmedDay = version == 2 ? Wire.I64(b, 392) : 0,
                DeletionGeneration = version == 2 ? Wire.I64(b, 400) : 0,
                Sequence = Wire.U64(b, 16),
                DayId = Wire.I64(b, 24),
                FileLimit = Wire.I64(b, 32),
                ErrorLimit = Wire.I64(b, 40),
                PolicyVersion = Wire.I64(b, 48),
                CleanupGeneration = Wire.I64(b, 56),
                CommittedEnd = Wire.I64(b, 64),
                NormalBytes = Wire.I64(b, 72),
                ErrorBytes = Wire.I64(b, 80),
                Exists = (flags & 1) != 0,
                Tombstone = (flags & 2) != 0,
                Clean = (flags & 4) != 0,
                Epoch = Wire.Slice(b, 88, 16),
                DataKeyId = JournalIo.ReadKeyId(b, 104, 105, true),
                ControlKeyId = JournalIo.ReadKeyId(b, 169, 170),
                HeaderHash = Wire.Slice(b, 336, 32),
                DirectoryId = Wire.Slice(b, 368, 16),
            };
            CheckPolicy(s.DayId, s.FileLimit, s.ErrorLimit, s.PolicyVersion, s.CleanupGeneration);
            if (
                s.CommittedEnd < 0
                || s.NormalBytes < 0
                || s.ErrorBytes < 0
                || s.NormalBytes > long.MaxValue - s.ErrorBytes
                || s.NormalBytes + s.ErrorBytes != s.CommittedEnd
                || (s.Exists && (s.DataKeyId.Length == 0 || s.CommittedEnd < 512))
            )
                throw new JournalBlockedException(JournalBlockReason.StateInvalid);
            if (b[234] == 1)
            {
                s.Intent = new AppendIntent
                {
                    CleanupGeneration = Wire.I64(b, 240),
                    DayId = Wire.I64(b, 248),
                    Start = Wire.I64(b, 256),
                    ExpectedEnd = Wire.I64(b, 264),
                    PreviousCommittedEnd = Wire.I64(b, 272),
                    Epoch = Wire.Slice(b, 280, 16),
                    BlockSequence = Wire.U64(b, 296),
                    WireHash = Wire.Slice(b, 304, 32),
                    IsErrorClass = b[235] == 1,
                };
                var i = s.Intent;
                if (
                    i.CleanupGeneration != s.CleanupGeneration
                    || i.DayId != s.DayId
                    || i.Start < 512
                    || i.PreviousCommittedEnd != i.Start
                    || i.ExpectedEnd <= i.Start
                    || i.ExpectedEnd - i.Start > 65856
                    || i.ExpectedEnd - i.Start < 320
                    || s.CommittedEnd > i.Start
                )
                    throw new JournalBlockedException(JournalBlockReason.StateInvalid);
            }
            else
            {
                CheckZeros(b, 235, 336);
            }

            return s;
        }

        private static void CheckPolicy(
            long day,
            long f,
            long e,
            long policy,
            long cleanupGeneration
        )
        {
            if (
                day < 0
                || day > DateTime.MaxValue.Ticks / TimeSpan.TicksPerDay
                || f < 0
                || e < 384
                || e >= f
                || f - e < 896
                || policy != 1
                || cleanupGeneration < 0
            )
                throw new JournalBlockedException(JournalBlockReason.PolicyMismatch);
        }

        private static void Begin(byte[] b, byte[] magic, ulong sequence)
        {
            Array.Clear(b, 0, 512);
            Buffer.BlockCopy(magic, 0, b, 0, 8);
            Wire.Put16(b, 8, 2);
            Wire.Put16(b, 10, 512);
            Wire.PutU64(b, 16, sequence);
        }

        private static void CheckPrefix(byte[] b, byte[] magic)
        {
            if (
                !Wire.Equal(b, 0, magic, 0, 8)
                || (Wire.U16(b, 8) != 1 && Wire.U16(b, 8) != 2)
                || Wire.U16(b, 10) != 512
            )
                throw new JournalBlockedException(JournalBlockReason.UnsupportedState);
        }

        private static void CheckKeyPadding(byte[] b, int length, int value)
        {
            CheckZeros(b, value + b[length], value + 64);
        }

        private static void CheckZeros(byte[] b, int start, int end)
        {
            for (int i = start; i < end; i++)
                if (b[i] != 0)
                    throw new JournalBlockedException(JournalBlockReason.UnsupportedState);
        }

        private static void Sign(byte[] b, KeyMaterial key, byte[] domain)
        {
            byte[] mac = key.CopyAuthenticationKey();
            try
            {
                Wire.MacInto(
                    mac,
                    b,
                    480,
                    new ArraySegment<byte>(domain),
                    new ArraySegment<byte>(b, 0, 480)
                );
            }
            finally
            {
                Wire.Clear(mac);
            }
        }

        private static bool Authenticate(byte[] b, KeyMaterial key, byte[] domain)
        {
            byte[] mac = key.CopyAuthenticationKey();
            byte[] tag = new byte[32];
            try
            {
                Wire.MacInto(
                    mac,
                    tag,
                    0,
                    new ArraySegment<byte>(domain),
                    new ArraySegment<byte>(b, 0, 480)
                );
                return Wire.Equal(tag, 0, b, 480, 32);
            }
            finally
            {
                Wire.Clear(mac);
                Wire.Clear(tag);
            }
        }
    }
}
