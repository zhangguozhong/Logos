using System;
using System.Security.Cryptography;
using Logos.Storage;
namespace Logos.Transport
{
    internal enum PendingPhase : byte
    {
        None, Prepared, AttemptReserved, Retry
    }
    internal sealed class PendingBatch
    {
        private readonly byte[] body, hash;
        internal PendingPhase Phase
        {
            get;
        }
        internal bool IsError
        {
            get;
        }
        internal int Attempts
        {
            get;
        }
        internal int Splits
        {
            get;
        }
        internal int RecordCount
        {
            get;
        }
        internal int Slot
        {
            get;
        }
        internal int BodyLength => body.Length;
        internal ulong SlotGeneration
        {
            get;
        }
        internal long Revision
        {
            get;
        }
        internal long CleanupGeneration
        {
            get;
        }
        internal long RetryUtc
        {
            get;
        }
        internal ExportBookmark Before
        {
            get;
        }
        internal ExportBookmark After
        {
            get;
        }
        // 此定位信息受已认证状态页保护。它只是保留的
        // 引用；C 在发送前仍必须认证导出封装。
        internal bool TryGetExportKeyReference(out string keyId)
        {
            keyId = null;
            int length = body[1088];
            if (length < 1 || length > 64)
                return false;
            for (int i = 1089; i < 1089 + length; i++)
                if (body[i] < 33 || body[i] > 126)
                    return false;
            keyId = System.Text.Encoding.ASCII.GetString(body, 1089, length);
            return true;
        }
        internal byte[] CopyBody() => (byte[])body.Clone();
        internal byte[] CopyHash() => (byte[])hash.Clone();
        internal PendingBatch(PendingPhase phase, bool isError, int attempts, int splits, int recordCount, int slot, ulong slotGeneration, long revision, long cleanupGeneration, long retryUtc, ExportBookmark before, ExportBookmark after, byte[] body)
        {
            if (phase < PendingPhase.Prepared || phase > PendingPhase.Retry || attempts < 0 || attempts > 5 || (phase != PendingPhase.Prepared && attempts == 0) || splits < 0 || splits > 8 || recordCount < 1 || recordCount > 256 || slot < 0 || slot > 3 || slotGeneration == 0 || revision < 0 || cleanupGeneration < 0 || retryUtc < 0 || retryUtc > DateTime.MaxValue.Ticks || before == null || after == null || body == null || body.Length < 1888 || body.Length > 67424)
                throw new ArgumentException("Invalid bounded pending manifest.");
            Phase = phase;
            IsError = isError;
            Attempts = attempts;
            Splits = splits;
            RecordCount = recordCount;
            Slot = slot;
            SlotGeneration = slotGeneration;
            Revision = revision;
            CleanupGeneration = cleanupGeneration;
            RetryUtc = retryUtc;
            Before = before;
            After = after;
            this.body = (byte[])body.Clone();
            using (var sha = SHA256.Create())
                hash = sha.ComputeHash(this.body);
        }
        // 待发送正文必须对应当前槽位里的任务及书签，读取持久状态时也要验证这层关系。
        internal void Validate(TaskSlot task, long generation)
        {
            if (task == null || RecordCount > task.MaxRecords || task.Generation != SlotGeneration || task.Revision != Revision || task.CleanupGeneration != CleanupGeneration || generation != CleanupGeneration || Before.CleanupGeneration != generation || After.CleanupGeneration != generation || (Phase == PendingPhase.Retry) != (task.State == TransferTaskState.Retry) || task.Attempts != Attempts || task.Splits != Splits || task.Bookmark == null || !Same(task.Bookmark, Before))
                throw new ArgumentException("Pending owner mismatch.");
            // 此有界前缀检查只检查结构。C 必须认证导出密钥，
            // 并将每一条内部记录与当前请求和授权的交集重新核对。
            var b = body;
            if (!Wire.Equal(b, 0, System.Text.Encoding.ASCII.GetBytes("LOGOSX01"), 0, 8) || (Wire.U16(b, 8) != 1 && Wire.U16(b, 8) != 2) || Wire.U16(b, 10) != 1024 || Wire.U32(b, 12) != b.Length || Wire.U32(b, 16) != b.Length - 1056 || Wire.U16(b, 20) != RecordCount || Wire.U16(b, 22) != (IsError ? 1 : 0) || Wire.I64(b, 24) != generation || Wire.I64(b, 32) != Revision || TransferStateCodec.ReadId(b, 96) != task.Request.TaskId || TransferStateCodec.ReadId(b, 225) != task.Request.DestinationId || TransferStateCodec.ReadId(b, 354) != task.Request.InstallationId)
                throw new ArgumentException("Envelope prefix mismatch.");
            TransferStateCodec.Zero(b, 60, 4);
            TransferStateCodec.Zero(b, 483, 29);
            if (Wire.U16(b, 8) == 1)
                TransferStateCodec.Zero(b, 544, 480);
            else
                TransferStateCodec.Zero(b, 800, 224);
            if (Before.Day < task.Request.FirstQuotaDay || Before.Day >= task.Request.FirstQuotaDay + task.Request.QuotaDays || Wire.I64(b, 40) != Before.Day || Wire.I64(b, 48) != Before.BlockOffset || Wire.I64(b, 88) != Before.CapturedEnd || Wire.U16(b, 56) != Before.NextRecordIndex || Before.NextRecordIndex > 255 || Wire.U16(b, 58) != After.NextRecordIndex || After.NextRecordIndex <= Before.NextRecordIndex || RecordCount > After.NextRecordIndex - Before.NextRecordIndex || Before.BlockOffset >= Before.CapturedEnd || After.Day != Before.Day || After.CapturedEnd != Before.CapturedEnd || After.BlockOffset != Before.BlockOffset || !Before.HasHeader(After.CopyHeaderHash()) || !Before.HasHeader(Wire.Slice(b, 512, 32)))
                throw new ArgumentException("Envelope bookmark mismatch.");
        }
        private static bool Same(ExportBookmark a, ExportBookmark b) => a.Day == b.Day && a.CapturedEnd == b.CapturedEnd && a.BlockOffset == b.BlockOffset && a.NextRecordIndex == b.NextRecordIndex && a.HasHeader(b.CopyHeaderHash());
    }
}
