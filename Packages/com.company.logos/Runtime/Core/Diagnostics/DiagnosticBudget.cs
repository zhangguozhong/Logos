using System;
using System.Threading;
namespace Logos
{
    public sealed class TemporaryDiagnosticRequest
    {
        public const int Version = 1, MaximumAttempts = 256, MaximumBytes = 256 * 1024;
        public const long MaximumDurationTicks = 60 * TimeSpan.TicksPerSecond;
        public LogLevel MinimumLevel
        {
            get;
        }
        public long DurationTicks
        {
            get;
        }
        public int Attempts
        {
            get;
        }
        public int EstimatedBytes
        {
            get;
        }
        public TemporaryDiagnosticRequest(LogLevel minimumLevel, TimeSpan duration, int attempts, int estimatedBytes)
        {
            if ((minimumLevel != LogLevel.Debug && minimumLevel != LogLevel.Trace) || duration.Ticks <= 0 || duration.Ticks > MaximumDurationTicks
                || attempts < 1 || attempts > MaximumAttempts || estimatedBytes < 1 || estimatedBytes > MaximumBytes)
                throw new ArgumentOutOfRangeException();
            MinimumLevel = minimumLevel;
            DurationTicks = duration.Ticks;
            Attempts = attempts;
            EstimatedBytes = estimatedBytes;
        }
    }
    // 一个不可变信箱；替换策略、上下文或撤销均推进请求版本，旧请求永不自动续期。
    public sealed class DiagnosticControlState
    {
        public long PipelineGeneration
        {
            get;
        }
        public long PolicyRevision
        {
            get;
        }
        public long ContextRevision
        {
            get;
        }
        public LogContext Context
        {
            get;
        }
        public long RequestRevision
        {
            get;
        }
        public TemporaryDiagnosticRequest Request
        {
            get;
        }
        public bool Enabled
        {
            get;
        }
        public bool BudgetEnabled
        {
            get;
        }
        // 写入由流水线既有生命周期门串行化，worker 按所读控制代际独立查看进度。
        private long incidentObservation;
        internal long NextIncidentObservation() => Interlocked.Increment(ref incidentObservation);
        internal long LatestIncidentObservation => Interlocked.Read(ref incidentObservation);

