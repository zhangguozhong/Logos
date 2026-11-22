using System;
namespace Logos.Storage
{
    // 传输页位于共享状态文件内，其读取、写入与刷新都通过 Journal 的所有者执行，清理时还需服从代际检查。
    public sealed partial class JournalSession
    {
        private bool transferClearIntentVerified;
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
