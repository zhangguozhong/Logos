namespace Logos
{
    public enum TransferPauseReason
    {
        None, Budget, Backpressure, Cancel
    }
    // 前缀表示后端已接手的记录数；持久化结果随后经 TryPeekOutcome 返回。背压等暂缓原因用于保留尚未转移的队首。
    public readonly struct BatchTransferResult
    {
        public int TransferredPrefix
        {
            get;
        }
        public bool NextRecordFailed
        {
            get;
        }
        public AdmissionReason FailureReason
        {
            get;
        }
        public TransferPauseReason PauseReason
        {
            get;
        }
        public BatchTransferResult(int transferredPrefix, bool nextRecordFailed = false, AdmissionReason failureReason = AdmissionReason.ProcessorRejected, TransferPauseReason pauseReason = TransferPauseReason.None)
        {
            TransferredPrefix = transferredPrefix;
            NextRecordFailed = nextRecordFailed;
            FailureReason = failureReason;
            PauseReason = pauseReason;
        }
    }
}
