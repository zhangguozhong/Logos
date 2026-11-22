using System;
using System.IO;

namespace Logos.Storage
{
    public sealed partial class JournalSession
    {
        public ClearCoordinator Clear
        {
            get;
            private set;
        }

        private IClearMemoryBarrier clearMemoryBarrier;
        public int ActiveReadLeases => readers.ActiveCount;

        public bool ValidateGeneration(long generation) =>
            global != null
            && generation == global.CleanupGeneration
            && Status != JournalStatus.Disposed;

        public void SetClearMemoryBarrier(IClearMemoryBarrier barrier)
        {
            CheckThread();
            clearMemoryBarrier = barrier;
        }

        public ClearRequestResult RequestClear(ClearScope scope)
        {
            CheckThread();
            if (scope == null)
                throw new ArgumentNullException(nameof(scope));
            if (scope.Kind == 2)
                return ClearRequestResult.UnsupportedScope;
            if (
                Clear.IsActive
                || expiringRow != null
                || ControlKeyRotationStatus == ControlKeyRotationStatus.Pending
            )
                return ClearRequestResult.Busy;
            if (
                lease == null
                || global == null
                || store == null
                || controlKey == null
                || Status == JournalStatus.Disposed
                || disposalStarted
            )
                return ClearRequestResult.Failed;
            VerifyOwner();
            foreach (long day in scope.Days)
                if (day > global.HighestDay)
                    return ClearRequestResult.Failed;
            transferClearIntentVerified = false;
            Clear.Scope = scope;
            Clear.Targets = scope.Kind == 1 ? new long[1024] : (long[])scope.Days.Clone();
            Clear.TargetCount = scope.Kind == 1 ? 0 : scope.Days.Length;
            Clear.ScanSlot = Clear.TargetIndex = Clear.Phase = 0;
            Clear.Row = null;
            Clear.NeedsResync = false;
            // I/O 可能阻塞了会话，而已确认追加仍需要重新同步。
            Clear.AppendResyncPending =
                phase == 5 || (recovery != null && ReferenceEquals(recoveringDay, current));
            Clear.Status = ClearStatus.Pending;
            // 此同步门禁先于第一次载荷写入。
            clearMemoryBarrier?.CloseTargets(scope);
            return ClearRequestResult.Pending;
        }

        private bool IsCurrentClearTarget =>
            Clear.IsActive
            && Clear.Scope != null
            && current != null
            && Clear.Scope.Contains(current.DayId);

        private void RestoreClearIntent()
        {
            if (global.ClearPhase != 1)
                return;
            Clear.Targets = store.ReadPayload(global);
            Clear.TargetCount = Clear.Targets.Length;
            Clear.Scope =
                global.ClearScope == 1
                    ? ClearScope.AllData
                    : ClearScope.ExplicitWholeDays(Clear.Targets);
            Clear.Phase = 2;
            Clear.TargetIndex = 0;
            Clear.Status = ClearStatus.Pending;
            clearMemoryBarrier?.CloseTargets(Clear.Scope);
        }

