namespace Logos
{
    // Transfer 恰好借用一条记录，只执行有界复制、编码和账本登记。
    // 若异常逸出，则保证本次调用未转移任何内容，也未留下账本条目。
    // 结果窥读可重复；只有匹配的 AckOutcome 能释放或复用对应固定槽位。
    public interface IBufferedLogBatchProcessor : ILogBatchProcessor, ILogWorkerControl
    {
        LogClassMask ReadyClassMask
        {
            get;
        }
        BatchTransferResult TransferBatch(LogBatchView batch, WorkerControlContext context);
        bool TryPeekOutcome(out LogStorageOutcome outcome);
        bool AckOutcome(ulong sequence);
    }
}
