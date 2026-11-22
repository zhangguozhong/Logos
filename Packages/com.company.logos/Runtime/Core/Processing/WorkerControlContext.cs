using System;
namespace Logos
{
    public interface IWorkerQueuedInput
    {
        bool HasQueuedInput
        {
            get;
        }
    }
    // 所有时间都使用 Core 时钟的起点和 100 ns 单位，不使用原始 Stopwatch 时间戳。
    // 此值可在同步调用内复制；其中的探针仍实时生效。
    public readonly struct WorkerControlContext
    {
        private readonly IMonotonicClock clock;
        private readonly WorkerStopSignal stop;
        private readonly long workDeadline;
        public WorkerControlPhase Phase
        {
            get;
        }
        public long NowTicks => clock.TimestampTicks;
        public long DeadlineTicks => StopRequested ? Math.Min(workDeadline, StopDeadlineTicks) : workDeadline;
        public long RemainingBudgetTicks => Math.Max(0, DeadlineTicks - NowTicks);
        public bool DeadlineReached => NowTicks >= DeadlineTicks;
        public bool StopRequested => stop.Requested;
        public WorkerStopKind StopKind => stop.Kind;
        public long StopDeadlineTicks => stop.Deadline;
        public bool IsCancellationRequested => stop.ShouldCancel(NowTicks);
        public ILogAdmissionController Admission
        {
            get;
        }
        // 外部控制器无法暴露队列时采用保守值。读取不产生分配。
        public bool HasQueuedInput => !(Admission is IWorkerQueuedInput input) || input.HasQueuedInput;
        internal IMonotonicClock Clock => clock;
        internal WorkerControlContext LimitDeadline(long deadline) => new WorkerControlContext(clock, stop, Math.Min(workDeadline, deadline), Phase, Admission);
        internal WorkerControlContext(IMonotonicClock clock, WorkerStopSignal stop, long deadline, WorkerControlPhase phase, ILogAdmissionController admission)
        {
            this.clock = clock;
            this.stop = stop;
            workDeadline = deadline;
            Phase = phase;
            Admission = admission;
        }
    }
}
