using System;
using Logos.Storage;
namespace Logos.Transport
{
    internal sealed partial class TransferCoordinator
    {
        private JournalExporter exporter;
        private ExportBatch exported;
        private int exportSlot = -1;
        private SourceReadGrant sourceGrant;
        private UploadRequest upload;
        private TransferSendPermit permit;
        private UploadResult result;
        private bool awaitingDebit, startReady, closing, settling, abandon, cancelForRetry;
        private long attemptStarted, nextProbe, executionRetryAt, lastProbe = -1;
        private bool disposed, exporterClosePending, exporterCloseSaturated;
        private long exporterCloseRetryAt;
        private volatile bool background, stopping, clearing;
        private bool clearMemoryDrained;
        private int ackSlot = -1, ackCount;
        private ulong ackGeneration;
        private UploadResult ackReceipt;
        private readonly bool[] countsKnown = new bool[4];
        private readonly ulong[] countGeneration = new ulong[4];
        private readonly long[] ackRecords = new long[4], ackBatches = new long[4];
        private long sessionAttempts, sessionAcknowledged;
        internal void CompletePending()
        {
            RequestStop();
        }
        // 目录关闭失败后的释放重试，必须提供持续推进的 Core 时钟。
        internal bool TryDispose(long nowTicks100ns)
        {
            CheckOwner();
            lastNow = Math.Max(lastNow, nowTicks100ns);
            return TryDispose();
        }
        internal bool TryDispose()
        {
            CheckOwner();
            if (disposed)
                return true;
            RequestStop();
            if (permit != null)
            {
                access.CancelPermit(permit);
                try
                {
                    if (!access.TryClose(permit))
                        return false;
                    permit = null;
                }
                catch (Exception ex) when (!(ex is OutOfMemoryException))
                {
                    reason = "CloseException";
                    return false;
                }
            }
            if (!FinishFileRecovery(now: lastNow) || !CloseExporter())
                return false;
            fileSpool?.CloseRecovery();
            ResetAttempt();
            Array.Clear(admitted, 0, admitted.Length);
            disposed = true;
            return true;
        }
        public void Dispose()
        {
            if (!TryDispose())
                throw new InvalidOperationException("Operation has not actually closed; coordinator retains ownership.");
        }
        internal long NextDelayTicks100ns(long now)
        {
            CheckOwner();
            lastNow = now;
            long work = Math.Min(ExecutionDelayTicks100ns(now), FileRecoveryDelay(now));
            if (disposed || stopping || clearing || current == null || !time.TryUtc(now, out var utc))
                return work;
            long expiry = utc < current.ExpiresUtc ? current.ExpiresUtc.Ticks - utc.Ticks : ready ? 0 : long.MaxValue;
            bool canClean = permit == null && store.Status == TransferStoreStatus.Ready && !ExporterCloseWaiting(now);
            for (int i = 0; i < 4; i++)
            {
                var row = store.Durable?.Slot(i);
                var request = row != null && IsRunnable(row) ? row.Request : i < 3 && row == null ? admitted[i] : null;
                if (request == null)
                    continue;
                if (request.ExpiresUtc > utc)
                    expiry = Math.Min(expiry, request.ExpiresUtc.Ticks - utc.Ticks);
                else if (canClean)
                    expiry = 0;
            }
            return Math.Min(work, expiry);
        }
        private static bool IsRunnable(TaskSlot row) => row.State <= TransferTaskState.Retry || (row.State == TransferTaskState.Blocked && row.Reason == TransferTerminalReason.TooLarge);
        private bool ExpireTask(DateTime utc, TransferSaveContext ctx)
        {
            for (int i = 0; i < 4; i++)
            {
                var row = store.Durable.Slot(i);
                if (row != null && IsRunnable(row) && utc >= row.Request.ExpiresUtc)
                {
                    Terminate(i, TransferTaskState.Partial, TransferTerminalReason.Expired, ctx);
                    return true;
                }
                if (i < 3 && row == null && admitted[i] != null && utc >= admitted[i].ExpiresUtc)
                {
                    ulong generation = store.Durable.NextSlotGeneration;
                    if (generation == ulong.MaxValue)
                        return false;
                    var rows = Rows();
                    rows[i] = new TaskSlot(admitted[i], generation, admittedRevision[i], journal.CleanupGeneration, TransferTaskState.Canceled, reason: TransferTerminalReason.Expired);
                    store.RequestSave(Copy(rows, nextGeneration: generation + 1), ctx);
                    return true;
                }
            }
            return false;
        }
        private long ExecutionDelayTicks100ns(long now)
        {
            CheckOwner();
            if (disposed)
                return long.MaxValue;
            if (permit != null)
                return nextProbe == long.MaxValue && now == long.MaxValue ? TransferPolicy.PollTicks : Math.Max(0, nextProbe - Math.Max(0, now));
            if (ExporterCloseWaiting(now))
                return exporterCloseSaturated ? 30 * TimeSpan.TicksPerSecond : exporterCloseRetryAt - Math.Max(0, now);
            if (exporter != null && (stopping || clearing || exporterClosePending))
                return 0;
            if (store.Status != TransferStoreStatus.Ready)
                return store.Status == TransferStoreStatus.Disposed ? long.MaxValue : store.NextDelayTicks100ns(now);
            lock (publication)
                if (publicationVersion != appliedVersion || desiredFloor > store.Durable.RevokedThrough)
                    return 0;
            if (stopping || clearing || current == null)
                return long.MaxValue;
            if (background && !settling)
                return long.MaxValue;
            if (executionRetryAt > now)
                return executionRetryAt - Math.Max(0, now);
            if (!time.TryUtc(now, out var utc))
                return long.MaxValue;
            if (utc >= current.ExpiresUtc)
                return ready ? 0 : long.MaxValue;
            if (ackSlot >= 0 || settling)
                return 0;
            if (FileSpoolUnavailable)
                return FileRecoveryDelay(now);
            for (int i = 0; i < 3; i++)
                if (admitted[i] != null)
                    return 0;
            var disabledAuto = store.Durable.Slot(3);
            if (!AutomaticEnabled && disabledAuto != null && disabledAuto.AutoError && IsRunnable(disabledAuto))
                return 0;
            var pending = store.Durable.Pending;
            if (pending != null)
            {
                long until = Math.Max(pending.RetryUtc, store.KnownGlobalThrottleUtc);
                return until > utc.Ticks ? until - utc.Ticks : 0;
            }
            for (int i = 0; i < 4; i++)
            {
                var row = store.Durable.Slot(i);
                if (row != null && (row.State == TransferTaskState.Queued || row.State == TransferTaskState.Exporting || (row.State == TransferTaskState.Blocked && row.Reason == TransferTerminalReason.TooLarge)))
                    return 0;
            }
            if (AutomaticEnabled && current.DomainCount > 0)
                return nextAutoProbe > now ? nextAutoProbe - Math.Max(0, now) : 0;
            return long.MaxValue;
        }
        internal bool OperationsClosed => permit == null && exporter == null && fileRecovery == null;
        internal void SetBackground(bool value)
        {
            lock (publication)
            {
                background = value;
            }
        }
        internal void RequestStop()
        {
            lock (publication)
            {
                stopping = true;
                ready = false;
                access.SetCoordinatorGate(false, 0);
            }
        }
        internal void CloseForClear()
        {
            lock (publication)
            {
                clearMemoryDrained = false;
                clearing = true;
                ready = false;
                access.SetCoordinatorGate(false, 0);
            }
        }
        internal bool ClearMemoryReady => clearing && clearMemoryDrained && OperationsClosed;
        private void DrainClosedClearMemory()
        {
            if (!clearing || !OperationsClosed || clearMemoryDrained)
                return;
            // 后台扫描已退出，交还跨页游标后再让存储模块接管清理。
            fileSpool?.CloseRecovery();
            // 上传正文保持借用，直到实际许可和导出器都关闭。
            Array.Clear(admitted, 0, admitted.Length);
            Array.Clear(admittedRevision, 0, admittedRevision.Length);
            ResetAttempt();
            exported = null;
            exportSlot = -1;
            sourceGrant = null;
            ackSlot = -1;
            ackCount = 0;
            ackGeneration = 0;
            ackReceipt = null;
            Array.Clear(countsKnown, 0, countsKnown.Length);
            Array.Clear(countGeneration, 0, countGeneration.Length);
            Array.Clear(ackRecords, 0, ackRecords.Length);
            Array.Clear(ackBatches, 0, ackBatches.Length);
            executionRetryAt = attemptStarted = nextProbe = 0;
            lastProbe = -1;
            ResetAutomaticMemory();
            clearMemoryDrained = true;
            reason = "Clearing";
        }
        internal void ResumeAfterClear()
        {
            CheckOwner();
            if (!ClearMemoryReady)
                throw new InvalidOperationException("Clear resources or memory are still owned.");
            clearing = false;
            clearMemoryDrained = false;
            ResetAttempt();
        }
        internal bool TryGetTask(string id, out TransferTaskSnapshot snapshot)
        {
            CheckOwner();
            snapshot = null;
            if (store.Durable == null)
                return false;
            for (int i = 0; i < 4; i++)
            {
                var row = store.Durable.Slot(i);
                if (row?.Request.TaskId != id)
                    continue;
                bool known = countsKnown[i] && countGeneration[i] == row.Generation;
                snapshot = new TransferTaskSnapshot(row, known, known ? ackRecords[i] : (long?)null, known ? ackBatches[i] : (long?)null);
                return true;
            }
            return false;
        }
        internal bool Release(string id)
        {
            CheckOwner();
            if (store.Status != TransferStoreStatus.Ready || store.Durable.Pending != null || permit != null)
                return false;
            for (int i = 0; i < 3; i++)
            {
                var row = store.Durable.Slot(i);
                if (row?.Request.TaskId != id)
                    continue;
                if (row.State != TransferTaskState.Complete && row.State != TransferTaskState.Canceled && row.State != TransferTaskState.Partial && row.State != TransferTaskState.Failed)
                    return false;
                var rows = Rows();
                rows[i] = null;
                var utc = time.TryUtc(lastNow, out var t) ? t : new DateTime(0, DateTimeKind.Utc);
                return store.RequestSave(Copy(rows), new TransferSaveContext(utc, policy.Total, policy.Error, false), true);
            }
            return false;
        }
        private void InitializeCounts()
        {
            for (int i = 0; i < 4; i++)
            {
                countGeneration[i] = store.Durable.Slot(i)?.Generation ?? 0;
                countsKnown[i] = false;
            }
        }
        private static TaskSlot Change(TaskSlot t, TransferTaskState state, ExportBookmark bookmark = null, bool replaceBookmark = false, long? source = null, long? encoded = null, int? batches = null, int? attempts = null, int? splits = null, int? maxRecords = null, TransferTerminalReason? terminal = null, long? retry = null, bool identityCoverageGap = false)
         => new TaskSlot(t.Request, t.Generation, t.Revision, t.CleanupGeneration, state, t.AutoError, maxRecords ?? t.MaxRecords, batches ?? t.GeneratedBatches, source ?? t.SourceUsed, encoded ?? t.EncodedUsed, replaceBookmark ? bookmark : t.Bookmark, attempts ?? t.Attempts, splits ?? t.Splits, terminal ?? t.Reason, retry ?? t.RetryUtc, t.IdentityCoverageGap || identityCoverageGap);
        private bool ExporterCloseWaiting(long now) => exporter != null && exporterClosePending && (exporterCloseSaturated || now < exporterCloseRetryAt);
        private void BackoffExporterClose()
        {
            exporterClosePending = true;
            long now = Math.Max(0, lastNow);
            exporterCloseSaturated = now > long.MaxValue - 30 * TimeSpan.TicksPerSecond;
            exporterCloseRetryAt = exporterCloseSaturated ? long.MaxValue : now + 30 * TimeSpan.TicksPerSecond;
            reason = "ExporterCloseFailed";
        }
        private bool CloseExporter()
        {
            if (exporter == null)
                return true;
            if (ExporterCloseWaiting(lastNow))
                return false;
            try
            {
                exporter.Dispose();
                exporter = null;
                exported = null;
                exportSlot = -1;
                exporterClosePending = exporterCloseSaturated = false;
                return true;
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                BackoffExporterClose();
                return false;
            }
        }
        private void ResetAttempt()
        {
            upload = null;
            result = null;
            awaitingDebit = startReady = closing = settling = abandon = cancelForRetry = false;
        }
        // 会话确认计数跟随已发布的确认状态更新，避免把尚在保存的回执计入完成进度。
        private bool CompleteAcknowledgment()
        {
            if (ackSlot < 0 || store.Durable.Pending != null)
                return false;
            var row = store.Durable.Slot(ackSlot);
            if (row != null && row.Generation == ackGeneration && row.State == TransferTaskState.Exporting)
            {
                if (exporter != null && exported != null)
                    exporter.Acknowledge(exported, ackReceipt);
                if (!CloseExporter())
                    return true;
                if (countGeneration[ackSlot] == ackGeneration)
                {
                    ackRecords[ackSlot] += ackCount;
                    ackBatches[ackSlot]++;
                }
                sessionAcknowledged += ackCount;
                preferAuto = ackSlot != 3;
            }
            exported = null;
            ackSlot = -1;
            ackReceipt = null;
            ResetAttempt();
            return true;
        }
        private void DriveExecution(JournalStepContext context, long now, DateTime utc, TransferSaveContext ctx)
        {
            if (now < executionRetryAt)
                return;
            var pending = store.Durable.Pending;
            if (pending != null)
            {
                var row = store.Durable.Slot(pending.Slot);
                if (row.AutoError != (pending.Slot == 3))
                {
                    Terminate(pending.Slot, TransferTaskState.Failed, TransferTerminalReason.Protocol, ctx);
                    return;
                }
                if (!access.IsAuthorized(row.Request, row.Revision, row.CleanupGeneration, utc))
                {
                    Terminate(pending.Slot, TransferTaskState.Canceled, TransferTerminalReason.ReauthorizationRequired, ctx);
                    return;
                }
                if (settling)
                {
                    Settle(utc, ctx);
                    return;
                }
                if (utc.Ticks < Math.Max(pending.RetryUtc, store.KnownGlobalThrottleUtc))
                {
                    reason = "RetryWaiting";
                    return;
                }
                if (upload == null)
                {
                    try
                    {
                        var body = new UploadBody(pending.CopyBody(), true);
                        // 恢复时重新用当前授权解码待发送正文，磁盘上有效的状态页本身不能代替逐条授权。
                        var verified = UploadEnvelope.Decode(body, exportKeys, row.Request, access, row.Revision, row.CleanupGeneration, utc);
                        if (verified.Records.Count != pending.RecordCount)
                            throw new InvalidOperationException("Pending record count differs.");
                        upload = new UploadRequest(row.Request.TaskId, row.Request.DestinationId, row.Request.InstallationId, row.Revision, row.CleanupGeneration, body);
                        reason = "PendingValidated";
                    }
                    catch (UnauthorizedAccessException)
                    {
                        Terminate(pending.Slot, TransferTaskState.Canceled, TransferTerminalReason.Unauthorized, ctx);
                    }
                    catch (JournalBlockedException)
                    {
                        reason = "ExportKeyUnavailable";
                        executionRetryAt = now > long.MaxValue - 30 * TimeSpan.TicksPerSecond ? long.MaxValue : now + 30 * TimeSpan.TicksPerSecond;
                    }
                    catch (Exception ex) when (!(ex is OutOfMemoryException))
                    {
                        Terminate(pending.Slot, TransferTaskState.Failed, TransferTerminalReason.Corrupt, ctx);
                    }
                    return;
                }
                if (startReady)
                {
                    if (!access.TryRegister(row.Request, row.Revision, row.CleanupGeneration, utc, out permit))
                    {
                        ResetAttempt();
                        return;
                    }
                    attemptStarted = now;
                    nextProbe = now;
                    startReady = false;
                    sessionAttempts++;
                    bool accepted = false;
                    try
                    {
                        accepted = access.TryStart(permit, service, upload, fileSpool);
                    }
                    catch (Exception ex) when (!(ex is OutOfMemoryException))
                    {
                        reason = "StartException";
                    }
                    if (!accepted)
                    {
                        result = new UploadResult(UploadStatus.Transient);
                        closing = cancelForRetry = true;
                        access.CancelPermit(permit);
                    }
                    return;
                }
                if (awaitingDebit)
                {
                    awaitingDebit = false;
                    // 持久扣费成功后仍要领取本进程的一次性资格，读取到已扣费的历史页不能直接启动请求。
                    if (store.TryTakeAttempt(out var reserved) && reserved.SlotGeneration == pending.SlotGeneration)
                    {
                        startReady = true;
                        reason = "AttemptDurable";
                    }
                    else
                        upload = null;
                    return;
                }
                if (pending.Attempts >= 5)
                {
                    Terminate(pending.Slot, TransferTaskState.Partial, TransferTerminalReason.RetryExhausted, ctx);
                    return;
                }
                if (store.RequestAttempt(ctx))
                {
                    awaitingDebit = true;
                    reason = "AttemptPrecharging";
                }
                else
                    Terminate(pending.Slot, TransferTaskState.Partial, TransferTerminalReason.DailyUploadBudget, ctx);
                return;
            }
            if (upload != null)
                ResetAttempt();
            int slot = exportSlot;
            if (slot < 0)
            {
                for (int order = 0; order < 4; order++)
                {
                    int i = preferAuto ? (order == 0 ? 3 : order - 1) : order;
                    var r = store.Durable.Slot(i);
                    if (r?.State == TransferTaskState.Blocked && r.Reason == TransferTerminalReason.TooLarge)
                    {
                        if (!store.RequestContinueSplit(i, ctx))
                            Terminate(i, TransferTaskState.Partial, TransferTerminalReason.TooLarge, ctx);
                        return;
                    }
                    if (r != null && (r.State == TransferTaskState.Queued || r.State == TransferTaskState.Exporting))
                    {
                        slot = i;
                        break;
                    }
                }
                if (slot < 0)
                {
                    reason = autoCoverageGap ? "AutoCoverageGap" : "Idle";
                    return;
                }
            }
            var task = store.Durable.Slot(slot);
            if (task.AutoError != (slot == 3))
            {
                Terminate(slot, TransferTaskState.Failed, TransferTerminalReason.Protocol, ctx);
                return;
            }
            if (!access.IsAuthorized(task.Request, task.Revision, task.CleanupGeneration, utc))
            {
                Terminate(slot, TransferTaskState.Canceled, TransferTerminalReason.ReauthorizationRequired, ctx);
                return;
            }
            if (exporter == null)
            {
                if (task.GeneratedBatches >= task.Request.Limits.Batches)
                {
                    Terminate(slot, TransferTaskState.Partial, TransferTerminalReason.PartialBatchBudget, ctx);
                    return;
                }
                if (task.EncodedUsed >= task.Request.Limits.EncodedBytes)
                {
                    Terminate(slot, TransferTaskState.Partial, TransferTerminalReason.PartialEncodedBudget, ctx);
                    return;
                }
                store.TryTakeSourceGrant(out _);
                bool selectedSource = store.TrySelectSourceGrant(slot, out sourceGrant, out var readAuthorization);
                if ((sourceGrant == null || !sourceGrant.Valid || sourceGrant.SlotGeneration != task.Generation || sourceGrant.Remaining == 0) && task.SourceUsed >= task.Request.Limits.SourceBytes)
                {
                    Terminate(slot, TransferTaskState.Partial, TransferTerminalReason.PartialSourceBudget, ctx);
                    return;
                }
                if (!selectedSource)
                {
                    store.RequestSourceGrant(slot, ctx);
                    reason = "SourcePrecharging";
                    return;
                }
                exporter = new JournalExporter(journal, task.Request, access, task.Revision, exportKeys, task.Bookmark, task.MaxRecords);
                var resume = store.GetAuthenticatedResume(slot);
                if (resume != null)
                    exporter.ApplyResume(resume);
                exporter.SetReadAuthorization(readAuthorization);
                exporter.RestrictRemainingBudgets(task.Request.Limits.SourceBytes, task.Request.Limits.EncodedBytes - task.EncodedUsed, task.Request.Limits.Batches - task.GeneratedBatches);
                exportSlot = slot;
                reason = "Exporting";
                return;
            }
            if (store.TryTakeSourceGrant(out _) && store.TrySelectSourceGrant(slot, out sourceGrant, out var toppedUp))
                exporter.SetReadAuthorization(toppedUp);
            TransferOutcome outcome;
            ExportBatch batch;
            try
            {
                outcome = exporter.Step(context, utc, out batch);
            }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException)
            {
                BackoffExporterClose();
                return;
            }
            long measured = task.SourceUsed;
            if (outcome == TransferOutcome.WaitingCredit)
            {
                if (store.TryTakeSourceGrant(out _) && store.TrySelectSourceGrant(slot, out sourceGrant, out var authorized))
                {
                    exporter.SetReadAuthorization(authorized);
                    return;
                }
                exporter.TryGetAuthenticatedProgress(out var progress);
                if (!store.RequestSourceGrant(slot, ctx, progress, exporter.IdentityCoverageGap))
                    Terminate(slot, TransferTaskState.Partial, TransferTerminalReason.PartialSourceBudget, ctx);
                else
                    reason = "WaitingCredit";
                return;
            }
            if (outcome == TransferOutcome.Progress)
            {
                if (task.AutoError && task.Bookmark == null && exporter.AuthenticatedSnapshot != null)
                {
                    var snapshot = exporter.AuthenticatedSnapshot;
                    if (!CloseExporter())
                        return;
                    var rows = Rows();
                    rows[slot] = Change(task, TransferTaskState.Exporting, snapshot, true);
                    store.RequestSave(Copy(rows), ctx);
                    reason = "AutoSnapshotPersisting";
                }
                return;
            }
            if (outcome == TransferOutcome.BatchReady)
            {
                bool error = batch.Request.Body[22] == 1;
                var p = new PendingBatch(PendingPhase.Prepared, error, 0, task.Splits, batch.RecordCount, slot, task.Generation, task.Revision, task.CleanupGeneration, 0, batch.BeforeBookmark, batch.NextBookmark, batch.Request.Body.CopyBytes());
                var rows = Rows();
                rows[slot] = Change(task, TransferTaskState.Pending, batch.BeforeBookmark, true, measured, task.EncodedUsed + p.BodyLength, task.GeneratedBatches + 1, 0, terminal: TransferTerminalReason.None, retry: 0, identityCoverageGap: exporter.IdentityCoverageGap);
                if (store.RequestSave(Copy(rows, pending: p, replacePending: true), ctx))
                {
                    exported = batch;
                    reason = "PendingPreparing";
                }
                return;
            }
            if (task.IdentityCoverageGap && (outcome == TransferOutcome.Complete || outcome == TransferOutcome.NoMatch))
                outcome = TransferOutcome.IdentityUnverifiable;
            TransferTerminalReason terminal;
            switch (outcome)
            {
                case TransferOutcome.Complete:
                    terminal = TransferTerminalReason.Complete;
                    break;
                case TransferOutcome.NoMatch:
                    terminal = TransferTerminalReason.NoMatch;
                    break;
                case TransferOutcome.KeyUnavailable:
                    reason = "ExportKeyUnavailable";
                    executionRetryAt = now > long.MaxValue - 30 * TimeSpan.TicksPerSecond ? long.MaxValue : now + 30 * TimeSpan.TicksPerSecond;
                    CloseExporter();
                    return;
                case TransferOutcome.NotReady:
                    reason = "SourceNotReady";
                    executionRetryAt = now > long.MaxValue - TimeSpan.TicksPerSecond ? long.MaxValue : now + TimeSpan.TicksPerSecond;
                    CloseExporter();
                    return;
                case TransferOutcome.IdentityUnverifiable:
                    terminal = TransferTerminalReason.IdentityUnverifiable;
                    break;
                case TransferOutcome.Missing:
                    terminal = TransferTerminalReason.Missing;
                    break;
                case TransferOutcome.Expired:
                    terminal = TransferTerminalReason.Expired;
                    break;
                case TransferOutcome.PartialBudget:
                    terminal = exporter.BudgetFailure == ExportBudgetFailure.Encoded ? TransferTerminalReason.PartialEncodedBudget : exporter.BudgetFailure == ExportBudgetFailure.Batches ? TransferTerminalReason.PartialBatchBudget : TransferTerminalReason.PartialSourceBudget;
                    break;
                case TransferOutcome.LeaseCancelled:
                    terminal = TransferTerminalReason.LeaseCancelled;
                    break;
                case TransferOutcome.Cancelled:
                    terminal = TransferTerminalReason.Cancelled;
                    break;
                case TransferOutcome.Unavailable:
                    terminal = TransferTerminalReason.NotReady;
                    break;
                case TransferOutcome.Unauthorized:
                    terminal = TransferTerminalReason.Unauthorized;
                    break;
                default:
                    terminal = TransferTerminalReason.Corrupt;
                    break;
            }
            var state = (outcome == TransferOutcome.Complete || outcome == TransferOutcome.NoMatch) ? TransferTaskState.Complete : TransferTaskState.Partial;
            Terminate(slot, state, terminal, ctx, measured);
        }
        private void Terminate(int slot, TransferTaskState state, TransferTerminalReason terminal, TransferSaveContext ctx, long? source = null)
        {
            if (slot == 3 && state != TransferTaskState.Complete)
                autoCoverageGap = true;
            ExportBookmark progress = null;
            if (exportSlot == slot && exporter != null)
                exporter.TryGetAuthenticatedProgress(out progress);
            if (exportSlot == slot && !CloseExporter())
                return;
            bool ownsPending = store.Durable.Pending?.Slot == slot;
            var rows = Rows();
            rows[slot] = Change(rows[slot], state, bookmark: progress, replaceBookmark: rows[slot].AutoError && progress != null && !ownsPending, source: source, terminal: terminal);
            if (store.RequestSave(Copy(rows, replacePending: ownsPending, autoBookmark: progress, replaceAutoBookmark: rows[slot].AutoError && progress != null && !ownsPending), ctx, true))
            {
                if (ownsPending)
                    ResetAttempt();
                reason = terminal.ToString();
            }
        }
        // 网络结果可先于请求资源释放出现，协调器还要驱动实际关闭才能结算任务。
        private void DriveOperation(long now, JournalStepContext context, IMonotonicClock liveClock)
        {
            if (now < nextProbe || now == long.MaxValue && lastProbe == long.MaxValue)
                return;
            lastProbe = now;
            nextProbe = now > long.MaxValue - TransferPolicy.PollTicks ? long.MaxValue : now + TransferPolicy.PollTicks;
            DateTime utc = default;
            bool valid = current != null && time.TryUtc(now, out utc) && utc < current.ExpiresUtc && utc < permit.Request.ExpiresUtc;
            if (stopping || clearing || !valid || (permit.IsCancellationRequested && !cancelForRetry) || journal.CleanupGeneration != permit.Generation)
            {
                abandon = true;
                closing = true;
                access.CancelPermit(permit);
            }
            if (!closing)
            {
                if (now < attemptStarted || now - attemptStarted >= TransferPolicy.WatchdogTicks)
                {
                    result = new UploadResult(UploadStatus.Transient);
                    closing = cancelForRetry = true;
                    access.CancelPermit(permit);
                }
                else
                {
                    try
                    {
                        if (permit.Operation is FileUploadAdapter.Operation file && file.ReadyToDispatch)
                        {
                            if (!DispatchFile(file, now, context, liveClock))
                                return;
                        }
                        if (permit.TryGetResult(out result))
                        {
                            if (result == null)
                                result = new UploadResult(UploadStatus.Permanent);
                            closing = true;
                        }
                    }
                    catch (Exception ex) when (!(ex is OutOfMemoryException))
                    {
                        result = new UploadResult(UploadStatus.Transient);
                        closing = cancelForRetry = true;
                        access.CancelPermit(permit);
                    }
                    // 先记录全局限流截止时间，避免当前操作迟迟未关闭时丢失服务端已经给出的限制。
                    if (closing && result?.Status == UploadStatus.Throttled && valid)
                    {
                        long delay = policy.ThrottleTicks(store.Durable.Pending.Attempts, store.Durable.Pending.SlotGeneration, result.RetryAfterSeconds);
                        store.ObserveGlobalThrottle(delay > DateTime.MaxValue.Ticks - utc.Ticks ? DateTime.MaxValue.Ticks : utc.Ticks + delay);
                    }
                    return;
                }
            }
            try
            {
                if (!access.TryClose(permit))
                {
                    reason = "AwaitingActualClose";
                    return;
                }
                permit = null;
                closing = false;
                settling = true;
                reason = "OperationClosed";
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                reason = "CloseException";
            }
        }
        private void Settle(DateTime utc, TransferSaveContext ctx)
        {
            var p = store.Durable.Pending;
            if (abandon)
            {
                Terminate(p.Slot, TransferTaskState.Canceled, TransferTerminalReason.Cancelled, ctx);
                return;
            }
            if (result != null && result.Status == UploadStatus.Accepted && upload.IsAcknowledged(result))
            {
                if (store.RequestAcknowledge(result, true, ctx))
                {
                    ackSlot = p.Slot;
                    ackGeneration = p.SlotGeneration;
                    ackCount = p.RecordCount;
                    ackReceipt = result;
                    settling = false;
                }
                return;
            }
            if (result != null && (result.Status == UploadStatus.Transient || result.Status == UploadStatus.Throttled))
            {
                if (p.Attempts >= 5)
                {
                    Terminate(p.Slot, TransferTaskState.Partial, TransferTerminalReason.RetryExhausted, ctx);
                    return;
                }
                long delay = result.Status == UploadStatus.Throttled ? policy.ThrottleTicks(p.Attempts, p.SlotGeneration, result.RetryAfterSeconds) : policy.RetryTicks(p.Attempts, p.SlotGeneration);
                if (delay > DateTime.MaxValue.Ticks - utc.Ticks)
                {
                    Terminate(p.Slot, TransferTaskState.Partial, TransferTerminalReason.Expired, ctx);
                    return;
                }
                // 重试同时受本批次退避和全局限流约束，取较晚者并检查任务是否还有效。
                long retry = Math.Max(utc.Ticks + delay, store.KnownGlobalThrottleUtc);
                var row = store.Durable.Slot(p.Slot);
                if (retry >= row.Request.ExpiresUtc.Ticks)
                {
                    Terminate(p.Slot, TransferTaskState.Partial, TransferTerminalReason.Expired, ctx);
                    return;
                }
                var pending = new PendingBatch(PendingPhase.Retry, p.IsError, p.Attempts, p.Splits, p.RecordCount, p.Slot, p.SlotGeneration, p.Revision, p.CleanupGeneration, retry, p.Before, p.After, p.CopyBody());
                var rows = Rows();
                rows[p.Slot] = Change(row, TransferTaskState.Retry, retry: retry);
                if (store.RequestSave(Copy(rows, pending: pending, replacePending: true), ctx))
                {
                    ResetAttempt();
                    reason = "RetryPersisting";
                }
                return;
            }
            if (result?.Status == UploadStatus.TooLarge)
            {
                if (!CloseExporter())
                    return;
                if (store.RequestSplit(true, ctx))
                {
                    ResetAttempt();
                    reason = "SplitPersisting";
                }
                else
                    Terminate(p.Slot, TransferTaskState.Partial, TransferTerminalReason.TooLarge, ctx);
                return;
            }
            if (result?.Status == UploadStatus.Unauthorized)
            {
                Terminate(p.Slot, TransferTaskState.Canceled, TransferTerminalReason.ReauthorizationRequired, ctx);
                return;
            }
            Terminate(p.Slot, TransferTaskState.Failed, TransferTerminalReason.Protocol, ctx);
        }
    }
}
