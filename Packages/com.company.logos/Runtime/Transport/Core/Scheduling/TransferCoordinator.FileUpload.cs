using System;
using System.Threading.Tasks;
using Logos.Storage;

namespace Logos.Transport
{
    internal sealed partial class TransferCoordinator
    {
        private FileUploadSpool fileSpool;
        private Task fileRecovery;
        private long fileRecoveryRetryAt;
        private bool fileRecoveryFailed;

        private bool FileSpoolUnavailable => service is FileUploadAdapter && (fileRecovery != null || fileSpool.NeedsRecovery);

        // 即使重启后还没有新的上传授权，也清理本会话拥有的孤儿文件。只有原 worker 发布和回收该任务。
        private void MaintainFileSpool(long now)
        {
            if (!FinishFileRecovery(now) || !fileSpool.NeedsRecovery || stopping || clearing
                || journal.Status != JournalStatus.Ready || now < fileRecoveryRetryAt)
                return;
            fileRecovery = Task.Run(fileSpool.Recover);
        }

        private bool FinishFileRecovery(long now)
        {
            if (fileRecovery == null)
                return true;
            if (!fileRecovery.IsCompleted)
                return false;
            fileRecoveryFailed = fileRecovery.IsFaulted;
            if (fileRecoveryFailed)
            {
                _ = fileRecovery.Exception;
                const long delay = 30 * TimeSpan.TicksPerSecond;
                fileRecoveryRetryAt = now > long.MaxValue - delay ? long.MaxValue : now + delay;
            }
            fileRecovery = null;
            return true;
        }

        private long FileRecoveryDelay(long now)
        {
            if (fileRecovery != null)
                return TransferPolicy.PollTicks;
            if (permit != null || stopping || clearing || journal.Status != JournalStatus.Ready)
                return long.MaxValue;
            if (fileSpool == null)
                return journal.HasUploadWorkspace ? 0 : long.MaxValue;
            if (!fileSpool.NeedsRecovery)
                return long.MaxValue;
            return Math.Max(0, fileRecoveryRetryAt - now);
        }

        private bool DispatchFile(FileUploadAdapter.Operation file, long now, JournalStepContext context, IMonotonicClock liveClock)
        {
            // 与撤权、停止和清理共用发布顺序。这里只允许调用快速启动入口，写文件、网络等待和关闭都在门外。
            lock (publication)
            {
                if (context.ShouldYield)
                    return false;
                long handoff = liveClock?.TimestampTicks ?? now;
                if (context.StopRequested || stopping || clearing || permit.IsCancellationRequested
                    || journal.CleanupGeneration != permit.Generation || !time.TryUtc(handoff, out var utc)
                    || utc >= permit.Request.ExpiresUtc || !access.IsAuthorized(permit.Request, permit.Revision, permit.Generation, utc))
                {
                    abandon = closing = true;
                    access.CancelPermit(permit);
                    return false;
                }
                if (background)
                {
                    // 后台不再发起新的传输；恢复前由统一的尝试/退避规则管理。
                    closing = cancelForRetry = true;
                    result = new UploadResult(UploadStatus.Transient);
                    access.CancelPermit(permit);
                    return false;
                }
                file.Dispatch();
                return true;
            }
        }
    }
}
