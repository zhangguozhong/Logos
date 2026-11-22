namespace Logos.Storage
{
    // 上层需区分本次槽位剩余空间不足与单条记录已越过上限：前者可能通过先封存已有记录解决。
    public enum RecordEncodeFailure
    {
        None,
        DestinationTooSmall,
        RecordTooLarge,
        SanitizeFailed,
        InvalidRecord,
    }
}
