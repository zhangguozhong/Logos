namespace Logos.Transport
{
    internal sealed class TransferHealthSnapshot
    {
        internal string Reason
        {
            get;
        }
        internal bool Ready
        {
            get;
        }
        internal TransferStoreStatus StoreStatus
        {
            get;
        }
        internal bool OperationOwned
        {
            get;
        }
        internal bool AwaitingClose
        {
            get;
        }
        internal long SessionAttempts
        {
            get;
        }
        internal long SessionAcknowledgedRecords
        {
            get;
        }
        internal TransferHealthSnapshot(string reason, bool ready, TransferStoreStatus status, bool operation = false, bool closing = false, long attempts = 0, long acknowledged = 0)
        {
            Reason = reason;
            Ready = ready;
            StoreStatus = status;
            OperationOwned = operation;
            AwaitingClose = closing;
            SessionAttempts = attempts;
            SessionAcknowledgedRecords = acknowledged;
        }
    }
    internal sealed class TransferTaskSnapshot
    {
        internal string TaskId
        {
            get;
        }
        internal TransferTaskState State
        {
            get;
        }
        internal TransferTerminalReason Reason
        {
            get;
        }
        // 恢复的任务可以读出持久状态，但缺少会话内确认计数时需保留未知标记。
        internal bool CountsKnown
        {
            get;
        }
        internal long? AcknowledgedRecords
        {
            get;
        }
        internal long? AcknowledgedBatches
        {
            get;
        }
        internal TransferTaskSnapshot(TaskSlot row, bool known, long? records, long? batches)
        {
            TaskId = row.Request.TaskId;
            State = row.State;
            Reason = row.Reason;
            CountsKnown = known;
            AcknowledgedRecords = records;
            AcknowledgedBatches = batches;
        }
    }
}
