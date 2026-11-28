using System;
using System.Threading;
namespace Logos
{
    internal enum WorkerStopMode
    {
        DrainUntilDeadline, DropPending
    }
    internal enum WorkerCompletion
    {
        NotStarted, Running, Stopping, StopTimedOut, Stopped, Faulted
    }
    internal enum WorkerDropReason
    {
        ProcessorRejected, ProcessorFault, Shutdown
    }
    internal sealed class WorkerStopSignal
    {
        private readonly object gate = new object();
        private bool requested;
        private WorkerStopMode mode;
        private long deadline;
        internal bool Request(WorkerStopMode value, long ticks)
        {
            lock (gate)
            {
                if (requested)
                    return false;
                mode = value;
                deadline = ticks;
                requested = true;
                return true;
            }
        }
        internal bool Requested
        {
            get
            {
                lock (gate)
                    return requested;
            }
        }
        internal WorkerStopKind Kind
        {
            get
            {
                lock (gate)
                    return (WorkerStopKind)mode;
            }
        }
        internal long Deadline
        {
            get
            {
                lock (gate)
                    return deadline;
            }
        }
        internal bool ShouldCancel(long now)
        {
            lock (gate)
                return requested && (mode == WorkerStopMode.DropPending || now >= deadline);
        }
    }
    internal enum WorkerInputProbePhase
    {
        BeforeReset, AfterReset, BeforeWait
    }
    // 实时快照仅供观察。只有终止的工作线程才具有静止的最终快照。
    internal sealed class WorkerSnapshot
    {
        private readonly long[] consumed, dropped;
        private readonly int[] inFlightByLevel;
        public long ConsumedForTest
        {
            get;
        }
        public long Dropped
        {
            get;
        }
        public int InFlight
        {
            get;
        }
        public int WorkerInFlight
        {
            get;
        }
        public int StorageInFlight
        {
            get;
        }
        public PipelineCompletionKind CompletionKind
        {
            get;
        }
        private readonly int[] storage;
        private readonly long[] committed, bufferedDrops, represented;
        public long GetRepresented(LogLevel level) => represented?[(int)level] ?? 0;
        public long GetCommitted(LogLevel level) => committed?[(int)level] ?? 0;
        public int GetWorkerInFlight(LogLevel level) => inFlightByLevel[(int)level];
        public int GetStorageInFlight(LogLevel level) => storage?[(int)level] ?? 0;
        public long GetBufferedDropped(LogLevel level, AdmissionReason reason) => bufferedDrops?[(int)level * HealthCounters.ReasonCount + (int)reason] ?? 0;
        public int InFlightBytes
        {
            get;
        }
        public long Faults
        {
            get;
        }
        public long BudgetOverruns
        {
            get;
        }
        public long TakeAttempts
        {
            get;
        }
        public long BudgetWaits
        {
            get;
        }
        public long ProducerWakeSets
        {
            get;
        }
        public long ControlWakeSets
        {
            get;
        }
        public long WakeSets => ProducerWakeSets + ControlWakeSets;
        internal WorkerSnapshot(long[] consumed, long[] dropped, int inFlight, int bytes, long faults, long overruns, long takes, long waits, int[] inFlightByLevel, int[] storage = null, long[] committed = null, long[] bufferedDrops = null, long producerWakeSets = 0, long controlWakeSets = 0, long[] represented = null)
        {
            this.inFlightByLevel = inFlightByLevel;
            this.consumed = consumed;
            this.dropped = dropped;
            WorkerInFlight = inFlight;
            this.storage = storage;
            this.committed = committed;
            this.represented = represented;
            this.bufferedDrops = bufferedDrops;
            CompletionKind = storage == null ? PipelineCompletionKind.SynchronousConsumedForTest : PipelineCompletionKind.BufferedCommitted;
            if (storage != null)
                foreach (int n in storage)
                    StorageInFlight += n;
            InFlight = inFlight + StorageInFlight;
            InFlightBytes = bytes;
            Faults = faults;
            BudgetOverruns = overruns;
            ProducerWakeSets = producerWakeSets;
            ControlWakeSets = controlWakeSets;
            TakeAttempts = takes;
            BudgetWaits = waits;
            foreach (long value in consumed)
                ConsumedForTest += value;
            foreach (long value in dropped)
                Dropped += value;
            if (bufferedDrops != null)
                foreach (long value in bufferedDrops)
                    Dropped += value;
        }
        public int GetInFlight(LogLevel level) => inFlightByLevel[(int)level] + (storage?[(int)level] ?? 0);
        public long GetConsumed(LogLevel level) => consumed[(int)level];
        public long GetDropped(LogLevel level, WorkerDropReason reason) => dropped[(int)level * 3 + (int)reason];
    }
    // 一个所有者线程、一个有界批次；不预取，也不拥有或释放队列。
    internal sealed partial class BackgroundLogWorker : IDisposable
    {
        internal bool IsWorkerThread => Thread.CurrentThread == thread;
        internal Func<bool> DrainRepeatTail;
        private const long TailRetryTicks = 10 * TimeSpan.TicksPerMillisecond;
        private long nextTerminalTail;
        internal Func<bool> DrainIdentityTails;
        internal Func<bool> DrainRunTails;
        internal IncidentInfoWindow IncidentWindow;
        internal Action<long> DrainIncident;
        private WorkerControlContext incidentTransferContext;
        private long runTailDue = long.MaxValue;
        internal void ScheduleRunTail(long now, bool fromWorker = false)
        {
            long due = AddSaturated(now, TimeSpan.TicksPerSecond);
            if (Interlocked.CompareExchange(ref runTailDue, due, long.MaxValue) == long.MaxValue
                && (!fromWorker || options.LegacyRunWorkerForBenchmark))
            {
                wake.Set();
#if LOGOS_WORKER_BENCHMARK
                Interlocked.Increment(ref runTailWakeSets);
#endif
            }
        }

