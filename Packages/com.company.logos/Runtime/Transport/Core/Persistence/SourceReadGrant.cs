using System;
namespace Logos.Transport
{
    // 仅数值额度：没有块大小数组，也无 I/O。失败或部分读取都在读取前
    // 消耗其完整请求上界；调用者绝不退款。
    internal sealed class SourceReadGrant
    {
        private readonly TransferStateStore store;
        internal int Slot
        {
            get;
        }
        internal long Revision
        {
            get;
        }
        internal RetrievalRequest Request
        {
            get;
        }
        internal ulong SlotGeneration
        {
            get;
        }
        internal long CleanupGeneration
        {
            get;
        }
        internal long Remaining
        {
            get;
            private set;
        }
        internal bool Valid
        {
            get;
            private set;
        }
        = true;
        internal SourceReadGrant(TransferStateStore store, int slot, TaskSlot row, long bytes)
        {
            this.store = store;
            Slot = slot;
            SlotGeneration = row.Generation;
            CleanupGeneration = row.CleanupGeneration;
            Revision = row.Revision;
            Request = row.Request;
            Remaining = bytes;
        }
        internal bool TryConsume(long upperBoundBytes) => store.ConsumeGrant(this, upperBoundBytes);
        internal void Invalidate()
        {
            Valid = false;
            Remaining = 0;
        }
        internal void AddCredit(long bytes)
        {
            Remaining = checked(Remaining + bytes);
        }
        internal void Consume(long bytes)
        {
            Remaining -= bytes;
        }
    }
    internal sealed partial class TransferStateStore
    {
        private readonly SourceReadGrant[] sourceCredits = new SourceReadGrant[4];
        private ulong sourceSelection;
        // 切换槽位会改变选择序号，旧读取器即使持有仍有余额的额度对象，也无法继续扣费读取。
        private sealed class SelectedSourceRead : Logos.Storage.IJournalSourceReadAuthorization
        {
            private readonly TransferStateStore store;
            private readonly SourceReadGrant grant;
            private readonly ulong selection;
            internal SelectedSourceRead(TransferStateStore store, SourceReadGrant grant, ulong selection)
            {
                this.store = store;
                this.grant = grant;
                this.selection = selection;
            }
            public bool TryConsume(long bytes) => selection == store.sourceSelection && store.ConsumeGrant(grant, bytes);
        }
        private void ChangeSourceSelection(SourceReadGrant grant)
        {
            activeGrant = grant;
            if (sourceSelection < ulong.MaxValue)
                sourceSelection++;
            else
                activeGrant = null;
        }
        private void InvalidateGrant()
        {
            for (int i = 0; i < 4; i++)
            {
                sourceCredits[i]?.Invalidate();
                sourceCredits[i] = null;
            }
            ChangeSourceSelection(null);
            issuedGrant = sourceReservationExisting = null;
        }
        private void InvalidateChangedGrants(TransferState next)
        {
            for (int i = 0; i < 4; i++)
                if (sourceCredits[i] != null && !GrantMatches(next, sourceCredits[i]))
                {
                    sourceCredits[i].Invalidate();
                    sourceCredits[i] = null;
                }
            if (activeGrant != null && !activeGrant.Valid)
                ChangeSourceSelection(null);
            if (issuedGrant != null && !issuedGrant.Valid)
                issuedGrant = null;
        }
        internal bool TrySelectSourceGrant(int slot, out SourceReadGrant grant, out Logos.Storage.IJournalSourceReadAuthorization authorization)
        {
            CheckOwner();
            grant = null;
            authorization = null;
            if (stopped || clearGate || Status != TransferStoreStatus.Ready || slot < 0 || slot > 3 || sourceSelection == ulong.MaxValue)
                return false;
            var existing = sourceCredits[slot];
            if (existing == null || !existing.Valid || !GrantMatches(Durable, existing, true))
                return false;
            ChangeSourceSelection(existing);
            grant = existing;
            authorization = new SelectedSourceRead(this, existing, sourceSelection);
            return true;
        }
        private static bool GrantMatches(TransferState state, SourceReadGrant grant, bool forRead = false)
        {
            if (state.CleanupGeneration != grant.CleanupGeneration)
                return false;
            for (int i = 0; i < 4; i++)
            {
                var row = state.Slot(i);
                if (row != null && i == grant.Slot && row.Generation == grant.SlotGeneration && row.Revision == grant.Revision && SameRequest(row.Request, grant.Request) && row.Revision > state.RevokedThrough && (row.State == TransferTaskState.Queued || row.State == TransferTaskState.Exporting || (!forRead && (row.State == TransferTaskState.Pending || row.State == TransferTaskState.Retry || (row.State == TransferTaskState.Blocked && row.Reason == TransferTerminalReason.TooLarge)))))
                    return true;
            }
            return false;
        }
        internal bool ConsumeGrant(SourceReadGrant grant, long bytes)
        {
            if (Status == TransferStoreStatus.Disposed)
                return false;
            CheckOwner();
            if (Status != TransferStoreStatus.Ready || stopped || clearGate || bytes <= 0 || grant == null || !grant.Valid || !ReferenceEquals(activeGrant, grant) || Durable == null || journal.CleanupGeneration != grant.CleanupGeneration || !GrantMatches(Durable, grant, true) || bytes > grant.Remaining)
                return false;
            grant.Consume(bytes);
            return true;
        }
        internal bool TryTakeSourceGrant(out SourceReadGrant grant)
        {
            CheckOwner();
            grant = null;
            if (stopped || clearGate || Status != TransferStoreStatus.Ready || issuedGrant == null)
                return false;
            if (journal.CleanupGeneration != issuedGrant.CleanupGeneration)
            {
                InvalidateGrant();
                return false;
            }
            journal.CheckTransferOwner();
            grant = issuedGrant;
            issuedGrant = null;
            ChangeSourceSelection(grant);
            return true;
        }
        internal bool RequestSourceGrant(int slot, TransferSaveContext context, ExportBookmark authenticatedProgress = null, bool identityCoverageGap = false)
        {
            CheckOwner();
            if (stopped || clearGate || Status != TransferStoreStatus.Ready || slot < 0 || slot > 3 || Durable == null || Durable.UnknownUsage)
                return false;
            var row = Durable.Slot(slot);
            if (row == null || (row.State != TransferTaskState.Queued && row.State != TransferTaskState.Exporting))
                return false;
            long remaining = row.Request.Limits.SourceBytes - row.SourceUsed;
            if (remaining <= 0)
                return false;
            long chunk = Math.Min(remaining, 1048576L);
            var rows = Rows(Durable);
            rows[slot] = CopyTask(row, sourceUsed: row.SourceUsed + chunk, bookmark: authenticatedProgress, replaceBookmark: authenticatedProgress != null, identityCoverageGap: identityCoverageGap);
            if (!RequestSave(CopyState(Durable, rows, autoBookmark: authenticatedProgress, replaceAutoBookmark: row.AutoError && authenticatedProgress != null), context))
                return false;
            sourceReservationExisting = sourceCredits[slot];
            sourceReservationSlot = slot;
            sourceReservationAmount = chunk;
            return true;
        }
    }
}
