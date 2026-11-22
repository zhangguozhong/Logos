using System;
using Logos.Storage;
namespace Logos.Transport
{
    internal sealed partial class TransferStateStore
    {
        private bool continuingSplit;
        private static TaskSlot CopyTask(TaskSlot t, long? sourceUsed = null, TransferTaskState? state = null, int? attempts = null, ExportBookmark bookmark = null, bool replaceBookmark = false, bool identityCoverageGap = false)
            => new TaskSlot(t.Request, t.Generation, t.Revision, t.CleanupGeneration, state ?? t.State, t.AutoError, t.MaxRecords, t.GeneratedBatches, sourceUsed ?? t.SourceUsed, t.EncodedUsed, replaceBookmark ? bookmark : t.Bookmark, attempts ?? t.Attempts, t.Splits, t.Reason, t.RetryUtc, t.IdentityCoverageGap || identityCoverageGap);
        private static TaskSlot[] Rows(TransferState s) => new[] { s.Slot(0), s.Slot(1), s.Slot(2), s.Slot(3) };
        private static TransferState CopyState(TransferState s, TaskSlot[] rows = null, PendingBatch pending = null, bool replacePending = false, ulong? normal = null, ulong? error = null, long? throttle = null, ExportBookmark autoBookmark = null, bool replaceAutoBookmark = false)
            => new TransferState(s.NetworkDay, s.OriginalU, s.OriginalV, normal ?? s.NormalUsed, error ?? s.ErrorUsed, s.CleanupGeneration, s.RevokedThrough, s.UnknownUsage, s.NextSlotGeneration, rows ?? Rows(s), replacePending ? pending : s.Pending, replaceAutoBookmark ? autoBookmark : s.AutoBookmark, s.AutoTaskCounter, throttle ?? s.GlobalThrottleUtc);
        internal static bool SameRequest(RetrievalRequest a, RetrievalRequest b)
        {
            var x = a.Scope;
            var y = b.Scope;
            return a.TaskId == b.TaskId && a.InstallationId == b.InstallationId && a.DestinationId == b.DestinationId && a.FirstQuotaDay == b.FirstQuotaDay && a.QuotaDays == b.QuotaDays && a.ExpiresUtc == b.ExpiresUtc && a.Limits.SourceBytes == b.Limits.SourceBytes && a.Limits.EncodedBytes == b.Limits.EncodedBytes && a.Limits.Batches == b.Limits.Batches && x.StartUtc == y.StartUtc && x.EndUtc == y.EndUtc && x.LevelMask == y.LevelMask && x.StartSequence == y.StartSequence && x.EndSequence == y.EndSequence && x.AppSessionId == y.AppSessionId && x.UnityInstanceId == y.UnityInstanceId && x.UnityVisitId == y.UnityVisitId && x.IdentityVersion == y.IdentityVersion && x.DiagnosticUserId == y.DiagnosticUserId;
        }
        private static bool SameBookmark(ExportBookmark a, ExportBookmark b) => a == b || (a != null && b != null && a.Day == b.Day && a.CapturedEnd == b.CapturedEnd && a.BlockOffset == b.BlockOffset && a.NextRecordIndex == b.NextRecordIndex && a.CleanupGeneration == b.CleanupGeneration && a.HasHeader(b.CopyHeaderHash()));
        private static bool SamePending(PendingBatch a, PendingBatch b) => a != null && b != null && a.Slot == b.Slot && a.SlotGeneration == b.SlotGeneration && a.BodyLength == b.BodyLength && Equal(a.CopyHash(), b.CopyHash());
        private static bool ReceiptMatches(PendingBatch pending, UploadResult receipt)
        {
            if (pending == null || receipt == null || receipt.Status != UploadStatus.Accepted || receipt.ReceivedLength != pending.BodyLength)
                return false;
            string hash = BitConverter.ToString(pending.CopyHash()).Replace("-", "").ToLowerInvariant();
            return receipt.BatchId == hash && receipt.BodySha256 == hash;
        }
        private static bool SameState(TransferState a, TransferState b)
        {
            if (a == b)
                return true;
            if (a == null || b == null || a.GlobalThrottleUtc != b.GlobalThrottleUtc || a.NetworkDay != b.NetworkDay || a.OriginalU != b.OriginalU || a.OriginalV != b.OriginalV || a.NormalUsed != b.NormalUsed || a.ErrorUsed != b.ErrorUsed || a.CleanupGeneration != b.CleanupGeneration || a.RevokedThrough != b.RevokedThrough || a.UnknownUsage != b.UnknownUsage || a.NextSlotGeneration != b.NextSlotGeneration || a.AutoTaskCounter != b.AutoTaskCounter || !SameBookmark(a.AutoBookmark, b.AutoBookmark))
                return false;
            if (a.Pending != b.Pending && (!SamePending(a.Pending, b.Pending) || a.Pending.Phase != b.Pending.Phase || a.Pending.Attempts != b.Pending.Attempts || a.Pending.Splits != b.Pending.Splits || a.Pending.RetryUtc != b.Pending.RetryUtc))
                return false;
            for (int i = 0; i < 4; i++)
            {
                var x = a.Slot(i);
                var y = b.Slot(i);
                if (x == y)
                    continue;
                if (x == null || y == null || x.Generation != y.Generation || x.Revision != y.Revision || x.CleanupGeneration != y.CleanupGeneration || x.State != y.State || x.IdentityCoverageGap != y.IdentityCoverageGap || x.AutoError != y.AutoError || x.MaxRecords != y.MaxRecords || x.GeneratedBatches != y.GeneratedBatches || x.SourceUsed != y.SourceUsed || x.EncodedUsed != y.EncodedUsed || x.Attempts != y.Attempts || x.Splits != y.Splits || x.Reason != y.Reason || x.RetryUtc != y.RetryUtc || !SameBookmark(x.Bookmark, y.Bookmark) || !SameRequest(x.Request, y.Request))
                    return false;
            }
            return true;
        }
        internal bool RequestSave(TransferState next, TransferSaveContext context, bool actualOperationClosed = false, UploadResult receipt = null)
        {
            CheckOwner();
            if (stopped || clearEpochResetDurable || Status != TransferStoreStatus.Ready || next == null || SameState(next, Durable) || (clearGate && !(Durable.UnknownUsage && next.NetworkDay > Durable.NetworkDay && !next.UnknownUsage)))
                return false;
            if (!journal.TransferRegionAvailable)
                return false;
            journal.CheckTransferOwner();
            if (Durable.UnknownUsage && !next.UnknownUsage && next.NetworkDay > Durable.NetworkDay)
            {
                const long guard = TimeSpan.TicksPerHour;
                if (context.TrustedUtc.Ticks > DateTime.MaxValue.Ticks - guard)
                    return false;
                next = CopyState(next, throttle: Math.Max(Math.Max(next.GlobalThrottleUtc, floorThrottle), context.TrustedUtc.Ticks + guard));
            }
            if (!ValidateTransition(next, context, actualOperationClosed, receipt))
                return false;
            if (Math.Max(Sequence, highestIssuedSequence) == ulong.MaxValue)
            {
                Block("State sequence exhausted.");
                return false;
            }
            // 任务或代际替换后，保留的导出器授权立即失效。
            InvalidateChangedGrants(next);
            attemptReservationPending = next.Pending != null && next.Pending.Phase == PendingPhase.AttemptReserved && Durable.Pending != null && next.Pending.Attempts > Durable.Pending.Attempts;
            candidate = next;
            publishingSequence = Math.Max(Sequence, highestIssuedSequence) + 1;
            RememberSequence(next.CleanupGeneration, publishingSequence);
            clearReset = false;
            controlWrite = next.RevokedThrough > Durable.RevokedThrough;
            phase = 10;
            Status = TransferStoreStatus.Saving;
            attemptReady = false;
            // 用量下界包括内存候选：不确定的部分写入绝不带来退款。
            ObserveFloor(next);
            return true;
        }
        private bool ValidateTransition(TransferState next, TransferSaveContext context, bool closed, UploadResult receipt)
        {
            var old = Durable;
            if (old == null || next.CleanupGeneration != journal.CleanupGeneration || next.CleanupGeneration != old.CleanupGeneration || next.GlobalThrottleUtc < old.GlobalThrottleUtc || next.RevokedThrough < old.RevokedThrough || next.NextSlotGeneration < old.NextSlotGeneration || !MeetsFloor(next))
                return false;
            if (next.NetworkDay != old.NetworkDay)
            {
                if (next.NetworkDay <= old.NetworkDay || !context.FreshAuthorization || context.TrustedUtc.Kind != DateTimeKind.Utc || next.NetworkDay != context.TrustedUtc.Ticks / TimeSpan.TicksPerDay || journal.TransferEffectiveDay != next.NetworkDay || !journal.TransferDayConfirmed || next.UnknownUsage || next.OriginalU != context.Total || next.OriginalV != context.Error || next.NormalUsed != 0 || next.ErrorUsed != 0)
                    return false;
            }
            else if (next.OriginalU != old.OriginalU || next.OriginalV != old.OriginalV || next.NormalUsed < old.NormalUsed || next.ErrorUsed < old.ErrorUsed || old.UnknownUsage != next.UnknownUsage)
                return false;
            for (int i = 0; i < 4; i++)
            {
                var t = next.Slot(i);
                var p = old.Slot(i);
                if (t != null && ((t.State <= TransferTaskState.Retry && t.AutoError != (i == 3)) || t.Request.InstallationId != installation || t.Request.DestinationId != destination || (t.Revision <= next.RevokedThrough && (t.State == TransferTaskState.Queued || t.State == TransferTaskState.Exporting || t.State == TransferTaskState.Pending || t.State == TransferTaskState.Retry))))
                    return false;
                if (p == null)
                {
                    if (t != null && (t.Generation < old.NextSlotGeneration || t.SourceUsed != 0))
                        return false;
                    continue;
                }
                if (t == null || t.Generation != p.Generation)
                {
                    if (old.Pending?.Slot == i && !closed)
                        return false;
                    if (t != null && (t.Generation < old.NextSlotGeneration || t.SourceUsed != 0))
                        return false;
                    continue;
                }
                if (p.State >= TransferTaskState.Complete && t.State <= TransferTaskState.Retry && !continuingSplit)
                    return false;
                if (!SameRequest(p.Request, t.Request) || t.SourceUsed < p.SourceUsed || t.EncodedUsed < p.EncodedUsed || t.GeneratedBatches < p.GeneratedBatches)
                    return false;
                if (old.Pending?.Slot == i && !SameBookmark(p.Bookmark, t.Bookmark) && !(closed && next.Pending == null && ReceiptMatches(old.Pending, receipt) && SameBookmark(t.Bookmark, old.Pending.After)))
                    return false;
            }
            if (old.Pending != null)
            {
                if (next.Pending == null)
                {
                    if (!closed)
                        return false;
                    var row = next.Slot(old.Pending.Slot);
                    if (row != null && row.Generation == old.Pending.SlotGeneration && (row.State == TransferTaskState.Queued || row.State == TransferTaskState.Exporting || row.State == TransferTaskState.Complete) && (!ReceiptMatches(old.Pending, receipt) || !SameBookmark(row.Bookmark, old.Pending.After)))
                        return false;
                }
                else
                {
                    if (!SamePending(old.Pending, next.Pending) || next.Pending.Attempts < old.Pending.Attempts)
                        return false;
                    int delta = next.Pending.Attempts - old.Pending.Attempts;
                    if (delta > 1)
                        return false;
                    if (delta == 1)
                    {
                        if (!context.FreshAuthorization || next.GlobalThrottleUtc > context.TrustedUtc.Ticks || next.Pending.Phase != PendingPhase.AttemptReserved || !CanSpend(old, next.Pending.BodyLength, old.Slot(next.Pending.Slot).AutoError, context) || !Charges(next, old, next.Pending))
                            return false;
                    }
                    else if (next.Pending.Phase == PendingPhase.AttemptReserved && old.Pending.Phase != PendingPhase.AttemptReserved)
                        return false;
                }
            }
            else if (next.Pending != null)
            {
                var pending = next.Pending;
                if (pending.Phase != PendingPhase.Prepared || pending.Attempts != 0)
                    return false;
                var row = next.Slot(pending.Slot);
                var prior = old.Slot(pending.Slot);
                long previousEncoded = prior != null && prior.Generation == row.Generation ? prior.EncodedUsed : 0;
                int previousBatches = prior != null && prior.Generation == row.Generation ? prior.GeneratedBatches : 0;
                if (row.EncodedUsed < previousEncoded || row.EncodedUsed - previousEncoded < pending.BodyLength || row.GeneratedBatches <= previousBatches)
                    return false;
            }
            if (old.UnknownUsage && (next.Pending != null || AnyRows(next)))
                return false;
            return true;
        }
        private static bool AnyRows(TransferState s) => s.Slot(0) != null || s.Slot(1) != null || s.Slot(2) != null || s.Slot(3) != null;
        private static bool Charges(TransferState next, TransferState old, PendingBatch p)
        {
            ulong n = next.NormalUsed - old.NormalUsed, e = next.ErrorUsed - old.ErrorUsed;
            return old.Slot(p.Slot).AutoError ? n == 0 && e >= (ulong)p.BodyLength : e == 0 && n >= (ulong)p.BodyLength;
        }
        private bool CanSpend(TransferState s, int bytes, bool error, TransferSaveContext context)
        {
            if (context.TrustedUtc.Ticks < Math.Max(s.GlobalThrottleUtc, floorThrottle) || s.UnknownUsage || s.NetworkDay < 0 || s.NetworkDay != context.TrustedUtc.Ticks / TimeSpan.TicksPerDay || s.OriginalU <= 0)
                return false;
            ulong total = (ulong)Math.Min(s.OriginalU, context.Total), normal = (ulong)Math.Min(s.OriginalU - s.OriginalV, context.Total - context.Error), reserved = (ulong)Math.Min(s.OriginalV, context.Error), cost = (ulong)bytes;
            if (s.NormalUsed > total || s.ErrorUsed > total - s.NormalUsed || cost > total - s.NormalUsed - s.ErrorUsed)
                return false;
            return error ? s.ErrorUsed <= reserved && cost <= reserved - s.ErrorUsed : s.NormalUsed <= normal && cost <= normal - s.NormalUsed;
        }
        // 先申请持久化本次完整正文的网络费用，再由 TryTakeAttempt 取走一次性发送资格。
        internal bool RequestAttempt(TransferSaveContext context)
        {
            CheckOwner();
            var p = Durable?.Pending;
            if (stopped || Status != TransferStoreStatus.Ready || p == null || p.Attempts >= 5 || Durable.Slot(p.Slot).AutoError != (p.Slot == 3) || !context.FreshAuthorization || !CanSpend(Durable, p.BodyLength, Durable.Slot(p.Slot).AutoError, context))
                return false;
            bool error = Durable.Slot(p.Slot).AutoError;
            var next = new PendingBatch(PendingPhase.AttemptReserved, p.IsError, p.Attempts + 1, p.Splits, p.RecordCount, p.Slot, p.SlotGeneration, p.Revision, p.CleanupGeneration, p.RetryUtc, p.Before, p.After, p.CopyBody());
            var rows = Rows(Durable);
            rows[p.Slot] = CopyTask(rows[p.Slot], state: TransferTaskState.Pending, attempts: p.Attempts + 1);
            return RequestSave(CopyState(Durable, rows, next, true, Durable.NormalUsed + (error ? 0u : (ulong)p.BodyLength), Durable.ErrorUsed + (error ? (ulong)p.BodyLength : 0u)), context);
        }
        // 每个新持久预留只授权一次。恢复不能重新创建此内存许可。
        // C 仍需认证每条封装记录，并注册其活跃访问许可。
        internal bool TryTakeAttempt(out PendingBatch pending)
        {
            CheckOwner();
            pending = null;
            if (stopped || clearGate || Status != TransferStoreStatus.Ready || !attemptReady || journal.CleanupGeneration != Durable.CleanupGeneration)
                return false;
            attemptReady = false;
            pending = Durable.Pending;
            return pending != null;
        }
        // 回执匹配且操作实际关闭后，才可把待发送状态替换为确认后的进度。
        internal bool RequestAcknowledge(UploadResult receipt, bool actualOperationClosed, TransferSaveContext context)
        {
            CheckOwner();
            var p = Durable?.Pending;
            if (Status != TransferStoreStatus.Ready || !actualOperationClosed || !ReceiptMatches(p, receipt) || p.Attempts == 0)
                return false;
            var rows = Rows(Durable);
            rows[p.Slot] = CopyTask(rows[p.Slot], state: TransferTaskState.Exporting, bookmark: p.After, replaceBookmark: true);
            return RequestSave(CopyState(Durable, rows, null, true, autoBookmark: p.After, replaceAutoBookmark: rows[p.Slot].AutoError), context, true, receipt);
        }
        internal bool RequestSplit(bool actualOperationClosed, TransferSaveContext context)
        {
            CheckOwner();
            var p = Durable?.Pending;
            if (Status != TransferStoreStatus.Ready || !actualOperationClosed || p == null || p.Attempts == 0 || p.RecordCount <= 1 || p.Splits >= 8)
                return false;
            var row = Durable.Slot(p.Slot);
            if (row.MaxRecords <= 1)
                return false;
            // 服务端拒绝过大正文时缩小每批记录上限，从原批次起点重新导出，同时保留已消耗的额度。
            int limit = Math.Min(row.MaxRecords / 2, Math.Max(1, p.RecordCount / 2));
            var rows = Rows(Durable);
            rows[p.Slot] = new TaskSlot(row.Request, row.Generation, row.Revision, row.CleanupGeneration, TransferTaskState.Blocked, row.AutoError, limit, row.GeneratedBatches, row.SourceUsed, row.EncodedUsed, p.Before, row.Attempts, row.Splits + 1, TransferTerminalReason.TooLarge, identityCoverageGap: row.IdentityCoverageGap);
            return RequestSave(CopyState(Durable, rows, null, true), context, true);
        }
        internal bool RequestContinueSplit(int slot, TransferSaveContext context)
        {
            CheckOwner();
            if (Status != TransferStoreStatus.Ready || Durable.Pending != null || slot < 0 || slot > 3)
                return false;
            var t = Durable.Slot(slot);
            if (t == null || t.State != TransferTaskState.Blocked || t.Reason != TransferTerminalReason.TooLarge || t.Bookmark == null || t.Splits < 1 || t.Splits > 8 || t.MaxRecords > Math.Max(1, 256 >> t.Splits) || t.GeneratedBatches < t.Splits)
                return false;
            var rows = Rows(Durable);
            rows[slot] = new TaskSlot(t.Request, t.Generation, t.Revision, t.CleanupGeneration, TransferTaskState.Exporting, t.AutoError, t.MaxRecords, t.GeneratedBatches, t.SourceUsed, t.EncodedUsed, t.Bookmark, 0, t.Splits, identityCoverageGap: t.IdentityCoverageGap);
            continuingSplit = true;
            try
            {
                return RequestSave(CopyState(Durable, rows), context);
            }
            finally
            {
                continuingSplit = false;
            }
        }
        internal bool RequestClearReset(bool actualOperationsClosed)
        {
            CheckOwner();
            if (stopped || !actualOperationsClosed || !journal.TransferRegionAvailable)
            {
                RevokeClearAuthorization();
                if (Status == TransferStoreStatus.Saving && clearReset)
                    BeginLoad();
                return false;
            }
            journal.CheckTransferOwner();
            if (!journal.TransferClearProof)
            {
                RevokeClearAuthorization();
                if (Status == TransferStoreStatus.Saving && clearReset)
                    BeginLoad();
                return false;
            }
            InvalidateGrant();
            attemptReady = false;
            if (ClearBarrierReady)
                return true;
            long generation = journal.CleanupGeneration;
            bool continuing = resetRequested && HasCurrentClearAuthorization
                && (Status == TransferStoreStatus.Loading || (Status == TransferStoreStatus.Saving && clearReset));
            if (!continuing)
                BeginLoad();
            clearOperationsClosed = true;
            clearOperationGeneration = generation;
            resetRequested = true;
            return true;
        }
    }
}
