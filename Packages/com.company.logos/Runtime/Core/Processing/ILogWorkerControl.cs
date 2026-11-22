using System;
namespace Logos
{
    public enum WorkerControlPhase
    {
        Startup, Running, DayTransition, Stopping, Finalizing
    }
    public enum WorkerStopKind
    {
        DrainUntilDeadline, DropPending
    }
    public enum WorkerFinalizationReason
    {
        Drained, StopDeadline, DropRequested, ProcessorFault
    }
    // Progressed 用来重新尝试先前遇到背压的类别；停止阶段另看 DrainComplete，以判断后端是否已经排空。
    public readonly struct WorkerControlResult
    {
        public bool Progressed
        {
            get;
        }
        public bool DrainComplete
        {
            get;
        }
        public WorkerControlResult(bool progressed, bool drainComplete = false)
        {
            Progressed = progressed;
            DrainComplete = drainComplete;
        }
    }
    // 包括释放在内的调用都属于同一个工作线程。CompletePending 仅发布结果：
    // 不得编码、分配或执行 I/O。Dispose 不得发布结果或提交新数据。
    // Dispose 抛异常时仍保留所有权，且必须幂等，以便工作线程重试释放。
    public interface ILogWorkerControl : IDisposable
    {
        WorkerControlResult Step(WorkerControlContext context);
        long NextWakeTicks
        {
            get;
        }
        void CompletePending(WorkerFinalizationReason reason);
    }
}