        internal DiagnosticControlState(long generation, long policyRevision, long contextRevision, LogContext context, long requestRevision, TemporaryDiagnosticRequest request, bool enabled, bool budgetEnabled)
        {
            PipelineGeneration = generation;
            PolicyRevision = policyRevision;
            ContextRevision = contextRevision;
            Context = context;
            RequestRevision = requestRevision;
            Request = request;
            Enabled = enabled;
            BudgetEnabled = budgetEnabled;
        }
    }
    public enum DiagnosticFilterDetail
    {
        DetailFloor, Sampled, PermitExhausted, PermitContention, Count
    }
    public enum DiagnosticPermitCharge
    {
        Granted, Exhausted, Contention
    }
    // 公共面只读；收费仅供 Core 回调，worker 刷新快照必须复用同一对象。
    public sealed class DiagnosticPermit
    {
        private readonly object gate = new object();
        private int remainingAttempts, remainingBytes;
        private int invalidated;
        public long RequestRevision
        {
            get;
        }
        public long IssuedTicks
        {
            get;
        }
        public long ExpiresTicks
        {
            get;
        }
        public LogLevel MinimumLevel
        {
            get;
        }
        public bool IsInvalidated => Volatile.Read(ref invalidated) != 0;
        public int RemainingAttempts => Volatile.Read(ref remainingAttempts);
        public int RemainingEstimatedBytes => Volatile.Read(ref remainingBytes);
        public DiagnosticPermit(long requestRevision, TemporaryDiagnosticRequest request, long issuedTicks)
        {
            if (request == null || requestRevision < 1 || issuedTicks < 0 || issuedTicks > long.MaxValue - request.DurationTicks)
                throw new ArgumentOutOfRangeException();
            RequestRevision = requestRevision;
            IssuedTicks = issuedTicks;
            ExpiresTicks = issuedTicks + request.DurationTicks;
            MinimumLevel = request.MinimumLevel;
            remainingAttempts = request.Attempts;
            remainingBytes = request.EstimatedBytes;
        }
        internal void Invalidate() => Interlocked.Exchange(ref invalidated, 1);
        // 一次请求同时占用一次尝试和相应字节。先核对期限及两项余量，任一不足时都不会部分扣款。
        internal DiagnosticPermitCharge Charge(long now, int bytes)
        {
            if (!Monitor.TryEnter(gate))
                return DiagnosticPermitCharge.Contention;
            try
            {
                if (Volatile.Read(ref invalidated) != 0 || now < IssuedTicks || now >= ExpiresTicks || bytes <= 0 || remainingAttempts == 0 || remainingBytes < bytes)
                {
                    remainingAttempts = remainingBytes = 0;
                    return DiagnosticPermitCharge.Exhausted;
                }
                remainingAttempts--;
                remainingBytes -= bytes;
                return DiagnosticPermitCharge.Granted;
            }
            finally
            {
                Monitor.Exit(gate);
            }
        }
    }
    // clear 的跨线程屏障：读取无锁；竞争或代际耗尽永久 fail-closed，不回绕。
    // 预算快照可以被多个生产者持有。通过比较捕获的代际，同一次失效操作也能让已经分发出去的快照过期。
    internal sealed class DiagnosticBudgetFence
    {
        private long generation;
        private int exhausted;
        internal long Capture() => Interlocked.Read(ref generation);
        internal bool IsCurrent(long captured) => Volatile.Read(ref exhausted) == 0 && captured == Interlocked.Read(ref generation);
        internal void Invalidate()
        {
            long previous = Interlocked.Read(ref generation);
            if (previous == long.MaxValue || Interlocked.CompareExchange(ref generation, previous + 1, previous) != previous)
                Interlocked.Exchange(ref exhausted, 1);
        }
    }
    public sealed class DiagnosticBudgetSnapshot
    {
        private readonly DiagnosticBudgetFence fence;
        private readonly long fenceGeneration;
        public bool IsCurrent => fence == null || fence.IsCurrent(fenceGeneration);
        public DiagnosticControlState Control
        {
            get;
        }
        public QuotaDayTag QuotaDayTag
        {
            get;
        }
        public long WriteGeneration
        {
            get;
        }
        public long CleanupGeneration
        {
            get;
        }
        public long IssuedTicks
        {
            get;
        }
        public long ExpiresTicks
        {
            get;
        }
        public bool Trusted
        {
            get;
        }
        public int SampleEvery
        {
            get;
        }
        public DiagnosticPermit Permit
        {
            get;
        }
        public DiagnosticBudgetSnapshot(DiagnosticControlState control, QuotaDayTag tag, long writeGeneration, long cleanupGeneration, long issuedTicks, long expiresTicks, bool trusted, int sampleEvery, DiagnosticPermit permit)
            : this(control, tag, writeGeneration, cleanupGeneration, issuedTicks, expiresTicks, trusted, sampleEvery, permit, null, 0)
        {
        }
        internal DiagnosticBudgetSnapshot(DiagnosticControlState control, QuotaDayTag tag, long writeGeneration, long cleanupGeneration, long issuedTicks, long expiresTicks, bool trusted, int sampleEvery, DiagnosticPermit permit, DiagnosticBudgetFence fence, long fenceGeneration)
        {
            this.fence = fence;
            this.fenceGeneration = fenceGeneration;
            if (control == null || issuedTicks < 0 || expiresTicks < issuedTicks || (sampleEvery != 1 && sampleEvery != 2 && sampleEvery != 4 && sampleEvery != 8))
                throw new ArgumentException();
            Control = control;
            QuotaDayTag = tag;
            WriteGeneration = writeGeneration;
            CleanupGeneration = cleanupGeneration;
            IssuedTicks = issuedTicks;
            ExpiresTicks = expiresTicks;
            Trusted = trusted;
            SampleEvery = sampleEvery;
            Permit = permit;
        }
    }
    public interface IDiagnosticControlBridge
    {
        DiagnosticControlState ReadControlState();
        void PublishBudgetSnapshot(DiagnosticBudgetSnapshot snapshot);
    }
}
