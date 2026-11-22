namespace Logos.Storage
{
    // 会话状态说明能否工作，阻塞原因说明恢复所缺的条件；追加结果单独描述这一次提交的确定性。
    public enum JournalStatus
    {
        New,
        Recovering,
        Ready,
        DirectoryBusy,
        Blocked,
        Disposed,
    }

    public enum JournalBlockReason
    {
        None,
        LeaseUnavailable,
        LeaseIdentityChanged,
        IoFailed,
        GlobalStateInvalid,
        KeyUnavailable,
        ShortHeader,
        HeaderInvalid,
        PolicyMismatch,
        DataMissing,
        IncompleteTail,
        BlockInvalid,
        TruncateFailed,
        MetadataLimit,
        UnknownFile,
        KnownCommitMissing,
        TimeUnconfirmed,
        StateInvalid,
        UnsupportedState,
    }

    public enum JournalAppendResult
    {
        Committed,
        QuotaClosed,
        DirectoryLimit,
        MetadataLimit,
        StaleDay,
        NotReady,
        IoFailed,
        Uncertain,
        InvalidBlock,
    }
}