        public void StepClear(JournalStepContext context)
        {
            CheckThread();
            if (Status == JournalStatus.Disposed || disposalStarted)
                throw new ObjectDisposedException(nameof(JournalSession));
            if (context.ShouldYield || !Clear.IsActive)
                return;
            try
            {
                VerifyOwner();
                if (Clear.AppendResyncPending)
                {
                    // 在任何清理载荷或墓碑替换追加检查点前，先解决其滞后状态。
                    if (Clear.NeedsResync)
                    {
                        recovery?.Dispose();
                        recovery = null;
                        global = store.ReadGlobal();
                        if (global.ClearPhase == 1)
                            throw new JournalBlockedException(JournalBlockReason.StateInvalid);
                        phase = 5;
                        Clear.NeedsResync = false;
                        return;
                    }
                    if (phase == 5)
                        BeginStateResync();
                    else if (recovery != null)
                        RecoverOneBlock();
                    if (phase != 5 && recovery == null)
                        Clear.AppendResyncPending = false;
                    return;
                }
                if (Clear.NeedsResync)
                {
                    transferClearIntentVerified = false;
                    global = store.ReadGlobal();
                    Clear.NeedsResync = false;
                    if (global.ClearPhase == 1)
                        RestoreClearIntent();
                    else if (
                        global.ClearPhase == 2
                        && store.VerifyCompletedBoth(global.CleanupGeneration)
                    )
                        FinishClear();
                    else
                    {
                        Clear.Phase = 1;
                        Clear.TargetIndex = 0;
                    }

                    return;
                }

                if (Clear.Phase == 0)
                {
                    CollectClearTargets(context);
                    return;
                }

                if (Clear.Phase == 1)
                {
                    // 删除之前先发布可恢复的清理目标。进程重启后根据持久意图继续，避免临时请求变成无依据的删除。
                    PublishClearIntent();
                    return;
                }

                if (Clear.Phase == 2)
                {
                    transferClearIntentVerified = false;
                    if (!store.VerifyClearIntentBoth(global))
                    {
                        store.WriteGlobalBoth(global, controlKey);
                        return;
                    }

                    transferClearIntentVerified = true;
                    readers.InvalidateGeneration(global.CleanupGeneration);
                    if (!readers.CloseCancelled(context) || readers.ActiveCount != 0)
                        return;
                    if (recovery != null)
                    {
                        recovery.Dispose();
                        recovery = null;
                        recoveringDay = null;
                        return;
                    }

                    if (Clear.Scope.Contains(global.HighestDay) && writer != null)
                    {
                        writer.Dispose();
                        writer = null;
                        return;
                    }

                    if (
                        clearMemoryBarrier != null
                        && !clearMemoryBarrier.ClearOwnedMemory(global.CleanupGeneration)
                    )
                        return;
                    Wire.Clear(scratch);
                    Clear.Phase = 3;
                    return;
                }

                if (Clear.Phase == 3)
                {
                    AuthorizeClearTarget();
                    return;
                }

                if (Clear.Phase == 4)
                {
                    if (
                        !store.VerifyClearIntentBoth(global)
                        || !store.VerifyDeletionBoth(Clear.Row, 1)
                    )
                        throw new JournalBlockedException(JournalBlockReason.StateInvalid);
                    string path = DayPath(Clear.Row.DayId);
                    if (files.Exists(path))
                        files.DeleteFile(path);
                    Clear.Phase = 5;
                    return; // 此步骤最多只执行这一次删除。
                }

                if (Clear.Phase == 5)
                {
                    Clear.Row.DeletionPhase = 2;
                    store.WriteDay(Clear.Row, controlKey, true);
                    if (!store.VerifyDeletionBoth(Clear.Row, 2))
                        throw new IOException("Deletion checkpoint verification failed.");
                    CompleteDeletedRow(Clear.Row);
                    Clear.TargetIndex++;
                    Clear.Row = null;
                    Clear.Phase = 3;
                    return;
                }

                if (Clear.Phase == 6)
                {
                    DeleteUnknownClearPage(context);
                    return;
                }

                if (Clear.Phase == 7)
                {
                    global.ClearPhase = 2;
                    global.ClearScope = 0;
                    global.PayloadLength = global.TargetCount = 0;
                    global.PayloadGeneration = 0;
                    Array.Clear(global.PayloadHash, 0, 32);
                    store.WriteGlobalBoth(global, controlKey);
                    if (!store.VerifyCompletedBoth(global.CleanupGeneration))
                        throw new IOException("Clear completion verification failed.");
                    FinishClear();
                }
            }
            catch (JournalBlockedException ex)
            {
                Block(ex.Reason);
                Clear.Status = ClearStatus.Failed;
            }
            catch (StorageFormatException)
            {
                Block(JournalBlockReason.StateInvalid);
                Clear.Status = ClearStatus.Failed;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                transferClearIntentVerified = false;
                Clear.NeedsResync = true;
                Clear.Status = ClearStatus.Pending;
            }
        }

        private void CollectClearTargets(JournalStepContext context)
        {
            if (Clear.Scope.Kind == 0)
            {
                Clear.Phase = 1;
                return;
            }

            int n = 0;
            while (Clear.ScanSlot < 1024 && n++ < 8 && !context.ShouldYield)
            {
                var row = store.ReadDay(Clear.ScanSlot++, out bool evidence);
                if (row == null)
                {
                    if (evidence)
                        throw new JournalBlockedException(JournalBlockReason.StateInvalid);
                    continue;
                }

                if (
                    row.CleanupGeneration > global.CleanupGeneration
                    || !Wire.Equal(row.DirectoryId, global.DirectoryId)
                )
                    throw new JournalBlockedException(JournalBlockReason.StateInvalid);
                Clear.Targets[Clear.TargetCount++] = row.DayId;
            }

            if (Clear.ScanSlot == 1024)
            {
                Array.Sort(Clear.Targets, 0, Clear.TargetCount);
                Clear.Phase = 1;
            }
        }

        private void PublishClearIntent()
        {
            // 复用载荷区前先确认上次完成状态的两个副本；否则崩溃后旧意图可能去读取新请求刚覆盖的目标列表。
            if (global.ClearPhase == 2 && !store.VerifyCompletedBoth(global.CleanupGeneration))
            {
                store.WriteGlobalBoth(global, controlKey);
                return;
            }

            global.CleanupGeneration = checked(global.CleanupGeneration + 1);
            byte[] payload = ClearPayload.Encode(
                global,
                Clear.Scope.Kind,
                Clear.Targets,
                Clear.TargetCount
            );
            try
            {
                store.WritePayload(payload);
                global.ClearPhase = 1;
                global.ClearScope = Clear.Scope.Kind;
                global.PayloadLength = payload.Length;
                global.PayloadGeneration = global.CleanupGeneration;
                global.TargetCount = Clear.TargetCount;
                global.PayloadHash = ClearPayload.Hash(payload);
                store.WriteGlobalBoth(global, controlKey);
            }
            finally
            {
                Wire.Clear(payload);
            }

            readers.InvalidateGeneration(global.CleanupGeneration);
            Clear.Phase = 2;
        }

