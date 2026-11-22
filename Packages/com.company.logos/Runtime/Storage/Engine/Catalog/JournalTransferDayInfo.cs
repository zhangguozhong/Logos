namespace Logos.Storage
{
    // 传输扫描根据按日摘要选择候选日期，读取块时另开游标，以便清理流程能追踪实际读者。
    internal enum JournalTransferDayInfo
    {
        Empty, Available, Missing, Expired
    }
    public sealed partial class JournalSession
    {
        internal void GetTransferScanBounds(out long first, out long last)
        {
            CheckThread();
            VerifyOwner();
            last = global.HighestDay;
            first = System.Math.Max(0, last - configured.RetentionDays + 1);
        }
        internal JournalTransferDayInfo GetTransferSummary(long day, out long end, out long errorBytes)
        {
            var status = GetTransferDayInfo(day);
            var summary = summaries[day % 1024];
            end = status == JournalTransferDayInfo.Available ? summary.ConfirmedEnd : 0;
            errorBytes = status == JournalTransferDayInfo.Available ? summary.ErrorBytes : 0;
            return status;
        }
        // 只含数值状态；路径、句柄和任意区域访问仍由所有者私有持有。
        internal JournalTransferDayInfo GetTransferDayInfo(long day)
        {
            CheckThread();
            VerifyOwner();
            if (global.TimeConfirmed && RetentionPolicy.IsExpired(day, global.HighestDay, configured.RetentionDays))
                return JournalTransferDayInfo.Expired;
            var summary = summaries[day % 1024];
            if (!summary.Valid || summary.DayId != day)
                return JournalTransferDayInfo.Missing;
            if (summary.Tombstone)
                return summary.DeletionKind == 1 ? JournalTransferDayInfo.Expired : JournalTransferDayInfo.Missing;
            return summary.ConfirmedEnd == 0 ? JournalTransferDayInfo.Empty : JournalTransferDayInfo.Available;
        }
    }
}
