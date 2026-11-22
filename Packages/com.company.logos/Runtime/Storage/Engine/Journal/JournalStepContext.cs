using System;
using System.Diagnostics;
using System.Threading;

namespace Logos.Storage
{
    // 工作线程的剩余时长要换算到 Stopwatch 时基，停止与取消仍通过实时探针观察，避免复制上下文后失去响应。
    public readonly struct JournalStepContext
    {
        private readonly WorkerControlContext worker;
        private readonly bool hasWorker;
        public CancellationToken Cancellation
        {
            get;
        }
        /// <summary>原始 Stopwatch 时间戳；绝不是 Core 时钟的绝对时间戳。</summary>
        public long DeadlineTimestamp
        {
            get;
        }
        internal bool IsWorkerControlled => hasWorker;
        public bool StopRequested => hasWorker && worker.StopRequested;
        public bool ShouldYield => Cancellation.IsCancellationRequested
            || (hasWorker && (worker.IsCancellationRequested || worker.RemainingBudgetTicks == 0))
            || Stopwatch.GetTimestamp() >= DeadlineTimestamp;
        public static JournalStepContext Unlimited => new JournalStepContext(CancellationToken.None, long.MaxValue);

        public JournalStepContext(CancellationToken cancellation, long deadlineTimestamp)
        {
            Cancellation = cancellation;
            DeadlineTimestamp = deadlineTimestamp;
            worker = default;
            hasWorker = false;
        }
        private JournalStepContext(WorkerControlContext context, long deadlineTimestamp)
        {
            Cancellation = CancellationToken.None;
            DeadlineTimestamp = deadlineTimestamp;
            worker = context;
            hasWorker = true;
        }
        /// <summary>不分配委托或装箱；复制的上下文保留实时的工作线程取消、停止和预算探针。</summary>
        public static JournalStepContext FromWorker(WorkerControlContext context)
        {
            // 先采集原始时间：转换不得延长实时剩余工作额度。
            long rawNow = Stopwatch.GetTimestamp();
            return new JournalStepContext(context, DeadlineFromRemaining(rawNow, context.RemainingBudgetTicks, Stopwatch.Frequency));
        }
        internal static long DeadlineFromRemaining(long rawNow, long remainingBudgetTicks, long stopwatchFrequency)
        {
            if (stopwatchFrequency <= 0)
                throw new ArgumentOutOfRangeException(nameof(stopwatchFrequency));
            if (remainingBudgetTicks <= 0)
                return rawNow;
            decimal available = (decimal)long.MaxValue - rawNow;
            if ((decimal)remainingBudgetTicks / TimeSpan.TicksPerSecond >= available / stopwatchFrequency)
                return long.MaxValue;
            // 上述比较将中间值限制为小于 2^64 乘 10^7，处于 decimal 范围内。
            decimal delta = decimal.Truncate((decimal)remainingBudgetTicks * stopwatchFrequency / TimeSpan.TicksPerSecond);
            return (long)(rawNow + delta);
        }
    }
}