        private void AuthorizeClearTarget()
        {
            if (Clear.TargetIndex == Clear.TargetCount)
            {
                Clear.Phase = Clear.Scope.Kind == 1 ? 6 : 7;
                return;
            }

            long day = Clear.Targets[Clear.TargetIndex];
            var row = store.ReadDay((int)(day % 1024), out bool evidence);
            if (row == null)
            {
                if (evidence || files.Exists(DayPath(day)) || day == global.HighestDay)
                    throw new JournalBlockedException(JournalBlockReason.StateInvalid);
                Clear.TargetIndex++;
                return;
            }

            if (
                row.DayId != day
                || row.CleanupGeneration > global.CleanupGeneration
                || !Wire.Equal(row.DirectoryId, global.DirectoryId)
            )
                throw new JournalBlockedException(JournalBlockReason.StateInvalid);
            row.CleanupGeneration = global.CleanupGeneration;
            row.DeletionGeneration = global.CleanupGeneration;
            row.DeletionKind = 2;
            row.DeletionPhase = 1;
            row.ExpiryConfirmedDay = 0;
            row.Tombstone = true;
            row.Intent = null;
            Clear.Row = row;
            store.WriteDay(row, controlKey, true);
            Clear.Phase = 4;
        }

        private void CompleteDeletedRow(JournalDayState row)
        {
            SaveSummary(row);
            if (directoryQuota.IsReady)
                directoryQuota.ObserveDay(row.DayId, 0, 0, 0);
            if (row.DayId != global.HighestDay)
                return;
            current = row;
            // 清理当天文件后，物理占用可以下降，但当日分区已经确认的用量要保留，两类写入都关闭至下一日志日。
            quota?.SetRecoveredUsage(row.NormalBytes, row.ErrorBytes);
            quota?.CloseBoth();
            CurrentHeader = null;
            dataKey?.Dispose();
            dataKey = null;
        }

        private void DeleteUnknownClearPage(JournalStepContext context)
        {
            if (!store.VerifyClearIntentBoth(global))
                throw new JournalBlockedException(JournalBlockReason.StateInvalid);
            if (Clear.UnknownEntries == null)
                Clear.UnknownEntries = files.EnumerateEntries(lease.CanonicalPath).GetEnumerator();
            for (int n = 0; n < 8 && !context.ShouldYield; n++)
            {
                if (!Clear.UnknownEntries.MoveNext())
                {
                    Clear.UnknownEntries.Dispose();
                    Clear.UnknownEntries = null;
                    Clear.Phase = 7;
                    return;
                }

                string path = Clear.UnknownEntries.Current;
                string name = Path.GetFileName(path);
                if (name == ".owner.lock" || name == ".journal-state")
                    continue;
                if (name == DirectoryQuota.UploadDirectoryName)
                {
                    if (!UploadWorkspace.IsSupported)
                        throw new JournalBlockedException(JournalBlockReason.UnknownFile);
                    bool emptied = false;
                    try
                    {
                        emptied = CleanUploadPage(true);
                    }
                    finally
                    {
                        // 枚举器已越过目录。失败或仍有下一页时，重试必须重新遇到它。
                        if (!emptied)
                        {
                            Clear.UnknownEntries.Dispose();
                            Clear.UnknownEntries = null;
                        }
                    }
                    if (!emptied)
                        return;
                    continue;
                }
                var info = files.GetEntryInfo(path);
                if (!info.IsRegularFile)
                    throw new JournalBlockedException(JournalBlockReason.UnknownFile);
                files.DeleteFile(path);
                // AllData 可能遇到所有者已观察核算之外创建的文件；稍后再扫描一次。
                directoryQuota.RestartScan();
                return;
            }
        }

        private void FinishClear()
        {
            transferClearIntentVerified = false;
            Clear.UnknownEntries?.Dispose();
            Clear.UnknownEntries = null;
            Clear.Row = null;
            Clear.Status = ClearStatus.Completed;
            if (Status != JournalStatus.Ready && Status != JournalStatus.Disposed)
            {
                Status = JournalStatus.Recovering;
                phase = 8;
            }

            if (Status == JournalStatus.Ready && !directoryQuota.IsReady)
            {
                Status = JournalStatus.Recovering;
                phase = 6;
            }
        }
    }
}
