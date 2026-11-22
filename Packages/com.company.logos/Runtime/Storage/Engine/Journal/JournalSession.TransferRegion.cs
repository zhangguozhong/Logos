using System;
namespace Logos.Storage
{
    // 传输页位于共享状态文件内，其读取、写入与刷新都通过 Journal 的所有者执行，清理时还需服从代际检查。
    public sealed partial class JournalSession
    {
        private bool transferClearIntentVerified;
        private UploadWorkspace uploadCleanup;
        private bool uploadCleanupForClear;

        // 清理屏障等待后台恢复退出后，AllData 才接管。不同代际不能复用半途的目录游标。
        internal bool CleanUploadPage(bool forClear)
        {
            if (uploadCleanup != null && uploadCleanupForClear != forClear)
                CloseUploadCleanup(uploadCleanupForClear);
            try
            {
                if (uploadCleanup == null)
                {
                    uploadCleanupForClear = forClear;
                    uploadCleanup = new UploadWorkspace(GetUploadFileOwner(), false);
                }
                if (!uploadCleanup.CleanOrphanPage(directoryQuota))
                    return false;
                CloseUploadCleanup(forClear);
                return true;
            }
            catch
            {
                CloseUploadCleanup(forClear);
                throw;
            }
        }

        internal void CloseUploadCleanup(bool forClear)
        {
            if (uploadCleanupForClear == forClear)
            {
                uploadCleanup?.Dispose();
                uploadCleanup = null;
            }
        }

        internal IDirectoryLease GetUploadFileOwner()
        {
            // 调度器等待后台文件操作结束后，才允许释放此租约。
            var owner = lease;
            if (owner == null || !owner.ValidateIdentity())
                throw new System.IO.IOException("Upload directory ownership unavailable.");
            return owner;
        }
        internal bool TryReserveUploadFile(int bytes)
        {
            CheckThread();
            return Status == JournalStatus.Ready && !disposalStarted
                && directoryQuota.TryReserveUpload(bytes, EffectiveOptions);
        }
        internal void ClearUploadFileCharge() => directoryQuota.ClearUploadCharge();

        internal bool TransferRegionAvailable => !disposalStarted && store != null && global != null
            && (Status == JournalStatus.Ready || (Clear.IsActive && Clear.Phase == 2));
        internal void CheckTransferOwner()
        {
            CheckThread();
            if (disposalStarted || Status == JournalStatus.Disposed)
                throw new ObjectDisposedException(nameof(JournalSession));
            VerifyOwner();
            if (!TransferRegionAvailable)
                throw new InvalidOperationException("Journal transfer region is not ready.");
        }
        internal byte[] CopyTransferDirectoryId()
        {
            CheckTransferOwner();
            return (byte[])global.DirectoryId.Clone();
        }
        internal long TransferEffectiveDay
        {
            get
            {
                CheckTransferOwner();
                return global.HighestDay;
            }
        }
        internal bool TransferDayConfirmed
        {
            get
            {
                CheckTransferOwner();
                return global.TimeConfirmed && (dayClock == null || !dayClock.Decision.RequiresConfirmation);
            }
        }
        internal bool TransferClearCompleted
        {
            get
            {
                CheckTransferOwner();
                return global.ClearPhase == 2 && !Clear.IsActive;
            }
        }
        // 仅在 Journal 检查所有权后，由原工作线程屏障调用。
        // 组合回调内部不得查询文件系统身份。
        internal bool TransferClearProofInMemory => TransferRegionAvailable && transferClearIntentVerified && Clear.IsActive && Clear.Phase == 2 && !Clear.NeedsResync && global.ClearPhase == 1;
        internal bool TransferClearProof
        {
            get
            {
                CheckTransferOwner();
                return TransferClearProofInMemory;
            }
        }
        internal void ReadTransferPage(int slot, byte[] page)
        {
            CheckTransferOwner();
            try
            {
                store.ReadTransferPage(slot, page);
            }
            catch
            {
                transferClearIntentVerified = false;
                throw;
            }
        }
        internal void WriteTransferPage(int slot, byte[] page, int count)
        {
            CheckTransferOwner();
            try
            {
                store.WriteTransferPage(slot, page, count);
            }
            catch
            {
                transferClearIntentVerified = false;
                throw;
            }
        }
        internal void FlushTransferPage()
        {
            CheckTransferOwner();
            try
            {
                store.FlushTransferPage();
            }
            catch
            {
                transferClearIntentVerified = false;
                throw;
            }
        }
    }
}
