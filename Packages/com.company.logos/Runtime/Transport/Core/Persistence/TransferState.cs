using System;
namespace Logos.Transport
{
    // 冻结候选：最多四个不可变槽位、一个不可变待处理正文和一个游标。
    // 转换授权、已知用量下界和 Journal 代际证明由 B2 负责。
    // 四个任务槽位共享每日网络用量及待发送批次，持久化时须作为同一份状态处理。
    internal sealed class TransferState
    {
        private readonly TaskSlot[] slots;
        internal long NetworkDay
        {
            get;
        }
        internal long OriginalU
        {
            get;
        }
        internal long OriginalV
        {
            get;
        }
        internal ulong NormalUsed
        {
            get;
        }
        internal ulong ErrorUsed
        {
            get;
        }
        internal long CleanupGeneration
        {
            get;
        }
        internal long RevokedThrough
        {
            get;
        }
        internal long GlobalThrottleUtc
        {
            get;
        }
        internal bool UnknownUsage
        {
            get;
        }
        internal ulong NextSlotGeneration
        {
            get;
        }
        internal PendingBatch Pending
        {
            get;
        }
        internal ExportBookmark AutoBookmark
        {
            get;
        }
        internal ulong AutoTaskCounter
        {
            get;
        }
        internal TaskSlot Slot(int index) => slots[index];
        internal static TransferState Empty() => new TransferState();
        internal TransferState(long networkDay = -1, long originalU = 0, long originalV = 0, ulong normalUsed = 0, ulong errorUsed = 0, long cleanupGeneration = 0, long revokedThrough = -1, bool unknownUsage = false, ulong nextSlotGeneration = 1, TaskSlot[] tasks = null, PendingBatch pending = null, ExportBookmark autoBookmark = null, ulong autoTaskCounter = 0, long globalThrottleUtc = 0)
        {
            if (globalThrottleUtc < 0 || globalThrottleUtc > DateTime.MaxValue.Ticks || networkDay < -1 || networkDay > DateTime.MaxValue.Ticks / TimeSpan.TicksPerDay || cleanupGeneration < 0 || revokedThrough < -1 || nextSlotGeneration == 0 || normalUsed > long.MaxValue || errorUsed > long.MaxValue || (tasks != null && tasks.Length != 4) || originalU < 0 || originalV < 0 || ((originalU == 0 || originalV == 0) ? originalU != 0 || originalV != 0 || normalUsed != 0 || errorUsed != 0 : originalV >= originalU) || (networkDay == -1 && (originalU != 0 || originalV != 0 || normalUsed != 0 || errorUsed != 0)) || (autoBookmark == null && autoTaskCounter != 0) || (autoBookmark != null && autoBookmark.CleanupGeneration != cleanupGeneration))
                throw new ArgumentException("Invalid transfer state header.");
            slots = tasks == null ? new TaskSlot[4] : (TaskSlot[])tasks.Clone();
            for (int i = 0; i < 4; i++)
            {
                var t = slots[i];
                if (t == null)
                    continue;
                if (t.CleanupGeneration != cleanupGeneration || t.Generation >= nextSlotGeneration)
                    throw new ArgumentException("Slot generation mismatch.");
                for (int j = 0; j < i; j++)
                    if (slots[j] != null && (slots[j].Generation == t.Generation || slots[j].Request.TaskId == t.Request.TaskId))
                        throw new ArgumentException("Duplicate task or generation.");
                bool owns = pending != null && pending.Slot == i;
                if ((t.State == TransferTaskState.Pending || t.State == TransferTaskState.Retry) != owns)
                    throw new ArgumentException("Pending row must uniquely own body.");
            }
            if (pending != null)
                pending.Validate(slots[pending.Slot], cleanupGeneration);
            GlobalThrottleUtc = globalThrottleUtc;
            NetworkDay = networkDay;
            OriginalU = originalU;
            OriginalV = originalV;
            NormalUsed = normalUsed;
            ErrorUsed = errorUsed;
            CleanupGeneration = cleanupGeneration;
            RevokedThrough = revokedThrough;
            UnknownUsage = unknownUsage;
            NextSlotGeneration = nextSlotGeneration;
            Pending = pending;
            AutoBookmark = autoBookmark;
            AutoTaskCounter = autoTaskCounter;
        }
        internal bool IsReset => Pending == null && AutoBookmark == null && AutoTaskCounter == 0 && slots[0] == null && slots[1] == null && slots[2] == null && slots[3] == null;
    }
}
