using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Logos.Storage
{
    /// <summary>固定数值原因乘六个事件等级。C 随后显式映射 Core 累计增量。</summary>
    public sealed class DropStatisticsCheckpoint
    {
        public const int ReasonCount = 32;
        public const int LevelCount = 6;
        internal const int CopyBytes = 2048;
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("LOGOSST2");
        private static readonly byte[] Domain = Encoding.ASCII.GetBytes(
            "Logos.Storage/Stats/v2\0"
        );
        private readonly long[] counters = new long[ReasonCount * LevelCount];
        private readonly byte[] scratch = new byte[CopyBytes];
        private readonly byte[] verification = new byte[CopyBytes];
        public int FixedBufferPayloadBytes =>
            counters.Length * sizeof(long) + scratch.Length + verification.Length;
        private readonly Action checkThread;
        private ulong sequence;
        private bool dirty,
            needsResync;
        private long nextAttempt;

        internal DropStatisticsCheckpoint(Action checkThread)
        {
            this.checkThread = checkThread;
        }

        public void Record(int reasonId, LogLevel level, long count = 1)
        {
            checkThread();
            int l = (int)level;
            if (reasonId < 0 || reasonId >= ReasonCount || l < 0 || l >= LevelCount || count < 0)
                throw new ArgumentOutOfRangeException();
            if (count == 0)
                return;
            int i = reasonId * LevelCount + l;
            counters[i] = count > long.MaxValue - counters[i] ? long.MaxValue : counters[i] + count;
            dirty = true;
        }

        public long[] Snapshot()
        {
            checkThread();
            return (long[])counters.Clone();
        }

        internal void Load(JournalStateStore store, IStorageKeyProvider keys)
        {
            checkThread();
            try
            {
                for (int copy = 0; copy < 2; copy++)
                {
                    store.ReadReserved(1024 + copy * CopyBytes, scratch);
                    string id;
                    if (!Wire.Equal(scratch, 0, Magic, 0, 8))
                        continue;
                    try
                    {
                        id = JournalIo.ReadKeyId(scratch, 24, 25);
                    }
                    catch (StorageFormatException)
                    {
                        continue;
                    }

                    using (var key = JournalIo.Acquire(keys, id))
                    {
                        if (!Verify(key))
                            continue;
                        ulong seq = Wire.U64(scratch, 16);
                        if (seq > sequence)
                            sequence = seq;
                        for (int i = 0; i < counters.Length; i++)
                        {
                            ulong value = Wire.U64(scratch, 128 + i * 8);
                            if (value <= long.MaxValue)
                                counters[i] = Math.Max(counters[i], (long)value);
                        }
                    }
                }

                needsResync = false;
            }
            finally
            {
                Wire.Clear(scratch);
            }
        }

        private bool Verify(KeyMaterial key)
        {
            if (
                Wire.U16(scratch, 8) != 2
                || Wire.U16(scratch, 10) != CopyBytes
                || Wire.U32(scratch, 12) != 0
            )
                return false;
            for (int i = 25 + scratch[24]; i < 128; i++)
                if (scratch[i] != 0)
                    return false;
            for (int i = 1664; i < 2016; i++)
                if (scratch[i] != 0)
                    return false;
            var mac = key.CopyAuthenticationKey();
            var tag = new byte[32];
            try
            {
                Wire.MacInto(
                    mac,
                    tag,
                    0,
                    new ArraySegment<byte>(Domain),
                    new ArraySegment<byte>(scratch, 0, 2016)
                );
                return Wire.Equal(tag, 0, scratch, 2016, 32);
            }
            finally
            {
                Wire.Clear(mac);
                Wire.Clear(tag);
            }
        }

        internal void Step(
            JournalStateStore store,
            IStorageKeyProvider keys,
            KeyMaterial key,
            JournalStepContext context,
            long timestamp
        )
        {
            checkThread();
            if (!dirty || timestamp < nextAttempt || context.ShouldYield)
                return;
            nextAttempt =
                timestamp > long.MaxValue - 30 * Stopwatch.Frequency
                    ? long.MaxValue
                    : timestamp + 30 * Stopwatch.Frequency;
            try
            {
                if (needsResync)
                    Load(store, keys);
                WriteCopy(store, key);
                dirty = false;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                needsResync = true;
            }
        }

        // 统计计数存入独立的认证副本。它们描述丢弃情况，不参与恢复数据文件的已提交字节下界。
        private void WriteCopy(JournalStateStore store, KeyMaterial key)
        {
            if (sequence == ulong.MaxValue)
                throw new JournalBlockedException(JournalBlockReason.MetadataLimit);
            sequence++;
            Array.Clear(scratch, 0, scratch.Length);
            Buffer.BlockCopy(Magic, 0, scratch, 0, 8);
            Wire.Put16(scratch, 8, 2);
            Wire.Put16(scratch, 10, CopyBytes);
            Wire.PutU64(scratch, 16, sequence);
            JournalIo.WriteKeyId(scratch, 24, 25, key.KeyId);
            for (int i = 0; i < counters.Length; i++)
                Wire.Put64(scratch, 128 + i * 8, counters[i]);
            byte[] mac = key.CopyAuthenticationKey();
            try
            {
                Wire.MacInto(
                    mac,
                    scratch,
                    2016,
                    new ArraySegment<byte>(Domain),
                    new ArraySegment<byte>(scratch, 0, 2016)
                );
                store.WriteReservedVerified(
                    1024 + (int)(sequence & 1) * CopyBytes,
                    scratch,
                    verification
                );
            }
            finally
            {
                Wire.Clear(mac);
                Wire.Clear(scratch);
                Wire.Clear(verification);
            }
        }

        internal void RotateBoth(JournalStateStore store, IStorageKeyProvider keys, KeyMaterial key)
        {
            Load(store, keys);
            WriteCopy(store, key);
            WriteCopy(store, key);
            dirty = false;
        }

        internal bool VerifyControlKeyBoth(
            JournalStateStore store,
            IStorageKeyProvider keys,
            string keyId
        )
        {
            try
            {
                for (int copy = 0; copy < 2; copy++)
                {
                    store.ReadReserved(1024 + copy * CopyBytes, scratch);
                    string id = JournalIo.ReadKeyId(scratch, 24, 25);
                    using (var key = JournalIo.Acquire(keys, id))
                        if (id != keyId || !Verify(key))
                            return false;
                }

                return true;
            }
            finally
            {
                Wire.Clear(scratch);
            }
        }

        internal void ClearBuffers()
        {
            Wire.Clear(scratch);
            Wire.Clear(verification);
            Array.Clear(counters, 0, counters.Length);
        }
    }
}
