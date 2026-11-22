namespace Logos.Transport
{
    /// <summary>持久记录身份。工作线程执行释放前，所有代际仍须匹配。</summary>
    public readonly struct TransferTaskHandle
    {
        public string TaskId
        {
            get;
        }
        public ulong SlotGeneration
        {
            get;
        }
        public long CleanupGeneration
        {
            get;
        }
        public bool IsAutomatic
        {
            get;
        }
        internal TransferTaskHandle(string id, ulong generation, long cleanup, bool automatic)
        {
            TaskId = id;
            SlotGeneration = generation;
            CleanupGeneration = cleanup;
            IsAutomatic = automatic;
        }
    }
    public readonly struct TransferTaskObservation
    {
        public bool DurableObserved
        {
            get;
        }
        public TransferTaskHandle Handle
        {
            get;
        }
        public TransferTaskState State
        {
            get;
        }
        public TransferTerminalReason Reason
        {
            get;
        }
        public long? AcknowledgedRecords
        {
            get;
        }
        public long? AcknowledgedBatches
        {
            get;
        }
        internal TransferTaskObservation(TaskSlot row, bool known, long records, long batches)
        {
            DurableObserved = true;
            Handle = new TransferTaskHandle(row.Request.TaskId, row.Generation, row.CleanupGeneration, row.AutoError);
            State = row.State;
            Reason = row.Reason;
            AcknowledgedRecords = known ? records : (long?)null;
            AcknowledgedBatches = known ? batches : (long?)null;
        }
    }
    /// <summary>工作线程的最近发布。缺失记录既不能证明命令失败，也不能证明磁盘不存在该记录。</summary>
    public readonly struct TransferSnapshot
    {
        public bool Ready
        {
            get;
        }
        public string Reason
        {
            get;
        }
        public bool OperationOwned
        {
            get;
        }
        public bool AwaitingClose
        {
            get;
        }
        public long SessionAttempts
        {
            get;
        }
        public long SessionAcknowledgedRecords
        {
            get;
        }
        public TransferStoreStatus StoreStatus
        {
            get;
        }
        public TransferTaskObservation Task0
        {
            get;
        }
        public TransferTaskObservation Task1
        {
            get;
        }
        public TransferTaskObservation Task2
        {
            get;
        }
        public TransferTaskObservation Task3
        {
            get;
        }
        internal TransferSnapshot(bool ready, string reason, TransferStoreStatus status, bool operation, bool closing, long attempts, long acknowledged, TransferTaskObservation task0, TransferTaskObservation task1, TransferTaskObservation task2, TransferTaskObservation task3)
        {
            Ready = ready;
            Reason = reason;
            StoreStatus = status;
            OperationOwned = operation;
            AwaitingClose = closing;
            SessionAttempts = attempts;
            SessionAcknowledgedRecords = acknowledged;
            Task0 = task0;
            Task1 = task1;
            Task2 = task2;
            Task3 = task3;
        }
    }
    internal sealed partial class TransferCoordinator
    {
        internal TransferSnapshot CapturePublicSnapshot()
        {
            CheckOwner();
            return new TransferSnapshot(ready, stopping ? "Stopped" : clearing ? "Clearing" : reason, store.Status, permit != null, closing, sessionAttempts, sessionAcknowledged, Observe(0), Observe(1), Observe(2), Observe(3));
        }
        private TransferTaskObservation Observe(int slot)
        {
            var row = store.Durable?.Slot(slot);
            return row == null ? default : new TransferTaskObservation(row, countsKnown[slot] && countGeneration[slot] == row.Generation, ackRecords[slot], ackBatches[slot]);
        }
        // 释放前比对任务与清理代数，避免手里的旧句柄清掉复用同一 ID 的新任务。
        internal TransferControlStatus Release(TransferTaskHandle handle)
        {
            CheckOwner();
            if (handle.TaskId == null || handle.IsAutomatic)
                return TransferControlStatus.StaleTaskHandle;
            for (int i = 0; i < 3; i++)
            {
                var row = store.Durable?.Slot(i);
                if (row == null || row.Request.TaskId != handle.TaskId)
                    continue;
                if (row.Generation != handle.SlotGeneration || row.CleanupGeneration != handle.CleanupGeneration || row.AutoError != handle.IsAutomatic)
                    return TransferControlStatus.StaleTaskHandle;
                return Release(handle.TaskId) ? TransferControlStatus.ReleaseSaveRequested : TransferControlStatus.ReleaseRejected;
            }
            return TransferControlStatus.StaleTaskHandle;
        }
    }
}
