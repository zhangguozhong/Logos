using System;
using System.Threading;
namespace Logos.Storage
{
    // 清理需要先处理目标日期仍在内存中的所有权。异步结果被确认后，磁盘阶段才可删除对应文件。
    public sealed partial class StorageBatchProcessor
    {
        private IClearMemoryBarrier auxiliaryBarrier;
        internal JournalSession TransferJournal => journal;
        internal void AttachAuxiliaryBarrier(IClearMemoryBarrier barrier)
        {
            if (initialized || auxiliaryBarrier != null || barrier == null)
                throw new InvalidOperationException("Auxiliary barrier must be attached once before worker start.");
            auxiliaryBarrier = barrier;
        }
        private ClearScope pendingClear, clearTargets;
        private int clearOwned;
        public ClearRequestResult RequestClear(ClearScope scope)
        {
            if (scope == null)
                throw new ArgumentNullException(nameof(scope));
            if (disposed || completed)
                return ClearRequestResult.Failed;
            if (scope.Kind == 2)
                return ClearRequestResult.UnsupportedScope;
            if (Volatile.Read(ref clearOwned) != 0 || Interlocked.CompareExchange(ref pendingClear, scope, null) != null)
                return ClearRequestResult.Busy;
            diagnosticBudgetFence.Invalidate();
            diagnosticBridge?.PublishBudgetSnapshot(null);
            WakeAt(0);
            return ClearRequestResult.Pending;
        }
        private bool ProcessClearRequest()
        {
            if (!journal.Clear.IsActive && journal.Clear.Status == ClearStatus.Completed)
            {
                Volatile.Write(ref clearOwned, 0);
                clearTargets = null;
                clearDayCursor = 0;
            }
            var scope = Volatile.Read(ref pendingClear);
            if (scope == null || journal.Status != JournalStatus.Ready)
                return false;
            Volatile.Write(ref clearOwned, 1);
            var result = journal.RequestClear(scope);
            if (result == ClearRequestResult.Busy)
                return false;
            Interlocked.Exchange(ref pendingClear, null);
            if (result != ClearRequestResult.Pending)
                Volatile.Write(ref clearOwned, 0);
            return true;
        }
        public void CloseTargets(ClearScope scope)
        {
            CheckWorker();
            clearDayCursor = 0;
            clearTargets = scope;
            InvalidateDiagnosticBudget();
            Volatile.Write(ref clearOwned, 1);
            auxiliaryBarrier?.CloseTargets(scope);
            PublishGate();
        }
        public bool ClearOwnedMemory(long cleanupGeneration)
        {
            return ClearStorageOwnedMemory(cleanupGeneration) && (auxiliaryBarrier?.ClearOwnedMemory(cleanupGeneration) ?? true);
        }
        private bool ClearStorageOwnedMemory(long cleanupGeneration)
        {
            CheckWorker();
            if (clearTargets == null)
                return true;
            if (clearTargets.Contains(activeDay))
            {
                if (!normal.IsEmpty && !normal.HasOutcome)
                {
                    Publish(normal, AdmissionReason.Cleared);
                    return false;
                }
                if (!error.IsEmpty && !error.HasOutcome)
                {
                    Publish(error, AdmissionReason.Cleared);
                    return false;
                }
                DropDiagnostic(AdmissionReason.Cleared);
                if (!normal.IsEmpty || !error.IsEmpty || DiagnosticOwned)
                    return false;
            }
            // Clear 最多处理 1024 天。只保留数值游标，每次调用最多将一个队首入队。
            if (clearTargets.Kind == 1)
            {
                // 所有队列准入归属于已发布的生效日期；发布前先排空旧日期数据。
                var result = admission?.DropOneQueuedDay(journal.CurrentEffectiveDay ?? activeDay, LogClassMask.All, AdmissionReason.Cleared) ?? QueueDayDropResult.NoMatchingHead;
                return result == QueueDayDropResult.NoMatchingHead;
            }
            while (clearDayCursor < clearTargets.Days.Length)
            {
                var result = admission?.DropOneQueuedDay(clearTargets.Days[clearDayCursor], LogClassMask.All, AdmissionReason.Cleared) ?? QueueDayDropResult.NoMatchingHead;
                if (result != QueueDayDropResult.NoMatchingHead)
                    return false;
                clearDayCursor++;
                return false;
            }
            return true;
        }
        private int clearDayCursor;
    }
}
