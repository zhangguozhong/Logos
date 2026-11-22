using System;
using Logos.Storage;
namespace Logos.Transport
{
    internal sealed partial class TransferCoordinator
    {
        private TransferAuthorizationSnapshot autoSnapshot;
        private string autoBinding;
        private long autoScanDay = -1, nextAutoProbe, autoObservedDay = -1, autoObservedEnd;
        private bool preferAuto = true, autoCoverageGap, autoBindingInvalid;
        private bool AutomaticEnabled => policy.AutoLimits != null;
        private void ResetAutomaticMemory()
        {
            autoSnapshot = null;
            autoBinding = null;
            autoScanDay = autoObservedDay = -1;
            nextAutoProbe = autoObservedEnd = 0;
            preferAuto = true;
            autoCoverageGap = autoBindingInvalid = false;
        }
        private bool AutoProof(TaskSlot row) => row != null && row.AutoError && row.Revision == current.Revision && row.CleanupGeneration == journal.CleanupGeneration && row.Request.TaskId == AutomaticAuthorizationBinding.TaskId(autoBinding, row.CleanupGeneration, row.Generation);
        private bool DriveAutomatic(long now, DateTime utc, TransferSaveContext ctx)
        {
            if (!AutomaticEnabled)
            {
                var disabled = store.Durable.Slot(3);
                if (disabled != null && disabled.AutoError && IsRunnable(disabled))
                {
                    Terminate(3, TransferTaskState.Canceled, TransferTerminalReason.Cancelled, ctx);
                    return true;
                }
                return false;
            }
            if (background || current == null || current.DomainCount == 0)
                return false;
            if (!ReferenceEquals(autoSnapshot, current))
            {
                string binding;
                try
                {
                    binding = AutomaticAuthorizationBinding.Hash(current);
                    autoBindingInvalid = false;
                }
                catch (System.Text.EncoderFallbackException)
                {
                    binding = null;
                    autoBindingInvalid = true;
                }
                if (binding != autoBinding)
                {
                    autoBinding = binding;
                    autoScanDay = -1;
                    nextAutoProbe = 0;
                    autoObservedDay = -1;
                }
                autoSnapshot = current;
                if (autoBindingInvalid)
                {
                    nextAutoProbe = long.MaxValue;
                    reason = "InvalidAutomaticAuthorization";
                }
            }
            var old = store.Durable.Slot(3);
            // 恢复的自动任务需重新匹配当前授权绑定和清理代数，成功后才可复用其扫描进度。
            bool proof = AutoProof(old);
            if (old != null && !proof && IsRunnable(old))
            {
                Terminate(3, TransferTaskState.Canceled, TransferTerminalReason.ReauthorizationRequired, ctx);
                return true;
            }
            if (autoBindingInvalid)
                return false;
            bool authorizedError = false;
            for (int i = 0; i < current.DomainCount; i++)
                authorizedError |= (current.DomainScope(i).LevelMask & 48) != 0;
            if (!authorizedError)
            {
                nextAutoProbe = long.MaxValue;
                return false;
            }
            if (old != null && IsRunnable(old))
                return false;
            if (now < nextAutoProbe)
                return false;
            journal.GetTransferScanBounds(out long first, out long last);
            if (autoScanDay < first || autoScanDay > last)
                autoScanDay = proof ? Math.Max(first, old.Request.FirstQuotaDay) : first;
            long day = autoScanDay;
            var info = journal.GetTransferSummary(day, out long end, out long errors);
            bool policyCanceled = proof && old.State == TransferTaskState.Canceled && old.Reason == TransferTerminalReason.Cancelled;
            bool unchanged = proof && !policyCanceled && old.Request.FirstQuotaDay == day && ((old.Bookmark != null && end <= old.Bookmark.CapturedEnd) || (autoObservedDay == day && end <= autoObservedEnd));
            bool scanned = unchanged && old.State == TransferTaskState.Complete;
            if (unchanged && !scanned)
            {
                autoCoverageGap = true;
                reason = "AutoCoverageGap";
                autoScanDay = day < last ? day + 1 : day;
                nextAutoProbe = day < last ? now : now > long.MaxValue - 30 * TimeSpan.TicksPerSecond ? long.MaxValue : now + 30 * TimeSpan.TicksPerSecond;
                return false;
            }
            if (info != JournalTransferDayInfo.Available || errors == 0 || scanned)
            {
                if (info == JournalTransferDayInfo.Missing || info == JournalTransferDayInfo.Expired)
                {
                    autoCoverageGap = true;
                    reason = "AutoCoverageGap";
                }
                if (day < last)
                {
                    autoScanDay = day + 1;
                    nextAutoProbe = now;
                }
                else
                {
                    autoScanDay = day;
                    nextAutoProbe = now > long.MaxValue - TimeSpan.TicksPerSecond ? long.MaxValue : now + TimeSpan.TicksPerSecond;
                }
                return false;
            }
            ulong generation = store.Durable.NextSlotGeneration;
            if (generation == ulong.MaxValue)
            {
                nextAutoProbe = long.MaxValue;
                reason = "AutoGenerationExhausted";
                return false;
            }
            DateTime start = current.DomainScope(0).StartUtc, finish = current.DomainScope(0).EndUtc;
            for (int i = 1; i < current.DomainCount; i++)
            {
                var scope = current.DomainScope(i);
                if (scope.StartUtc < start)
                    start = scope.StartUtc;
                if (scope.EndUtc > finish)
                    finish = scope.EndUtc;
            }
            DateTime expires = current.ExpiresUtc;
            long ceiling = utc.Ticks > DateTime.MaxValue.Ticks - TimeSpan.TicksPerMinute * 30 ? DateTime.MaxValue.Ticks : utc.Ticks + TimeSpan.TicksPerMinute * 30;
            if (expires.Ticks > ceiling)
                expires = new DateTime(ceiling, DateTimeKind.Utc);
            var request = new RetrievalRequest(AutomaticAuthorizationBinding.TaskId(autoBinding, journal.CleanupGeneration, generation), destination, installation, day, 1, new RetrievalScope(start, finish, 48), expires, policy.AutoLimits);
            ExportBookmark bookmark = null;
            var previous = store.Durable.AutoBookmark;
            if (proof && previous != null && previous.Day == day && previous.CapturedEnd <= end)
                bookmark = new ExportBookmark(day, end, previous.CopyHeaderHash(), previous.BlockOffset, previous.NextRecordIndex, previous.CleanupGeneration);
            var rows = Rows();
            rows[3] = new TaskSlot(request, generation, current.Revision, journal.CleanupGeneration, autoError: true, bookmark: bookmark);
            if (!store.RequestSave(Copy(rows, nextGeneration: generation + 1, autoBookmark: proof ? previous : null, replaceAutoBookmark: true), ctx))
                return false;
            autoObservedDay = day;
            autoObservedEnd = end;
            countGeneration[3] = generation;
            countsKnown[3] = true;
            ackRecords[3] = ackBatches[3] = 0;
            reason = "AutoQueued";
            return true;
        }
    }
}
