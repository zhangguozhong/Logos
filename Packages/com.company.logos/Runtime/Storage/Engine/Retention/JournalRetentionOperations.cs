using System;
using System.Diagnostics;
using System.IO;

namespace Logos.Storage
{
    public sealed partial class JournalSession
    {
        private JournalDayState expiringRow;
        private int expiryPhase;
        private int maintenanceSlot;
        private long maintenanceConfirmedDay;
        private long nextMaintenanceTimestamp;
        private bool maintenanceCycle;
        private bool maintenanceQuotaScan;
        private bool expiryNeedsResync;

        /// <summary>只有显式确认的 UTC 日期才能授权过期；工作仍在所有者工作线程上执行。</summary>
        public void StepMaintenance(long explicitlyConfirmedDay, JournalStepContext context) =>
            StepMaintenanceAt(explicitlyConfirmedDay, context, Stopwatch.GetTimestamp());

        internal void StepMaintenanceAt(
            long explicitlyConfirmedDay,
            JournalStepContext context,
            long timestamp
        )
        {
            CheckThread();
            if (Status == JournalStatus.Disposed)
                throw new ObjectDisposedException(nameof(JournalSession));
            if (
                context.ShouldYield
                || lease == null
                || global == null
                || store == null
                || Clear.IsActive
                || ControlKeyRotationStatus == ControlKeyRotationStatus.Pending
            )
                return;
            if (
                explicitlyConfirmedDay < global.HighestDay
                || explicitlyConfirmedDay > DateTime.MaxValue.Ticks / TimeSpan.TicksPerDay
            )
                throw new ArgumentOutOfRangeException(nameof(explicitlyConfirmedDay));
            try
            {
                VerifyOwner();
                if (explicitlyConfirmedDay > global.HighestDay && expiringRow == null)
                {
                    // 未来维护日期必须先推进会话，并关闭旧写入器。
                    Step(explicitlyConfirmedDay, context);
                    return;
                }
                readers.RequestExpired(timestamp);
                if (expiringRow != null)
                {
                    StepExpiry(context);
                    return;
                }

                if (!maintenanceCycle)
                {
                    if (timestamp < nextMaintenanceTimestamp)
                        return;
                    nextMaintenanceTimestamp =
                        timestamp > long.MaxValue - 30 * Stopwatch.Frequency
                            ? long.MaxValue
                            : timestamp + 30 * Stopwatch.Frequency;
                    maintenanceCycle = true;
                    maintenanceSlot = 0;
                    maintenanceConfirmedDay = explicitlyConfirmedDay;
                    directoryQuota.RestartScan();
                    maintenanceQuotaScan = true;
                    // 在首次观察可能阻塞之前，就让缓冲所有者暂停现有写令牌等待。
                    // 公共或手动调用者保留现有单次调用的进展语义。
                    if (context.IsWorkerControlled)
                        return;
                }

                if (maintenanceQuotaScan)
                {
                    directoryQuota.StepAt(context, timestamp);
                    if (directoryQuota.IsReady)
                        maintenanceQuotaScan = false;
                    return;
                }

                for (int n = 0; n < 8 && maintenanceSlot < 1024 && !context.ShouldYield; n++)
                {
                    var summary = summaries[maintenanceSlot++];
                    if (
                        !summary.Valid
                        || !RetentionPolicy.IsExpired(
                            summary.DayId,
                            maintenanceConfirmedDay,
                            configured.RetentionDays
                        )
                    )
                        continue;
                    if (
                        summary.Tombstone
                        && summary.DeletionKind == 1
                        && summary.DeletionPhase == 2
                    )
                        continue;
                    var row = store.ReadDay((int)(summary.DayId % 1024), out _);
                    if (
                        row == null
                        || row.DayId != summary.DayId
                        || row.CleanupGeneration > global.CleanupGeneration
                    )
                        throw new JournalBlockedException(JournalBlockReason.StateInvalid);
                    BeginExpiry(row, maintenanceConfirmedDay);
                    return;
                }

                if (maintenanceSlot == 1024)
                    maintenanceCycle = false;
            }
            catch (JournalBlockedException ex)
            {
                Block(ex.Reason);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                expiryNeedsResync = expiringRow != null;
            }
        }

