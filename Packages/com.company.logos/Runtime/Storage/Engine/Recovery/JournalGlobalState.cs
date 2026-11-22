namespace Logos.Storage
{
    // 全局副本把目录身份、已接受的最高日期和清理载荷摘要关联起来；跨重启继续清理时使用这里发布的代际。
    internal sealed class JournalGlobalState
    {
        internal ushort SchemaVersion = 2;
        internal ulong Sequence;
        internal byte ClearPhase;
        internal byte ClearScope;
        internal int PayloadLength;
        internal long PayloadGeneration;
        internal byte[] PayloadHash = new byte[32];
        internal int TargetCount;
        internal byte[] DirectoryId = new byte[16];
        internal long HighestDay;
        internal bool TimeConfirmed = true;
        internal long CleanupGeneration;
        internal long FileLimit;
        internal long ErrorLimit;
        internal long PolicyVersion;
        internal string ControlKeyId;
    }
}
