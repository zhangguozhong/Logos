using System;
using System.Threading;
namespace Logos.Storage
{
    // 每次工作步在暂存、封块、追加和结果发布之间推进；返回的唤醒时刻要兼顾令牌补充及维护，防止无法写入时空转。
    public sealed partial class StorageBatchProcessor
    {
        public WorkerControlResult Step(WorkerControlContext context)
        {
            CheckWorker();
            admission = context.Admission;
            if (completed || context.IsCancellationRequested || context.DeadlineReached)
            {
                RefreshDiagnosticBudget(context);
                return new WorkerControlResult(false);
            }
            long now = context.NowTicks;
            lastNow = now;
            DiagnosticControlBoundaryChanged();
            if (!barrierAttached)
            {
                journal.SetClearMemoryBarrier(this);
                barrierAttached = true;
            }
            if (!initialized)
            {
                rate = new WriteRateLimiter(now);
                initialized = true;
                nextObservation = now;
                nextJournal = now;
            }
            ConsumeLifecycleSignals(now);
            if (now >= nextObservation)
            {
                observation = authority.Observe();
                journal.ObserveTime(observation);
                nextObservation = Add(now, StorageRuntimeLimits.ControlIntervalTicks);
                if (activeDay >= 0 && journal.TimeDecision.EffectiveDay > activeDay && !journal.TimeDecision.RequiresConfirmation && !transitioning)
                {
                    aggregation?.Freeze();
                    transitioning = true;
                    transitionExpired = false;
                    transitionDeadline = Add(now, TimeSpan.TicksPerSecond);
                }
            }
            if (context.IsCancellationRequested || context.DeadlineReached)
                return FinishStep(context, false);
            if (transitioning)
            {
                PublishGate();
                transitionExpired = now >= transitionDeadline;
                if (transitionExpired)
                {
                    if (!normal.IsEmpty && !normal.HasOutcome)
                    {
                        Publish(normal, AdmissionReason.DayRolloverDropped);
                        return FinishStep(context, true);
                    }
                    if (!error.IsEmpty && !error.HasOutcome)
                    {
                        Publish(error, AdmissionReason.DayRolloverDropped);
                        return FinishStep(context, true);
                    }
                    DropDiagnostic(AdmissionReason.DayRolloverDropped);
                    if (!normal.IsEmpty || !error.IsEmpty || DiagnosticOwned)
                        return FinishStep(context, false);
                    QueueDayDropResult result = admission.DropOneQueuedDay(activeDay, LogClassMask.All, AdmissionReason.DayRolloverDropped);
                    if (result != QueueDayDropResult.NoMatchingHead)
                        return FinishStep(context, result == QueueDayDropResult.Dropped);
                }
                else
                {
                    if (ProcessOneSlot(context, BlockSealReason.DayTransition))
                        return FinishStep(context, true);
                    if (!normal.IsEmpty || !error.IsEmpty || DiagnosticOwned || context.HasQueuedInput)
                        return FinishStep(context, false);
                }
                // 安全发布前，两个暂存结果都已确认，且不再保留旧输入。
                journal.Step(JournalStepContext.FromWorker(context));
                nextJournal = Add(now, TimeSpan.TicksPerMillisecond * 2);
                SynchronizeReady();
                PublishGate();
                return FinishStep(context, true);
            }
            if (ProcessClearRequest())
                return FinishStep(context, true);
            if (journal.Clear.IsActive)
            {
                clearWorkTurn = !clearWorkTurn;
                if (clearWorkTurn && clearTargets != null && !clearTargets.Contains(activeDay) && ProcessOneSlot(context, context.Phase == WorkerControlPhase.Stopping ? BlockSealReason.Stop : BlockSealReason.None))
                    return FinishStep(context, true);
                journal.StepClear(JournalStepContext.FromWorker(context));
                SynchronizeReady();
                PublishGate();
                return FinishStep(context, true);
            }
            if (journal.Status == JournalStatus.Ready)
            {
                if ((normalRetry != 0 && now >= normalRetry) || (errorRetry != 0 && now >= errorRetry))
                {
                    if (normalRetry != 0 && now >= normalRetry)
                    {
                        normalRetry = 0;
                        normalBlocked = AdmissionReason.Accepted;
                    }
                    if (errorRetry != 0 && now >= errorRetry)
                    {
                        errorRetry = 0;
                        errorBlocked = AdmissionReason.Accepted;
                    }
                    journal.RequestCapacityProbe();
                    PublishGate();
                    return FinishStep(context, true);
                }
                SynchronizeReady();
                PublishGate();
                MergeDiagnostics(now);
                if (preferDailyDrop && DropOneDailyClosedHead(context))
                {
                    preferDailyDrop = false;
                    return FinishStep(context, true);
                }
                if (ProcessOneSlot(context, context.Phase == WorkerControlPhase.Stopping ? BlockSealReason.Stop : BlockSealReason.None))
                {
                    preferDailyDrop = true;
                    return FinishStep(context, true);
                }
                if (DropOneDailyClosedHead(context))
                {
                    preferDailyDrop = false;
                    return FinishStep(context, true);
                }
                if (context.Phase == WorkerControlPhase.Stopping && !context.HasQueuedInput && normal.IsEmpty && error.IsEmpty && !DiagnosticOwned)
                {
                    if (!stopSyncComplete)
                    {
                        stopSyncComplete = journal.ForceSyncUnit(ref stopSyncPhase, JournalStepContext.FromWorker(context));
                        return FinishStep(context, true);
                    }
                    return FinishStep(context, false, true);
                }
            }
            bool drive = journal.Status == JournalStatus.New || journal.Status == JournalStatus.Recovering || now >= nextJournal;
            if (drive)
            {
                bool retried = false;
                if (journal.Status == JournalStatus.Blocked && now >= nextProbe)
                {
                    retried = journal.RequestRecoveryRetry();
                    nextProbe = Add(now, StorageRuntimeLimits.MaintenanceIntervalTicks);
                }
                JournalStatus before = journal.Status;
                journal.Step(JournalStepContext.FromWorker(context));
                if (journal.Status == JournalStatus.Blocked && (before != JournalStatus.Blocked || retried))
                    nextProbe = Add(now, StorageRuntimeLimits.MaintenanceIntervalTicks);
                nextJournal = Add(now, journal.Status == JournalStatus.Recovering ? TimeSpan.TicksPerMillisecond * 2 : StorageRuntimeLimits.ControlIntervalTicks);
                SynchronizeReady();
                PublishGate();
                return FinishStep(context, true);
            }
            PublishGate();
            return FinishStep(context, false);
        }
        private void SynchronizeReady()
        {
            if (journal.Status != JournalStatus.Ready)
                return;
            long day = journal.CurrentEffectiveDay.Value;
            if (DiagnosticOwned && (day != activeDay || !ReferenceEquals(encodingHeader, journal.CurrentHeader)))
                return;
            if (day != activeDay)
            {
                activeDay = day;
                normalBlocked = errorBlocked = AdmissionReason.Accepted;
                normalRetry = errorRetry = 0;
                transitioning = transitionExpired = false;
                dailyDropPending = false;
            }
            if (!ReferenceEquals(encodingHeader, journal.CurrentHeader))
            {
                encodingKey?.Dispose();
                encodingKey = null;
                encodingHeader = journal.CurrentHeader;
                if (encodingHeader != null)
                    encodingKey = JournalIo.Acquire(keys, encodingHeader.KeyId);
            }
        }
        private bool ProcessOneSlot(WorkerControlContext context, BlockSealReason forceReason)
        {
            if (journal.Status != JournalStatus.Ready || encodingKey == null)
                return false;
            if (journal.DirectoryInventoryInProgress)
            {
                normal.PauseWriteWait(context.NowTicks);
                error.PauseWriteWait(context.NowTicks);
                journal.StepDirectoryInventory(JournalStepContext.FromWorker(context));
                if (!journal.DirectoryInventoryInProgress)
                {
                    normal.ResumeWriteWait(context.NowTicks);
                    error.ResumeWriteWait(context.NowTicks);
                }
                return true; // 在已有工作线程和控制额度内，恰好执行一个有界清点单元。
            }
            normal.ResumeWriteWait(context.NowTicks);
            error.ResumeWriteWait(context.NowTicks);
            bool freezeDiagnostic = forceReason == BlockSealReason.Stop || forceReason == BlockSealReason.DayTransition;
            if (freezeDiagnostic)
                aggregation?.Freeze();
            bool first = nextClassError;
            if (ProcessSlot(first ? error : normal, first, context, forceReason))
            {
                nextClassError = !first;
                return true;
            }
            if (ProcessSlot(first ? normal : error, !first, context, forceReason))
            {
                nextClassError = first;
                return true;
            }
            return DriveDiagnostic(context.NowTicks, freezeDiagnostic);
        }
        private bool ProcessSlot(BoundedBlockStaging staging, bool isError, WorkerControlContext context, BlockSealReason forceReason)
        {
            if (staging.IsEmpty || staging.HasOutcome || context.IsCancellationRequested || context.DeadlineReached)
                return false;
            long now = context.NowTicks;
            if (!staging.IsSealed)
            {
                if (forceReason == BlockSealReason.None)
                {
                    if (!staging.IsSealDue(now))
                        return false;
                }
                else
                    staging.RequestSeal(forceReason);
                if (!staging.TrySeal(encodingHeader, encodingKey, Add(now, StorageRuntimeLimits.WriteWaitTicks), journal.WriteGeneration, out _))
                    Publish(staging, AdmissionReason.EncodingFailed);
                return true;
            }
            if (staging.IsSynthetic && (diagnosticAttempts >= 256 || diagnosticAttemptBytes > 1024 * 1024 - staging.SealedLength
                || !journal.CanAppendDiagnostic(isError, staging.SealedLength, activeDay, journal.WriteGeneration)))
            {
                if (diagnosticBudgetRejections < long.MaxValue)
                    diagnosticBudgetRejections++;
                Publish(staging, AdmissionReason.StorageLimit);
                return true;
            }
            var preflight = journal.PreflightOwnedAppend(isError, staging.SealedLength, activeDay, journal.WriteGeneration);
            if (preflight != JournalAppendResult.Committed)
            {
                ResolveAppend(staging, isError, preflight, now);
                return true;
            }
            if (now >= staging.PendingDeadlineTicks)
            {
                Publish(staging, AdmissionReason.WriteRateLimited);
                return true;
            }
            // 这是扣款前最后一次实时检查。成功取得额度后直接进入
            // 实际追加尝试，因此取消不会消耗未使用的额度。
            if (context.IsCancellationRequested || context.DeadlineReached)
                return false;
            if (!rate.TryAcquire(isError, staging.SealedLength, now, out _))
            {
                if (now >= staging.PendingDeadlineTicks)
                {
                    Publish(staging, AdmissionReason.WriteRateLimited);
                    return true;
                }
                return false;
            }
            if (staging.IsSynthetic)
            {
                diagnosticAttempts++;
                diagnosticAttemptBytes += staging.SealedLength;
            }
            if (isError)
                appendUncertainError = true;
            else
                appendUncertainNormal = true;
            // 故障逸出时保留此标记，使 CompletePending 不能把追加报告为确定失败。
            var result = journal.TryAppend(staging.SealedLease);
            if (isError)
                appendUncertainError = false;
            else
                appendUncertainNormal = false;
            ResolveAppend(staging, isError, result, now);
            return true;
        }
        private void ResolveAppend(BoundedBlockStaging staging, bool isError, JournalAppendResult result, long now)
        {
            AdmissionReason reason;
            switch (result)
            {
                case JournalAppendResult.Committed:
                    Publish(staging, AdmissionReason.Accepted, true);
                    return;
                case JournalAppendResult.QuotaClosed:
                    reason = isError ? AdmissionReason.ErrorDailyLimit : AdmissionReason.NormalDailyLimit;
                    break;
                case JournalAppendResult.DirectoryLimit:
                    reason = AdmissionReason.DirectoryLimit;
                    break;
                case JournalAppendResult.MetadataLimit:
                    reason = AdmissionReason.MetadataLimit;
                    break;
                case JournalAppendResult.StaleDay:
                    reason = AdmissionReason.StaleQuotaDay;
                    break;
                case JournalAppendResult.Uncertain:
                    reason = AdmissionReason.UncertainIo;
                    break;
                case JournalAppendResult.InvalidBlock:
                    reason = AdmissionReason.EncodingFailed;
                    break;
                default:
                    reason = AdmissionReason.StorageFault;
                    break;
            }
            Publish(staging, reason);
            if (reason == AdmissionReason.NormalDailyLimit || reason == AdmissionReason.ErrorDailyLimit)
            {
                if (isError)
                    errorBlocked = reason;
                else
                    normalBlocked = reason;
                dailyDropPending = true;
            }
            else if (reason == AdmissionReason.DirectoryLimit || reason == AdmissionReason.MetadataLimit || reason == AdmissionReason.StorageFault || reason == AdmissionReason.UncertainIo)
            {
                if (isError)
                {
                    errorBlocked = reason;
                    if (errorRetry == 0)
                        errorRetry = Add(now, StorageRuntimeLimits.MaintenanceIntervalTicks);
                }
                else
                {
                    normalBlocked = reason;
                    if (normalRetry == 0)
                        normalRetry = Add(now, StorageRuntimeLimits.MaintenanceIntervalTicks);
                }
            }
        }
        private void PublishGate()
        {
            if (admission == null)
                return;
            long day = Math.Max(0, journal.CurrentEffectiveDay ?? activeDay);
            AdmissionReason n, e;
            QuotaAdmissionState state;
            if (transitioning)
            {
                state = QuotaAdmissionState.DayTransition;
                n = e = AdmissionReason.DayTransition;
            }
            else if (journal.CurrentEffectiveDay.HasValue && observation.UtcNow.Ticks / TimeSpan.TicksPerDay != journal.CurrentEffectiveDay.Value)
            {
                state = QuotaAdmissionState.Closed;
                n = e = journal.TimeDecision.Kind == JournalTimeDecisionKind.TimeUncertain ? AdmissionReason.TimeUncertain : AdmissionReason.TimeConfirmationRequired;
            }
            else if (journal.Status != JournalStatus.Ready)
            {
                state = QuotaAdmissionState.Recovering;
                n = e = journal.Status == JournalStatus.New ? AdmissionReason.NotReady : AdmissionReason.Recovering;
            }
            else
            {
                state = QuotaAdmissionState.Ready;
                n = journal.NormalClosed ? AdmissionReason.NormalDailyLimit : normalBlocked;
                e = journal.ErrorClosed ? AdmissionReason.ErrorDailyLimit : errorBlocked;
                if ((clearTargets != null && clearTargets.Contains(day)) || (journal.Status == JournalStatus.Ready && journal.CurrentHeader == null))
                    n = e = AdmissionReason.Cleared;
            }
            publishedQuotaTag = admission.SetGate(new QuotaAdmissionGate(day, state, n, e));
            if (!budgetTag.Equals(publishedQuotaTag))
                InvalidateDiagnosticBudget();
        }
        private bool ClassCanReceive(bool isError)
        {
            return isError
                ? !journal.ErrorClosed && errorBlocked == AdmissionReason.Accepted
                : !journal.NormalClosed && normalBlocked == AdmissionReason.Accepted;
        }
        private bool dailyDropPending, preferDailyDrop, dropErrorFirst;
        private bool DropOneDailyClosedHead(WorkerControlContext context)
        {
            if (!dailyDropPending || admission == null || context.IsCancellationRequested || context.DeadlineReached)
                return false;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                bool isError = attempt == 0 ? dropErrorFirst : !dropErrorFirst;
                bool closed = isError ? journal.ErrorClosed || errorBlocked == AdmissionReason.ErrorDailyLimit
                    : journal.NormalClosed || normalBlocked == AdmissionReason.NormalDailyLimit;
                if (!closed)
                    continue;
                var reason = isError ? AdmissionReason.ErrorDailyLimit : AdmissionReason.NormalDailyLimit;
                var result = admission.DropOneQueuedDay(activeDay, isError ? LogClassMask.ErrorFatal : LogClassMask.Normal, reason);
                if (result == QueueDayDropResult.Dropped)
                {
                    dropErrorFirst = !isError;
                    return true;
                }
                if (result == QueueDayDropResult.Contended || result == QueueDayDropResult.LeaseBusy)
                    return false;
                if (context.IsCancellationRequested || context.DeadlineReached)
                    return false;
            }
            dailyDropPending = false;
            return false;
        }
        private bool clearWorkTurn;
        private long lastNow;
        private WorkerControlResult FinishStep(WorkerControlContext context, bool progressed, bool drainComplete = false)
        {
            lastNow = context.NowTicks;
            RefreshDiagnosticBudget(context);
            RecomputeWake(lastNow);
            PublishHealth();
            return new WorkerControlResult(progressed, drainComplete);
        }
        private void RecomputeWake(long now)
        {
            long next = DiagnosticWake(now, nextObservation);
            if (Volatile.Read(ref controlCheckRequested) != 0)
                next = now;
            if (Volatile.Read(ref backgroundSealRequested) != 0)
                next = System.Math.Min(next, nextBackgroundSeal);
            if (journal.DirectoryInventoryInProgress || journal.Status == JournalStatus.Recovering || journal.Status == JournalStatus.New || journal.Clear.IsActive || Volatile.Read(ref pendingClear) != null)
                next = Math.Min(next, Add(now, TimeSpan.TicksPerMillisecond * 2));
            else
                next = Math.Min(next, nextJournal);
            next = SlotWake(normal, false, now, next);
            next = SlotWake(error, true, now, next);
            if (transitioning)
                next = Math.Min(next, transitionExpired ? Add(now, TimeSpan.TicksPerMillisecond * 2) : transitionDeadline);
            if (dailyDropPending)
                next = Math.Min(next, Add(now, TimeSpan.TicksPerMillisecond * 2));
            if (normalRetry != 0)
                next = Math.Min(next, normalRetry);
            if (errorRetry != 0)
                next = Math.Min(next, errorRetry);
            WakeAt(next);
        }
        private long SlotWake(BoundedBlockStaging staging, bool isError, long now, long next)
        {
            if (staging.IsEmpty || staging.HasOutcome)
                return next;
            if (journal.DirectoryInventoryInProgress)
                return Math.Min(next, Add(now, TimeSpan.TicksPerMillisecond * 2));
            if (staging.IsSealed)
                return Math.Min(next, Math.Min(staging.PendingDeadlineTicks, rate.NextAvailableTicks(isError, staging.SealedLength, now)));
            return Math.Min(next, staging.SealRequested ? now : Add(staging.FirstRecordTicks, StorageRuntimeLimits.SealWaitTicks));
        }
    }
}