        // 过期流程记录确认日期、删除类别和阶段，恢复时据此判断文件缺失是否属于已获授权的删除。
        private void BeginExpiry(JournalDayState row, long confirmedDay)
        {
            if (!RetentionPolicy.IsExpired(row.DayId, confirmedDay, configured.RetentionDays))
                throw new JournalBlockedException(JournalBlockReason.TimeUnconfirmed);
            expiringRow = row;
            maintenanceConfirmedDay = confirmedDay;
            expiryPhase = 0;
        }

        private void StepExpiry(JournalStepContext context)
        {
            if (expiryNeedsResync)
            {
                global = store.ReadGlobal();
                var row = store.ReadDay((int)(expiringRow.DayId % 1024), out _);
                if (row == null || row.DayId != expiringRow.DayId || global.ClearPhase == 1)
                    throw new JournalBlockedException(JournalBlockReason.StateInvalid);
                expiringRow = row;
                expiryPhase = 0;
                expiryNeedsResync = false;
                return;
            }

            if (expiryPhase == 0)
            {
                // 内存中的 BeginExpiry 不是持久授权。先前已认证的意图可修复。
                if (expiringRow.DeletionPhase == 0 && !CanAuthorizeNewTimeState)
                    return;
                expiringRow.Tombstone = true;
                expiringRow.DeletionKind = 1;
                expiringRow.DeletionPhase = 1;
                expiringRow.ExpiryConfirmedDay = maintenanceConfirmedDay;
                expiringRow.DeletionGeneration = expiringRow.CleanupGeneration;
                expiringRow.Intent = null;
                store.WriteDay(expiringRow, controlKey, true);
                expiryPhase = 1;
                return;
            }

            if (expiryPhase == 1)
            {
                readers.CancelAll();
                if (!readers.CloseCancelled(context) || readers.ActiveCount != 0)
                    return;
                if (!store.VerifyDeletionBoth(expiringRow, 1))
                    throw new JournalBlockedException(JournalBlockReason.StateInvalid);
                string path = DayPath(expiringRow.DayId);
                if (files.Exists(path))
                    files.DeleteFile(path);
                expiryPhase = 2;
                return;
            }

            expiringRow.DeletionPhase = 2;
            store.WriteDay(expiringRow, controlKey, true);
            if (!store.VerifyDeletionBoth(expiringRow, 2))
                throw new IOException("Expiry completion verification failed.");
            CompleteDeletedRow(expiringRow);
            expiringRow = null;
        }

        /// <summary>临时元数据或 I/O 限制后的显式所有者重试；绝不重置持久化额度权威状态。</summary>
        public bool RequestRecoveryRetry()
        {
            CheckThread();
            if (Status != JournalStatus.Blocked)
                return false;
            bool transient = BlockReason == JournalBlockReason.IoFailed || BlockReason == JournalBlockReason.MetadataLimit;
            if (!transient && !(BlockReason == JournalBlockReason.LeaseUnavailable
                && lease == null && lastLeaseFailure == DirectoryLeaseStatus.IoError))
                return false;
            if (lease == null)
            {
                // Unsupported、Invalid 和 Busy 结果绝不授权重复获取原生所有权。
                // 不得丢弃任何权威状态或句柄来退回取得所有权之前的状态。
                if (store != null || global != null)
                    return false;
                phase = 0;
                BlockReason = JournalBlockReason.None;
                Status = JournalStatus.New;
                return true;
            }
            if (global == null)
            {
                if (BlockReason != JournalBlockReason.IoFailed)
                    return false;
                // 保留所有者。在另一个有界 Step 中关闭任何部分启动资源，
                // 然后重新打开并认证现有文件；绝不覆盖初始化。
                phase = 10;
                Status = JournalStatus.Recovering;
                return true;
            }
            if (store == null)
                return false;
            phase = 8;
            Status = JournalStatus.Recovering;
            return true;
        }

        private void RetryRecovery()
        {
            CloseAllCatalogs();
            writer?.Dispose();
            writer = null;
            recovery?.Dispose();
            recovery = null;
            dataKey?.Dispose();
            dataKey = null;
            global = store.ReadGlobal();
            RestoreClearIntent();
            current = null;
            slot = 0;
            directoryQuota.RestartScan();
            phase = 2;
            BlockReason = JournalBlockReason.None;
        }
    }
}
