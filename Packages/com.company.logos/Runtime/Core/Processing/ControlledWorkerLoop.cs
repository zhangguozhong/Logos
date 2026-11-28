using System;
using System.Threading;
namespace Logos
{
    internal sealed partial class BackgroundLogWorker
    {
        private readonly ILogWorkerControl workerControl;
        private readonly IBufferedLogBatchProcessor buffered;
        private readonly int[] storageByLevel = new int[6];
        private readonly long[] represented = new long[6];
        private readonly long[] committed = new long[6], bufferedDrops = new long[6 * HealthCounters.ReasonCount];
        private ulong lastOutcome, lastAcknowledged;
        private int storageInFlight;
        private long nextControlAttempt;
        private LogClassMask blockedClasses;
        private BoundedLogQueue.HeadLease pin;
        private bool hasPin;
        internal ProducerRegistry Producers
        {
            get;
            set;
        }
        private WorkerControlContext ControlContext(WorkerControlPhase phase)
        {
            long now = clock.TimestampTicks;
            long remaining = Math.Max(0, Math.Min(options.WorkerBudgetTicks - batchSpent, options.WorkerWindowBudgetTicks - windowSpent));
            return new WorkerControlContext(clock, stop, AddSaturated(now, remaining), phase, queue);
        }
        private void ChargeWork(long begin, long allowance)
        {
            long elapsed = Math.Max(0, clock.TimestampTicks - begin);
            batchSpent = AddSaturated(batchSpent, elapsed);
            windowSpent = AddSaturated(windowSpent, elapsed);
            if (elapsed <= allowance)
                return;
            Interlocked.Increment(ref overruns);
            long units = elapsed / options.WorkerWindowBudgetTicks + (elapsed % options.WorkerWindowBudgetTicks == 0 ? 0 : 1);
            long period = MultiplySaturated(units, options.WorkerWindowTicks);
            long cooldown = period == long.MaxValue ? long.MaxValue : Math.Max(options.WorkerWindowTicks, period - elapsed);
            cooldownUntil = AddSaturated(clock.TimestampTicks, cooldown);
            windowSpent = options.WorkerWindowBudgetTicks;
        }
        private WorkerControlResult StepControl(WorkerControlPhase phase)
        {
            long begin = clock.TimestampTicks;
            var context = ControlContext(phase);
            long allowance = context.RemainingBudgetTicks;
            try
            {
                WorkerControlResult result = workerControl.Step(context);
                if (result.Progressed)
                    blockedClasses = LogClassMask.None;
                nextControlAttempt = AddSaturated(clock.TimestampTicks, TimeSpan.TicksPerMillisecond * 2);
                return result;
            }
            finally
            {
                ChargeWork(begin, allowance);
            }
        }
        private void RunControlled()
        {
            WorkerFinalizationReason reason = WorkerFinalizationReason.Drained;
            bool startup = true, drainComplete = false, stoppingStep = false;
            try
            {
                while (true)
                {
                    DrainOutcomes();
                    if (stop.ShouldCancel(clock.TimestampTicks))
                    {
                        reason = stop.Kind == WorkerStopKind.DropPending ? WorkerFinalizationReason.DropRequested : WorkerFinalizationReason.StopDeadline;
                        break;
                    }
                    if (!AwaitBudget())
                    {
                        reason = stop.Kind == WorkerStopKind.DropPending ? WorkerFinalizationReason.DropRequested : WorkerFinalizationReason.StopDeadline;
                        break;
                    }
                    long now = clock.TimestampTicks;
                    if (!startup && stop.Requested && TryDrainRepeatTail() &&
                        (batchSpent >= options.WorkerBudgetTicks || windowSpent >= options.WorkerWindowBudgetTicks)) continue;
                    // 等待摘要额度期间继续移交队列、处理存储控制；停止阶段会关闭准入门。
                    bool finish = stop.Requested && DrainRepeatTail == null;
                    if (startup || (finish && !stoppingStep) || (now >= nextControlAttempt && (finish || now >= workerControl.NextWakeTicks)))
                    {
                        MarkWorkerActivity();
                        var phase = startup ? WorkerControlPhase.Startup : finish ? WorkerControlPhase.Stopping : queue.IsDayTransition ? WorkerControlPhase.DayTransition : WorkerControlPhase.Running;
                        var step = StepControl(phase);
                        if (phase == WorkerControlPhase.Stopping)
                        {
                            stoppingStep = true;
                            drainComplete = step.DrainComplete;
                        }
                        startup = false;
                        DrainOutcomes();
                        if (stop.ShouldCancel(clock.TimestampTicks))
                            continue;
                        if (batchSpent >= options.WorkerBudgetTicks || windowSpent >= options.WorkerWindowBudgetTicks)
                            continue;
                    }
                    if (!PrepareInputProbe()) continue;
                    bool contention;
                    bool attempted = buffered != null ? TransferOne(out contention) : ProcessControlledSynchronous(out contention);
                    if (attempted)
                    {
                        MarkWorkerActivity();
                        // 新取得的存储所有权会使先前“排空完成”的观察失效。
                        if (stop.Requested)
                            drainComplete = false;
                        continue;
                    }
                    if (contention)
                    {
                        control.WaitOne(2);
                        continue;
                    }
                    // 就绪提示以及固定或归还操作可能在没有转移记录的情况下耗尽额度。
                    // 先恢复已消耗的预算，再考虑进入可能没有后续唤醒的空闲等待。
                    if (batchSpent >= options.WorkerBudgetTicks || windowSpent >= options.WorkerWindowBudgetTicks)
                        continue;
                    if (stop.Requested && queue.Snapshot().QueuedCount == 0 && stoppingStep && drainComplete)
                        break;
                    WaitControlled();
                }
            }
            catch (Exception)
            {
                Interlocked.Increment(ref faults);
                queue.Close();
                reason = WorkerFinalizationReason.ProcessorFault;
            }
            // 故障退出也作废未交付的原文，不能依赖宿主之后一定会调用 Stop/Dispose。
            IncidentWindow?.Reset(null);
            // 到达最终清理阶段前同步调用已返回。不在主线程释放资源。
            if (hasPin)
                ResolveCurrentPin(true, AdmissionReason.Faulted);
            if (buffered == null)
                DropAll(reason == WorkerFinalizationReason.ProcessorFault ? WorkerDropReason.ProcessorFault : WorkerDropReason.Shutdown);
            else
                DropBufferedQueue(reason == WorkerFinalizationReason.ProcessorFault ? AdmissionReason.Faulted : AdmissionReason.ShutdownDropped);
            // 生产者可能仍在复制已预留记录。其注册和预留必须先归还，
            // 后端才能释放所有者或密钥。注册表读取不获取流水线生命周期锁。
            while ((Producers != null && Producers.Count != 0) || queue.Snapshot().ReservedCount != 0)
                control.WaitOne(2);
            try
            {
                DrainOutcomes();
            }
            catch (Exception)
            {
                Interlocked.Increment(ref faults);
                reason = WorkerFinalizationReason.ProcessorFault;
            }
            try
            {
                workerControl.CompletePending(reason);
            }
            catch (Exception)
            {
                Interlocked.Increment(ref faults);
            }
            // CompletePending 按契约不得抛异常，并发布所有尚未完成的已转移项目。
            // 若故障后端尚未归还所有权，则继续保留工作线程和资源。
            while (true)
            {
                try
                {
                    DrainOutcomes();
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref faults);
                }
                if (Volatile.Read(ref storageInFlight) == 0)
                    break;
                control.WaitOne(2);
            }
            int retryMilliseconds = 2;
            while (true)
            {
                try
                {
                    workerControl.Dispose();
                    break;
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref faults);
                    control.WaitOne(retryMilliseconds);
                    retryMilliseconds = Math.Min(100, retryMilliseconds * 2);
                }
            }
        }
        internal long GetBufferedDiscarded(LogLevel level, AdmissionReason reason) => Interlocked.Read(ref bufferedDrops[(int)level * HealthCounters.ReasonCount + (int)reason]);
        private bool ProcessControlledSynchronous(out bool contention)
        {
            contention = false;
            if (count == 0 && !Take(out var decision))
            {
                contention = decision == QueueTakeDecision.ContentionRejected;
                return false;
            }
            long begin = clock.TimestampTicks;
            var c = ControlContext(stop.Requested ? WorkerControlPhase.Stopping : WorkerControlPhase.Running);
            var context = new BatchProcessingContext(clock, stop, c.DeadlineTicks);
            long allowance = c.RemainingBudgetTicks;
            BatchProcessResult result;
            try
            {
                result = processor.ProcessBatch(new LogBatchView(batch, offset, count), context);
            }
            finally
            {
                ChargeWork(begin, allowance);
            }
            if (!Valid(result, context))
                throw new InvalidOperationException("Processor violated bounded prefix protocol.");
            for (int i = 0; i < result.SucceededPrefix; i++)
                CompleteOne(null);
            if (result.NextRecordFailed)
                CompleteOne(WorkerDropReason.ProcessorRejected);
            if (result.BudgetReason != BatchBudgetReason.None)
                windowSpent = options.WorkerWindowBudgetTicks;
            return true;
        }
        // 固定 worker 同步移交，沿用正常存储脱敏和配额。原始前缀不会进入普通队列等待后续消费。
        internal bool CanTransferIncidentNow => !stop.Requested && !incidentTransferContext.DeadlineReached
            && (buffered.ReadyClassMask & LogClassMask.Normal) != 0;
        internal BatchTransferResult TransferIncidentRecord(LogRecord record)
        {
            var context = incidentTransferContext;
            if (stop.Requested || context.IsCancellationRequested) return new BatchTransferResult(0, true, AdmissionReason.Stopping);
            if (context.DeadlineReached) return new BatchTransferResult(0, pauseReason: TransferPauseReason.Budget);
            if ((buffered.ReadyClassMask & LogClassMask.Normal) == 0)
                return new BatchTransferResult(0, pauseReason: TransferPauseReason.Backpressure);
            if (context.DeadlineReached) return new BatchTransferResult(0, pauseReason: TransferPauseReason.Budget);
            batch[0] = record;
            try
            {
                var result = buffered.TransferBatch(new LogBatchView(batch, 0, 1), context);
                if (!ValidTransfer(result, context))
                    throw new InvalidOperationException("Invalid incident transfer result.");
                if (result.TransferredPrefix == 1)
                {
                    Interlocked.Increment(ref storageByLevel[(int)LogLevel.Info]);
                    Interlocked.Increment(ref storageInFlight);
                }
                return result;
            }
            finally { batch[0] = null; }
        }

        private bool TransferOne(out bool contention)
        {
            contention = false;
            long begin = clock.TimestampTicks;
            var context = ControlContext(stop.Requested ? WorkerControlPhase.Stopping : WorkerControlPhase.Running);
            long allowance = context.RemainingBudgetTicks;
            try
            {
                Interlocked.Increment(ref takeAttempts);
                // 唤醒标记已在探测前清零。之后的提交会发出信号，空队列无需查询后端或争用队列锁。
                if (!queue.HasQueuedInput)
                    return false;
                bool holdNewNormal = holdNewNormalUntilRetry && !stop.Requested && begin < retryIdentityTicks;
                if (!queue.TryPinHead(buffered.ReadyClassMask & ~blockedClasses, out pin, out var decision, holdNewNormal, resolveStartup: true))
                {
                    contention = decision == QueueTakeDecision.ContentionRejected;
                    return false;
                }
                hasPin = true;
                LogRecord record = pin.Record;
                batch[0] = record;
                Volatile.Write(ref inFlight, 1);
                Volatile.Write(ref inFlightBytes, RecordMemoryCost.Of(record));
                Interlocked.Increment(ref inFlightByLevel[(int)record.Level]);
                if (context.IsCancellationRequested || context.DeadlineReached)
                {
                    ResolveCurrentPin(false, null);
                    return false;
                }
                if (pin.StartupFailure != AdmissionReason.Accepted)
                {
                    ResolveCurrentPin(true, pin.StartupFailure);
                    return true;
                }
                BatchTransferResult result = buffered.TransferBatch(new LogBatchView(batch, 0, 1), context);
                if (!ValidTransfer(result, context))
                    throw new InvalidOperationException("Processor violated transfer protocol.");
                if (result.TransferredPrefix == 1)
                {
                    // 先登记所有权，再清除工作线程借用或归还队列容量。
                    Interlocked.Increment(ref storageByLevel[(int)record.Level]);
                    Interlocked.Increment(ref storageInFlight);
                    ResolveCurrentPin(true, null);
                    return true;
                }
                if (result.NextRecordFailed)
                {
                    ResolveCurrentPin(true, result.FailureReason);
                    return true;
                }
                if (result.PauseReason == TransferPauseReason.Backpressure)
                    blockedClasses |= record.Level >= LogLevel.Error ? LogClassMask.ErrorFatal : LogClassMask.Normal;
                else if (result.PauseReason == TransferPauseReason.Budget)
                    windowSpent = options.WorkerWindowBudgetTicks;
                ResolveCurrentPin(false, null);
                return true;
            }
            finally
            {
                ChargeWork(begin, allowance);
            }
        }
        // 每次仅借出一个队首。报告成功、已知失败或暂缓必须互斥；若以预算或取消为由中断，还要与实时上下文核对。
        private static bool ValidTransfer(BatchTransferResult r, WorkerControlContext c)
        {
            if (r.TransferredPrefix < 0 || r.TransferredPrefix > 1 || r.PauseReason < TransferPauseReason.None || r.PauseReason > TransferPauseReason.Cancel)
                return false;
            if (r.TransferredPrefix == 1)
                return !r.NextRecordFailed && r.PauseReason == TransferPauseReason.None;
            if (r.NextRecordFailed)
                return r.PauseReason == TransferPauseReason.None && r.FailureReason != AdmissionReason.Accepted && (uint)r.FailureReason < HealthCounters.ReasonCount;
            return r.PauseReason == TransferPauseReason.Backpressure || (r.PauseReason == TransferPauseReason.Budget && c.DeadlineReached) || (r.PauseReason == TransferPauseReason.Cancel && c.IsCancellationRequested);
        }
        private void ResolveCurrentPin(bool remove, AdmissionReason? reason)
        {
            LogRecord record = pin.Record;
            if (reason.HasValue)
                Interlocked.Increment(ref bufferedDrops[(int)record.Level * HealthCounters.ReasonCount + (int)reason.Value]);
            if (!queue.ResolvePin(pin, remove, remove && !reason.HasValue))
                throw new InvalidOperationException("Lost worker pin identity.");
            Interlocked.Decrement(ref inFlightByLevel[(int)record.Level]);
            batch[0] = null;
            pin = default;
            hasPin = false;
            Volatile.Write(ref inFlight, 0);
            Volatile.Write(ref inFlightBytes, 0);
        }
        private void DropBufferedQueue(AdmissionReason reason)
        {
            while (true)
            {
                if (!queue.TryPinHead(LogClassMask.All, out pin, out var decision))
                {
                    if (decision == QueueTakeDecision.ContentionRejected)
                    {
                        control.WaitOne(2);
                        continue;
                    }
                    return;
                }
                hasPin = true;
                Interlocked.Increment(ref inFlightByLevel[(int)pin.Record.Level]);
                ResolveCurrentPin(true, reason);
            }
        }
        private void DrainOutcomes()
        {
            if (buffered == null)
                return;
            while (buffered.TryPeekOutcome(out var outcome))
            {
                MarkWorkerActivity();
                if (outcome.Sequence == 0 || outcome.Sequence < lastOutcome || outcome.Sequence <= lastAcknowledged)
                    throw new InvalidOperationException("Nonmonotonic outcome.");
                // 结果记账和后端确认分开。若 Ack 失败后再次窥见同一序号，只补确认，不能再次扣减在途数或累加已提交数。
                if (outcome.Sequence != lastOutcome)
                {
                    if (outcome.Kind < LogStorageOutcomeKind.Committed || outcome.Kind > LogStorageOutcomeKind.RepresentedBySummary || (uint)outcome.Reason >= HealthCounters.ReasonCount || (outcome.Kind == LogStorageOutcomeKind.Dropped && outcome.Reason == AdmissionReason.Accepted) || (outcome.Kind != LogStorageOutcomeKind.Dropped && outcome.Reason != AdmissionReason.Accepted))
                        throw new InvalidOperationException("Invalid outcome.");
                    for (int i = 0; i < 6; i++)
                        if (outcome.Counts.Get((LogLevel)i) > Volatile.Read(ref storageByLevel[i]))
                            throw new InvalidOperationException("Outcome exceeds transferred ownership.");
                    for (int i = 0; i < 6; i++)
                    {
                        int value = (int)outcome.Counts.Get((LogLevel)i);
                        if (outcome.Kind == LogStorageOutcomeKind.Committed)
                            Interlocked.Add(ref committed[i], value);
                        else if (outcome.Kind == LogStorageOutcomeKind.RepresentedBySummary)
                            Interlocked.Add(ref represented[i], value);
                        else
                            Interlocked.Add(ref bufferedDrops[i * HealthCounters.ReasonCount + (int)outcome.Reason], value);
                        Interlocked.Add(ref storageByLevel[i], -value);
                        Interlocked.Add(ref storageInFlight, -value);
                    }
                    lastOutcome = outcome.Sequence;
                }
                if (!buffered.AckOutcome(outcome.Sequence))
                    throw new InvalidOperationException("Outcome acknowledgement rejected.");
                lastAcknowledged = outcome.Sequence;
            }
        }
        private void WaitControlled()
        {
            long now = clock.TimestampTicks;
            long next = Math.Max(nextControlAttempt, workerControl.NextWakeTicks);
            if (stop.Requested)
                next = Math.Min(stop.Deadline, Math.Min(next, DrainRepeatTail == null ? nextControlAttempt : nextTerminalTail));
            int wait = next == long.MaxValue ? Timeout.Infinite : ToMilliseconds(Math.Max(0, next - now));
            InputProbeForTest?.Invoke(WorkerInputProbePhase.BeforeWait);
            WaitForInput(IdentityWait(wait));
        }
    }
}
