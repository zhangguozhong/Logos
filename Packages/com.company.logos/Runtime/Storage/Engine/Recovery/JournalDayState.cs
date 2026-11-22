namespace Logos.Storage
{
    // 每日检查点保存提交下界、分类用量和可能未完成的追加或删除。槽位复用时还需区分日期和清理代际。
    internal sealed class JournalDayState
    {
        internal ushort SchemaVersion = 2;
        internal byte DeletionKind;
        internal byte DeletionPhase;
        internal long ExpiryConfirmedDay;
        internal long DeletionGeneration;
        internal ulong Sequence;
        internal long DayId;
        internal long FileLimit;
        internal long ErrorLimit;
        internal long PolicyVersion;
        internal long CleanupGeneration;
        internal long CommittedEnd;
        internal long NormalBytes;
        internal long ErrorBytes;
        internal bool Exists;
        internal bool Tombstone;
        internal bool Clean;
        internal string DataKeyId;
        internal string ControlKeyId;
        internal byte[] Epoch = new byte[16];
        internal byte[] HeaderHash = new byte[32];
        internal byte[] DirectoryId = new byte[16];
        internal AppendIntent Intent;
    }

    // 不保存块列表：每个固定元数据槽位仅保留一份小摘要。
    internal struct JournalDaySummary
    {
        internal bool Valid;
        internal bool Tombstone;
        internal byte DeletionPhase;
        internal byte DeletionKind;
        internal long DayId;
        internal long ConfirmedEnd;
        internal long NormalBytes;
        internal long ErrorBytes;
        internal long CleanupGeneration;
    }
}