        private int identityTailsChanged;
        private bool holdNewNormalUntilRetry;
        private long retryIdentityTicks = long.MaxValue;

        internal void MarkIdentityTails() => Volatile.Write(ref identityTailsChanged, 1);

        // 仅已注册的生产者调用，注销前句柄有效；切号收尾无需再取生命周期锁。
        internal void NotifyLateIdentityTail()
        {
            if (Interlocked.Exchange(ref identityTailsChanged, 1) == 0) wake.Set();
        }

        private int IdentityWait(int wait)
        {
            long due = DrainRunTails == null ? retryIdentityTicks : Math.Min(retryIdentityTicks, Interlocked.Read(ref runTailDue));
            if (IncidentWindow != null) due = Math.Min(due, IncidentWindow.NextDueTicks);
            if (due == long.MaxValue || stop.Requested) return wait;
            int retry = ToMilliseconds(Math.Max(0, due - clock.TimestampTicks));
            return wait == Timeout.Infinite ? retry : Math.Min(wait, retry);
        }

        // 仅固定工作线程调用，在关闭存储控制门之前提交剩余摘要。
        private bool TryDrainRepeatTail()
        {
            if (!stop.Requested || DrainRepeatTail == null || stop.ShouldCancel(clock.TimestampTicks))
                return false;
            long now = clock.TimestampTicks;
            if (now < nextTerminalTail) return false;
            MarkWorkerActivity();
            if (DrainRepeatTail()) nextTerminalTail = AddSaturated(now, TailRetryTicks);
            else DrainRepeatTail = null;
            long spent = Math.Max(0, clock.TimestampTicks - now);
#if LOGOS_WORKER_BENCHMARK
            Interlocked.Increment(ref stopSummaryScans);
            Interlocked.Add(ref stopSummaryScanTicks, spent);
#endif
            batchSpent = AddSaturated(batchSpent, spent);
            windowSpent = AddSaturated(windowSpent, spent);
            return true;
        }

