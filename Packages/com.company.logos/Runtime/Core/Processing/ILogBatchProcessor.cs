using System;
namespace Logos
{
    public readonly struct LogBatchView
    {
        private readonly LogRecord[] records;
        private readonly int offset;
        public int Count
        {
            get;
        }
        public LogRecord this[int index]
        {
            get
            {
                if (index < 0 || index >= Count)
                    throw new ArgumentOutOfRangeException(nameof(index));
                return records[offset + index];
            }
        }
        internal LogBatchView(LogRecord[] records, int offset, int count)
        {
            this.records = records;
            this.offset = offset;
            Count = count;
        }
    }
    public enum BatchBudgetReason
    {
        None, DeadlineReached, CancellationRequested
    }
    public readonly struct BatchProcessResult
    {
        public int SucceededPrefix
        {
            get;
        }
        public bool NextRecordFailed
        {
            get;
        }
        public BatchBudgetReason BudgetReason
        {
            get;
        }
        public BatchProcessResult(int succeededPrefix, bool nextRecordFailed = false, BatchBudgetReason budgetReason = BatchBudgetReason.None)
        {
            SucceededPrefix = succeededPrefix;
            NextRecordFailed = nextRecordFailed;
            BudgetReason = budgetReason;
        }
    }
    public readonly struct BatchProcessingContext
    {
        private readonly IMonotonicClock clock;
        private readonly WorkerStopSignal stop;
        public bool IsCancellationRequested => stop.ShouldCancel(clock.TimestampTicks);
        public long DeadlineTicks
        {
            get;
        }
        public bool DeadlineReached => clock.TimestampTicks >= DeadlineTicks;
        internal BatchProcessingContext(IMonotonicClock clock, WorkerStopSignal stop, long deadlineTicks)
        {
            this.clock = clock;
            this.stop = stop;
            DeadlineTicks = deadlineTicks;
        }
    }
    // 只读视图和记录仅在本次同步调用期间借用；不得保留或异步使用。
    // 视图仅暴露此后缀；只有工作线程能清空或复用其私有数组。
    // 实现必须限制工作量，在记录之间检查取消或截止时间，并且只报告确定结果。
    // 已知下一条记录损坏时结束本次调用；工作线程丢弃它，并继续处理未触及的后缀。
    // 只返回确定成功的前缀，可附带一条确定失败的下一条记录，或因预算而未触及的后缀。
    // 零进展必须明确给出预算或取消原因。CancellationRequested 必须对应上下文中的取消。
    // 通过上下文观察关闭；协作取消应返回 CancellationRequested，而不是抛异常。
    // 任何异常都会使整个未决后缀变为不确定；该后缀会被丢弃且永不重试。处理完成不代表持久化。
    public interface ILogBatchProcessor
    {
        BatchProcessResult ProcessBatch(LogBatchView batch, BatchProcessingContext context);
    }
}
