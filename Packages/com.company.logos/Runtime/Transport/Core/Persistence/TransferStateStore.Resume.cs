using Logos.Storage;
namespace Logos.Transport
{
    internal sealed partial class TransferStateStore
    {
        internal sealed class AuthenticatedResume
        {
            private readonly TransferStateStore owner;
            private readonly int slot;
            private readonly TaskSlot row;
            internal ExportBookmark Bookmark => row.Bookmark;
            private AuthenticatedResume(TransferStateStore owner, int slot, TaskSlot row)
            {
                this.owner = owner;
                this.slot = slot;
                this.row = row;
            }
            internal static AuthenticatedResume FromDurable(TransferStateStore owner, int slot)
            {
                owner.CheckOwner();
                if (owner.Status != TransferStoreStatus.Ready || owner.clearGate || owner.stopped || slot < 0 || slot > 3)
                    return null;
                var row = owner.Durable.Slot(slot);
                return row?.Bookmark == null ? null : new AuthenticatedResume(owner, slot, row);
            }
            // 恢复凭据只适用于原日志和仍然匹配的任务、清理代数及书签；使用时再检查一次。
            internal bool IsCurrent(JournalSession journal, RetrievalRequest request, long revision, long generation)
            {
                owner.CheckOwner();
                var current = owner.Durable?.Slot(slot);
                return owner.Status == TransferStoreStatus.Ready && !owner.clearGate && !owner.stopped && ReferenceEquals(owner.journal, journal) && current != null && current.Generation == row.Generation && current.Revision == revision && current.CleanupGeneration == generation && generation == journal.CleanupGeneration && SameRequest(current.Request, request) && SameBookmark(current.Bookmark, row.Bookmark);
            }
        }
        internal AuthenticatedResume GetAuthenticatedResume(int slot)
        {
            return AuthenticatedResume.FromDurable(this, slot);
        }
    }
}