        private readonly object lifecycle = new object();
        private readonly BoundedLogQueue queue;
        private readonly LoggingOptions options;
        private readonly ILogBatchProcessor processor;
        private readonly IMonotonicClock clock;
        private readonly AutoResetEvent wake, control;
        private readonly WorkerStopSignal stop = new WorkerStopSignal();
        private readonly Thread thread;
        private readonly LogRecord[] batch;
        private readonly long[] consumed = new long[6], dropped = new long[18];
        private readonly int[] inFlightByLevel = new int[6];
        internal bool HasFaulted => Interlocked.Read(ref faults) != 0;
        private bool started, disposed;
        private int offset, count, inFlight, inFlightBytes;
        private long faults, overruns, takeAttempts, budgetWaits;
        private int producerWakePending;
        private long producerWakeSets, controlWakeSets;
#if LOGOS_WORKER_BENCHMARK
        private long idleWaits, signaledWaits, emptyWakes, runTailWakeSets;
        private long liveSummaryScans, stopSummaryScans, liveSummaryScanTicks, stopSummaryScanTicks;
        private bool idleSignalWithoutWork;

        internal void CopyBenchmarkCounters(HealthSnapshot result)
        {
            result.WorkerIdleWaits = Interlocked.Read(ref idleWaits);
            result.WorkerSignaledWaits = Interlocked.Read(ref signaledWaits);
            result.WorkerEmptyWakes = Interlocked.Read(ref emptyWakes);
            result.RunTailWakeSets = Interlocked.Read(ref runTailWakeSets);
            result.LiveSummaryScans = Interlocked.Read(ref liveSummaryScans);
            result.StopSummaryScans = Interlocked.Read(ref stopSummaryScans);
            result.LiveSummaryScanTicks = Interlocked.Read(ref liveSummaryScanTicks);
            result.StopSummaryScanTicks = Interlocked.Read(ref stopSummaryScanTicks);
        }
#endif

        [System.Diagnostics.Conditional("LOGOS_WORKER_BENCHMARK")]
        private void MarkWorkerActivity()
        {
#if LOGOS_WORKER_BENCHMARK
            idleSignalWithoutWork = false;
#endif
        }

