using System;
using System.Text;
using Logos.Storage;
namespace Logos.Transport
{
    internal sealed partial class TransferStateCodec
    {
        private static readonly byte[] PayloadMagic = Encoding.ASCII.GetBytes("LOGOSP01");
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static void PutContext(byte[] b, int at, string value)
        {
            if (value == null)
            {
                Wire.Put16(b, at, 65535);
                return;
            }
            int n = StrictUtf8.GetBytes(value, 0, value.Length, b, at + 2);
            Wire.Put16(b, at, (ushort)n);
        }
        private static string ReadContext(byte[] b, int at)
        {
            int n = Wire.U16(b, at);
            if (n == 65535)
            {
                Zero(b, at + 2, 384);
                return null;
            }
            if (n > 384)
                throw new ArgumentException("Context length.");
            Zero(b, at + 2 + n, 384 - n);
            string value = StrictUtf8.GetString(b, at + 2, n);
            TaskSlot.ValidateContext(value);
            return value;
        }
        // 书签坐标和头部摘要分区存放，读写两端必须使用同一组偏移。
        private static void PutBookmark(byte[] b, int at, int hashAt, ExportBookmark bookmark)
        {
            if (bookmark == null)
                return;
            Wire.Put64(b, at, bookmark.Day);
            Wire.Put64(b, at + 8, bookmark.CapturedEnd);
            Wire.Put64(b, at + 16, bookmark.BlockOffset);
            Wire.Put16(b, at + 24, (ushort)bookmark.NextRecordIndex);
            var hash = bookmark.CopyHeaderHash();
            try
            {
                Buffer.BlockCopy(hash, 0, b, hashAt, 32);
            }
            finally
            {
                Wire.Clear(hash);
            }
        }
        private static ExportBookmark ReadBookmark(byte[] b, int at, int hashAt, long generation)
        {
            return new ExportBookmark(Wire.I64(b, at), Wire.I64(b, at + 8), Wire.Slice(b, hashAt, 32), Wire.I64(b, at + 16), Wire.U16(b, at + 24), generation);
        }
        private static byte[] EncodePayload(TransferState s)
        {
            var b = new byte[PlainBytes];
            Buffer.BlockCopy(PayloadMagic, 0, b, 0, 8);
            Wire.Put16(b, 8, CurrentVersion);
            Wire.Put16(b, 10, 256);
            Wire.Put32(b, 12, PlainBytes);
            Wire.Put64(b, 16, s.NetworkDay);
            Wire.Put64(b, 24, s.OriginalU);
            Wire.Put64(b, 32, s.OriginalV);
            Wire.PutU64(b, 40, s.NormalUsed);
            Wire.PutU64(b, 48, s.ErrorUsed);
            Wire.Put64(b, 56, s.CleanupGeneration);
            Wire.Put64(b, 64, s.RevokedThrough);
            Wire.Put32(b, 72, (s.UnknownUsage ? 1u : 0u) | (s.Pending != null ? 2u : 0u));
            Wire.Put32(b, 76, unchecked((uint)(s.Pending?.Slot ?? -1)));
            Wire.PutU64(b, 80, s.NextSlotGeneration);
            if (s.AutoBookmark == null)
                Wire.Put64(b, 96, -1);
            else
                PutBookmark(b, 96, 128, s.AutoBookmark);
            Wire.PutU64(b, 160, s.AutoTaskCounter);
            Wire.Put64(b, 168, s.GlobalThrottleUtc);
            for (int i = 0; i < 4; i++)
                if (s.Slot(i) != null)
                    PutTask(b, 256 + 4096 * i, s.Slot(i));
            if (s.Pending != null)
                PutPending(b, s.Pending);
            return b;
        }
        private static TransferState DecodePayload(byte[] b)
        {
            if (!Wire.Equal(b, 0, PayloadMagic, 0, 8) || Wire.U16(b, 10) != 256 || Wire.U32(b, 12) != PlainBytes || Wire.U32(b, 72) > 3)
                throw new ArgumentException("Invalid state payload.");
            Zero(b, 88, 8);
            Zero(b, 122, 6);
            if (Wire.U16(b, 8) == 1)
                Zero(b, 168, 88);
            else
                Zero(b, 176, 80);
            Zero(b, 85088, 13216);
            long generation = Wire.I64(b, 56);
            ExportBookmark auto = null;
            if (Wire.I64(b, 96) == -1)
            {
                Zero(b, 104, 18);
                Zero(b, 128, 40);
            }
            else
                auto = ReadBookmark(b, 96, 128, generation);
            var tasks = new TaskSlot[4];
            for (int i = 0; i < 4; i++)
                tasks[i] = ReadTask(b, 256 + 4096 * i);
            bool hasPending = (Wire.U32(b, 72) & 2) != 0;
            int slot = unchecked((int)Wire.U32(b, 76));
            PendingBatch pending = null;
            if (!hasPending)
            {
                if (slot != -1)
                    throw new ArgumentException("Unexpected pending slot.");
                Zero(b, 16640, 1024 + 67424);
            }
            else
            {
                pending = ReadPending(b);
                if (slot != pending.Slot)
                    throw new ArgumentException("Pending slot mismatch.");
            }
            return new TransferState(Wire.I64(b, 16), Wire.I64(b, 24), Wire.I64(b, 32), Wire.U64(b, 40), Wire.U64(b, 48), generation, Wire.I64(b, 64), (Wire.U32(b, 72) & 1) != 0, Wire.U64(b, 80), tasks, pending, auto, Wire.U64(b, 160), Wire.U16(b, 8) == 1 ? 0 : Wire.I64(b, 168));
        }
        private static void PutTask(byte[] b, int o, TaskSlot t)
        {
            var r = t.Request;
            var s = r.Scope;
            b[o] = (byte)t.State;
            b[o + 1] = (byte)(t.AutoError ? 1 : 0);
            b[o + 2] = s.LevelMask;
            b[o + 3] = t.IdentityCoverageGap ? (byte)1 : (byte)0;
            Wire.Put32(b, o + 4, t.Bookmark == null ? 0u : 1u);
            Wire.PutU64(b, o + 8, t.Generation);
            Wire.Put64(b, o + 16, t.Revision);
            Wire.Put64(b, o + 24, t.CleanupGeneration);
            Wire.Put64(b, o + 32, r.FirstQuotaDay);
            Wire.Put32(b, o + 40, (uint)r.QuotaDays);
            Wire.Put32(b, o + 44, (uint)t.MaxRecords);
            Wire.Put64(b, o + 48, s.StartUtc.Ticks);
            Wire.Put64(b, o + 56, s.EndUtc.Ticks);
            Wire.Put64(b, o + 64, s.StartSequence ?? -1);
            Wire.Put64(b, o + 72, s.EndSequence ?? -1);
            Wire.Put64(b, o + 80, s.IdentityVersion ?? -1);
            Wire.Put64(b, o + 88, r.ExpiresUtc.Ticks);
            Wire.Put64(b, o + 96, r.Limits.SourceBytes);
            Wire.Put64(b, o + 104, r.Limits.EncodedBytes);
            Wire.Put32(b, o + 112, (uint)r.Limits.Batches);
            Wire.Put32(b, o + 116, (uint)t.GeneratedBatches);
            Wire.Put64(b, o + 120, t.SourceUsed);
            Wire.Put64(b, o + 128, t.EncodedUsed);
            PutBookmark(b, o + 136, o + 176, t.Bookmark);
            Wire.Put16(b, o + 162, (ushort)t.Attempts);
            b[o + 164] = (byte)t.Splits;
            b[o + 165] = (byte)t.Reason;
            Wire.Put64(b, o + 168, t.RetryUtc);
            PutId(b, o + 208, r.TaskId);
            PutId(b, o + 337, r.InstallationId);
            PutId(b, o + 466, r.DestinationId);
            PutContext(b, o + 595, s.AppSessionId);
            PutContext(b, o + 981, s.UnityInstanceId);
            PutContext(b, o + 1367, s.UnityVisitId);
            PutContext(b, o + 1753, s.DiagnosticUserId);
        }
        private static long? Nullable(byte[] b, int o)
        {
            long n = Wire.I64(b, o);
            if (n < -1)
                throw new ArgumentException("Invalid nullable integer.");
            return n == -1 ? (long?)null : n;
        }
        private static TaskSlot ReadTask(byte[] b, int o)
        {
            if (b[o] == 0)
            {
                Zero(b, o, 4096);
                return null;
            }
            if (b[o + 1] > 1 || Wire.U32(b, o + 4) > 1)
                throw new ArgumentException("Task flags.");
            if (b[o + 3] > (Wire.U16(b, 8) >= 4 ? 1 : 0))
                throw new ArgumentException("Invalid task coverage flag.");
            Zero(b, o + 166, 2);
            string diagnosticUserId = null;
            if (Wire.U16(b, 8) >= 4)
            {
                diagnosticUserId = ReadContext(b, o + 1753);
                Zero(b, o + 2139, 1957);
            }
            else
                Zero(b, o + 1753, 2343);
            long generation = Wire.I64(b, o + 24);
            ExportBookmark bookmark = null;
            if (Wire.U32(b, o + 4) == 0)
            {
                Zero(b, o + 136, 26);
                Zero(b, o + 176, 32);
            }
            else
                bookmark = ReadBookmark(b, o + 136, o + 176, generation);
            var scope = new RetrievalScope(new DateTime(Wire.I64(b, o + 48), DateTimeKind.Utc), new DateTime(Wire.I64(b, o + 56), DateTimeKind.Utc), b[o + 2], Nullable(b, o + 64), Nullable(b, o + 72), ReadContext(b, o + 595), ReadContext(b, o + 981), ReadContext(b, o + 1367), Nullable(b, o + 80), diagnosticUserId);
            var request = new RetrievalRequest(ReadId(b, o + 208), ReadId(b, o + 466), ReadId(b, o + 337), Wire.I64(b, o + 32), checked((int)Wire.U32(b, o + 40)), scope, new DateTime(Wire.I64(b, o + 88), DateTimeKind.Utc), new ExportLimits(Wire.I64(b, o + 96), Wire.I64(b, o + 104), checked((int)Wire.U32(b, o + 112))));
            return new TaskSlot(request, Wire.U64(b, o + 8), Wire.I64(b, o + 16), generation, (TransferTaskState)b[o], b[o + 1] == 1, checked((int)Wire.U32(b, o + 44)), checked((int)Wire.U32(b, o + 116)), Wire.I64(b, o + 120), Wire.I64(b, o + 128), bookmark, Wire.U16(b, o + 162), b[o + 164], (TransferTerminalReason)b[o + 165], Wire.I64(b, o + 168), b[o + 3] == 1);
        }
        private static void PutPending(byte[] b, PendingBatch p)
        {
            const int o = 16640;
            b[o] = (byte)p.Phase;
            b[o + 1] = (byte)(p.IsError ? 1 : 0);
            Wire.Put16(b, o + 2, (ushort)p.Attempts);
            Wire.Put16(b, o + 4, (ushort)p.Splits);
            Wire.Put16(b, o + 6, (ushort)p.RecordCount);
            Wire.Put32(b, o + 8, (uint)p.Slot);
            Wire.Put32(b, o + 12, (uint)p.BodyLength);
            Wire.PutU64(b, o + 16, p.SlotGeneration);
            Wire.Put64(b, o + 24, p.Revision);
            Wire.Put64(b, o + 32, p.CleanupGeneration);
            Wire.Put64(b, o + 40, p.RetryUtc);
            byte[] hash = p.CopyHash(), body = p.CopyBody();
            try
            {
                Buffer.BlockCopy(hash, 0, b, o + 48, 32);
                PutBookmark(b, o + 80, o + 112, p.Before);
                PutBookmark(b, o + 144, o + 176, p.After);
                Buffer.BlockCopy(body, 0, b, 17664, body.Length);
            }
            finally
            {
                Wire.Clear(hash);
                Wire.Clear(body);
            }
        }
        private static PendingBatch ReadPending(byte[] b)
        {
            const int o = 16640;
            uint n = Wire.U32(b, o + 12);
            if (n < 1888 || n > 67424 || b[o + 1] > 1)
                throw new ArgumentException("Pending length/class.");
            Zero(b, o + 106, 6);
            Zero(b, o + 170, 6);
            Zero(b, o + 208, 816);
            Zero(b, 17664 + (int)n, 67424 - (int)n);
            byte[] body = Wire.Slice(b, 17664, (int)n), hash = null;
            try
            {
                long generation = Wire.I64(b, o + 32);
                var p = new PendingBatch((PendingPhase)b[o], b[o + 1] == 1, Wire.U16(b, o + 2), Wire.U16(b, o + 4), Wire.U16(b, o + 6), checked((int)Wire.U32(b, o + 8)), Wire.U64(b, o + 16), Wire.I64(b, o + 24), generation, Wire.I64(b, o + 40), ReadBookmark(b, o + 80, o + 112, generation), ReadBookmark(b, o + 144, o + 176, generation), body);
                hash = p.CopyHash();
                if (!Wire.Equal(hash, 0, b, o + 48, 32))
                    throw new ArgumentException("Pending hash mismatch.");
                return p;
            }
            finally
            {
                Wire.Clear(body);
                Wire.Clear(hash);
            }
        }
    }
}