        private void WaitForInput(int milliseconds)
        {
#if LOGOS_WORKER_BENCHMARK
            if (idleSignalWithoutWork) Interlocked.Increment(ref emptyWakes);
            idleSignalWithoutWork = wake.WaitOne(milliseconds);
            Interlocked.Increment(ref idleWaits);
            if (idleSignalWithoutWork) Interlocked.Increment(ref signaledWaits);
#else
            wake.WaitOne(milliseconds);
#endif
        }
        // 仅供确定性竞态测试在空闲探测边界设置屏障；生产环境保持 null。
        internal Action<WorkerInputProbePhase> InputProbeForTest
        {
            get;
            set;
        }
        private long windowStart, windowSpent, batchSpent, cooldownUntil;
        public BackgroundLogWorker(BoundedLogQueue queue, LoggingOptions options, ILogBatchProcessor processor, IMonotonicClock clock = null)
        {
            this.queue = queue ?? throw new ArgumentNullException(nameof(queue));
            this.options = (options ?? throw new ArgumentNullException(nameof(options))).Freeze();
            this.processor = processor ?? throw new ArgumentNullException(nameof(processor));
            workerControl = processor as ILogWorkerControl;
            buffered = processor as IBufferedLogBatchProcessor;
            if (buffered != null)
                queue.EnableQuotaGate(processor is IStartupLogProcessor);
            this.clock = clock ?? new StopwatchClock();
            batch = new LogRecord[this.options.BatchCount];
            wake = new AutoResetEvent(false);
            control = new AutoResetEvent(false);
            thread = new Thread(Run) { IsBackground = true, Name = "Logos.Worker" };
        }
        public WorkerCompletion Completion
        {
            get
            {
                lock (lifecycle)
                {
                    if (!started)
                        return WorkerCompletion.NotStarted;
                    // 仅设置 finally 标记仍不够：此时托管调用栈帧可能仍在执行。
                    if (!thread.IsAlive)
                        return Interlocked.Read(ref faults) != 0 ? WorkerCompletion.Faulted : WorkerCompletion.Stopped;
                    if (!stop.Requested)
                        return WorkerCompletion.Running;
                    return clock.TimestampTicks >= stop.Deadline ? WorkerCompletion.StopTimedOut : WorkerCompletion.Stopping;
                }
            }
        }
        public void Start()
        {
            lock (lifecycle)
            {
                if (disposed)
                    throw new ObjectDisposedException(nameof(BackgroundLogWorker));
                if (started)
                    throw new InvalidOperationException("Worker can start only once.");
                thread.Start();
                started = true;
            }
        }
        // 生产者或流水线仅在提交成功之后、队列元数据锁之外调用。
        // Set 不执行处理器或用户回调。先取出再等待，因此合并或过期的信号是安全的。
        public void NotifyWork()
        {
            if (Interlocked.Exchange(ref producerWakePending, 1) != 0)
                return;
            lock (lifecycle)
            {
                if (!disposed)
                {
                    wake.Set();
                    Interlocked.Increment(ref producerWakeSets);
                }
            }
        }
        private bool PrepareInputProbe()
        {
            InputProbeForTest?.Invoke(WorkerInputProbePhase.BeforeReset);
            // 必须先清零再探测输入：此前提交由本轮探测发现，此后提交发布新的 event。
            // 不在空探测之后清零，也不 Reset event；过期信号至多导致一次额外空探测。
            Interlocked.Exchange(ref producerWakePending, 0);
            InputProbeForTest?.Invoke(WorkerInputProbePhase.AfterReset);
            if (IncidentWindow != null && !stop.Requested && IncidentWindow.NextDueTicks <= clock.TimestampTicks)
            {
                long begin = clock.TimestampTicks;
                // 一轮至多移交一个前缀。扫描、文本复制与后端脱敏共用本轮起始截止时间。
                incidentTransferContext = ControlContext(WorkerControlPhase.Running);
                long allowance = incidentTransferContext.RemainingBudgetTicks;
                try { DrainIncident(begin); }
                finally
                {
                    incidentTransferContext = default;
                    ChargeWork(begin, allowance);
                }
                if (batchSpent >= options.WorkerBudgetTicks || windowSpent >= options.WorkerWindowBudgetTicks) return false;
            }
            long due;
            if (DrainRunTails != null && !stop.Requested && (options.LegacyRunWorkerForBenchmark
                ? clock.TimestampTicks >= Interlocked.Read(ref runTailDue)
                : (due = Volatile.Read(ref runTailDue)) != long.MaxValue && clock.TimestampTicks >= due))
            {
                long start = clock.TimestampTicks;
                Interlocked.Exchange(ref runTailDue, long.MaxValue);
                MarkWorkerActivity();
                if (DrainRunTails()) ScheduleRunTail(start, true);
                long spent = Math.Max(0, clock.TimestampTicks - start);
#if LOGOS_WORKER_BENCHMARK
                Interlocked.Increment(ref liveSummaryScans);
                Interlocked.Add(ref liveSummaryScanTicks, spent);
#endif
                batchSpent = AddSaturated(batchSpent, spent);
                windowSpent = AddSaturated(windowSpent, spent);
                if (batchSpent >= options.WorkerBudgetTicks || windowSpent >= options.WorkerWindowBudgetTicks) return false;
            }
            if (!stop.Requested && (Volatile.Read(ref identityTailsChanged) != 0
                || (retryIdentityTicks != long.MaxValue && clock.TimestampTicks >= retryIdentityTicks)))
            {
                Interlocked.Exchange(ref identityTailsChanged, 0);
                long start = clock.TimestampTicks;
                bool firstScan = retryIdentityTicks == long.MaxValue;
                MarkWorkerActivity();
                retryIdentityTicks = DrainIdentityTails() ? AddSaturated(start, TailRetryTicks) : long.MaxValue;
                // 只为首次未排空的旧尾数留一个重试窗口，持续拒绝时恢复正常移交。
                holdNewNormalUntilRetry = firstScan && retryIdentityTicks != long.MaxValue;
                long spent = Math.Max(0, clock.TimestampTicks - start);
#if LOGOS_WORKER_BENCHMARK
                Interlocked.Increment(ref liveSummaryScans);
                Interlocked.Add(ref liveSummaryScanTicks, spent);
#endif
                batchSpent = AddSaturated(batchSpent, spent);
                windowSpent = AddSaturated(windowSpent, spent);
                return batchSpent < options.WorkerBudgetTicks && windowSpent < options.WorkerWindowBudgetTicks;
            }
            return true;
        }
        internal void NotifyControl()
        {
            lock (lifecycle)
            {
                if (!disposed)
                {
                    control.Set();
                    wake.Set();
                    Interlocked.Increment(ref controlWakeSets);
                }
            }
        }
        // 首次请求固定模式和绝对单调截止时间。后续请求只唤醒控制等待。
        public void RequestStop(WorkerStopMode mode, long deadlineTicks)
        {
            if (mode != WorkerStopMode.DrainUntilDeadline && mode != WorkerStopMode.DropPending)
                throw new ArgumentOutOfRangeException(nameof(mode));
            if (deadlineTicks < 0)
                throw new ArgumentOutOfRangeException(nameof(deadlineTicks));
            // 发布停止前先关闭准入，否则空队列的消费者可能在最后一次提交之前退出。
            queue.Close();
            stop.Request(mode, deadlineTicks);
            lock (lifecycle)
            {
                if (!disposed)
                {
                    control.Set();
                    wake.Set();
                    Interlocked.Increment(ref controlWakeSets);
                }
            }
        }
        public WorkerSnapshot Snapshot()
        {
            var c = new long[6];
            var d = new long[18];
            var f = new int[6];
            for (int i = 0; i < f.Length; i++)
                f[i] = Volatile.Read(ref inFlightByLevel[i]);
            for (int i = 0; i < c.Length; i++)
                c[i] = Interlocked.Read(ref consumed[i]);
            for (int i = 0; i < d.Length; i++)
                d[i] = Interlocked.Read(ref dropped[i]);
            return new WorkerSnapshot(c, d, Volatile.Read(ref inFlight), Volatile.Read(ref inFlightBytes),
                Interlocked.Read(ref faults), Interlocked.Read(ref overruns), Interlocked.Read(ref takeAttempts), Interlocked.Read(ref budgetWaits), f, buffered == null ? null : Copy(storageByLevel), buffered == null ? null : Copy(committed), buffered == null ? null : Copy(bufferedDrops), Interlocked.Read(ref producerWakeSets), Interlocked.Read(ref controlWakeSets), buffered == null ? null : Copy(represented));
        }
        private static int[] Copy(int[] source)
        {
            var copy = new int[source.Length];
            for (int i = 0; i < copy.Length; i++)
                copy[i] = Volatile.Read(ref source[i]);
            return copy;
        }
        private static long[] Copy(long[] source)
        {
            var copy = new long[source.Length];
            for (int i = 0; i < copy.Length; i++)
                copy[i] = Interlocked.Read(ref source[i]);
            return copy;
        }
        private void Run()
        {
            windowStart = clock.TimestampTicks;
            if (workerControl != null)
            {
                RunControlled();
                return;
            }
            try
            {
                while (true)
                {
                    if (stop.ShouldCancel(clock.TimestampTicks))
                    {
                        DropAll(WorkerDropReason.Shutdown);
                        return;
                    }
                    if (!AwaitBudget())
                    {
                        DropAll(WorkerDropReason.Shutdown);
                        return;
                    }
                    if (stop.Requested && TryDrainRepeatTail() &&
                        (batchSpent >= options.WorkerBudgetTicks || windowSpent >= options.WorkerWindowBudgetTicks)) continue;
                    if (count == 0)
                    {
                        if (!PrepareInputProbe()) continue;
                        if (!Take(out var decision))
                        {
                            if (decision == QueueTakeDecision.ContentionRejected)
                            {
                                control.WaitOne(2);
                                continue;
                            }
                            if (stop.Requested)
                            {
                                if (TryDrainRepeatTail()) continue;
                                if (DrainRepeatTail == null) return;
                                WaitForInput(ToMilliseconds(Math.Max(0, Math.Min(nextTerminalTail, stop.Deadline) - clock.TimestampTicks)));
                                continue;
                            }
                            InputProbeForTest?.Invoke(WorkerInputProbePhase.BeforeWait);
                            WaitForInput(IdentityWait(Timeout.Infinite));
                            continue;
                        }
                        MarkWorkerActivity();
                        batchSpent = 0;
                    }
                    // 在此观察到停止或丢弃截止时间后，不再开始用户调用。
                    if (stop.ShouldCancel(clock.TimestampTicks))
                        continue;
                    long begin = clock.TimestampTicks;
                    long allowance = Math.Min(options.WorkerBudgetTicks - batchSpent, options.WorkerWindowBudgetTicks - windowSpent);
                    long deadline = AddSaturated(begin, allowance);
                    if (stop.Requested)
                        deadline = Math.Min(deadline, stop.Deadline);
                    var context = new BatchProcessingContext(clock, stop, deadline);
                    BatchProcessResult result = processor.ProcessBatch(new LogBatchView(batch, offset, count), context);
                    long elapsed = Math.Max(0, clock.TimestampTicks - begin);
                    batchSpent = AddSaturated(batchSpent, elapsed);
                    windowSpent = AddSaturated(windowSpent, elapsed);
                    if (elapsed > allowance)
                    {
                        Interlocked.Increment(ref overruns);
                        // 按配置的工作占比补偿实际耗时，并向上取整到完整窗口。
                        // 单次调用超过整个窗口时，不得形成反复“工作加一个窗口”的忙循环。
                        long units = elapsed / options.WorkerWindowBudgetTicks + (elapsed % options.WorkerWindowBudgetTicks == 0 ? 0 : 1);
                        long period = MultiplySaturated(units, options.WorkerWindowTicks);
                        long cooldown = period == long.MaxValue ? long.MaxValue : Math.Max(options.WorkerWindowTicks, period - elapsed);
                        cooldownUntil = AddSaturated(clock.TimestampTicks, cooldown);
                        windowSpent = options.WorkerWindowBudgetTicks;
                    }
                    if (!Valid(result, context))
                        throw new InvalidOperationException("Processor violated bounded prefix protocol.");
                    int success = result.SucceededPrefix;
                    for (int i = 0; i < success; i++)
                        CompleteOne(null);
                    if (result.NextRecordFailed)
                        CompleteOne(WorkerDropReason.ProcessorRejected);
                    if (result.BudgetReason != BatchBudgetReason.None || batchSpent >= options.WorkerBudgetTicks)
                        windowSpent = options.WorkerWindowBudgetTicks;
                }
            }
            catch (Exception)
            {
                Interlocked.Increment(ref faults);
                queue.Close();
                DropAll(WorkerDropReason.ProcessorFault);
            }
        }
        private bool Valid(BatchProcessResult result, BatchProcessingContext context)
        {
            if (result.SucceededPrefix < 0 || result.SucceededPrefix > count)
                return false;
            if (result.BudgetReason < BatchBudgetReason.None || result.BudgetReason > BatchBudgetReason.CancellationRequested)
                return false;
            if (result.NextRecordFailed && (result.SucceededPrefix == count || result.BudgetReason != BatchBudgetReason.None))
                return false;
            if (result.BudgetReason == BatchBudgetReason.DeadlineReached && !context.DeadlineReached)
                return false;
            if (result.BudgetReason == BatchBudgetReason.CancellationRequested && !context.IsCancellationRequested)
                return false;
            return result.SucceededPrefix == count || result.NextRecordFailed || result.BudgetReason != BatchBudgetReason.None;
        }
        // 当前窗口耗尽时，即使有新的提交唤醒了线程，也要重新检查时间和剩余额度，避免积压输入绕过限额。
        private bool AwaitBudget()
        {
            bool counted = false;
            while (true)
            {
                long now = clock.TimestampTicks;
                if (stop.ShouldCancel(now))
                    return false;
                if (now >= cooldownUntil && now - windowStart >= options.WorkerWindowTicks)
                {
                    windowStart = now;
                    windowSpent = batchSpent = 0;
                }
                if (now >= cooldownUntil && windowSpent < options.WorkerWindowBudgetTicks && batchSpent < options.WorkerBudgetTicks)
                    return true;
                if (!counted)
                {
                    Interlocked.Increment(ref budgetWaits);
                    counted = true;
                }
                long remaining = now < cooldownUntil ? cooldownUntil - now : options.WorkerWindowTicks - Math.Max(0, now - windowStart);
                if (stop.Requested)
                    remaining = Math.Min(remaining, Math.Max(0, stop.Deadline - now));
                // 只有停止或控制信号能中断预算等待；生产者信号不能制造高频重试循环。
                control.WaitOne(ToMilliseconds(remaining));
            }
        }
        private bool Take(out QueueTakeDecision decision)
        {
            Interlocked.Increment(ref takeAttempts);
            bool taken = queue.TryTakeBatch(batch, out count, out int bytes, out decision);
            for (int i = 0; i < count; i++)
                Interlocked.Increment(ref inFlightByLevel[(int)batch[i].Level]);
            offset = 0;
            Volatile.Write(ref inFlightBytes, bytes);
            Volatile.Write(ref inFlight, count);
            return taken;
        }
        private void CompleteOne(WorkerDropReason? reason)
        {
            LogRecord record = batch[offset];
            if (reason.HasValue)
                Interlocked.Increment(ref dropped[(int)record.Level * 3 + (int)reason.Value]);
            else
                Interlocked.Increment(ref consumed[(int)record.Level]);
            Interlocked.Decrement(ref inFlightByLevel[(int)record.Level]);
            batch[offset++] = null;
            count--;
            Interlocked.Add(ref inFlightBytes, -(RecordMemoryCost.Of(record)));
            Volatile.Write(ref inFlight, count);
        }
        private void DropAll(WorkerDropReason reason)
        {
            while (true)
            {
                while (count > 0)
                    CompleteOne(reason);
                if (Take(out var decision))
                    continue;
                if (decision != QueueTakeDecision.ContentionRejected)
                    return;
                control.WaitOne(2);
            }
        }
        private static long MultiplySaturated(long value, long factor) => value > long.MaxValue / factor ? long.MaxValue : value * factor;
        private static long AddSaturated(long value, long extra) => extra > long.MaxValue - value ? long.MaxValue : value + extra;
        private static int ToMilliseconds(long ticks) => (int)Math.Max(1, Math.Min(100, ticks / TimeSpan.TicksPerMillisecond + (ticks % TimeSpan.TicksPerMillisecond == 0 ? 0 : 1)));
        public void Dispose()
        {
            lock (lifecycle)
            {
                if (disposed)
                    return;
                if (started && thread.IsAlive)
                    throw new InvalidOperationException("Worker thread has not terminated; borrowed buffers and handles remain owned.");
                disposed = true;
                wake.Dispose();
                control.Dispose();
            }
        }
    }
}
