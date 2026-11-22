namespace Logos.Storage
{
    // 最小和最大时间、序号描述块内记录的范围；身份上下文必须一致，范围相交本身不能证明两条记录可放进同一块。
    /// <summary>包含一个不可变上下文引用的有界摘要。仅在记录复制成功后更新。</summary>
    public sealed class BlockMetadata
    {
        public long DayId
        {
            get;
        }
        public long PolicyVersion
        {
            get;
        }
        public int RecordCount
        {
            get;
            internal set;
        }
        public bool IsErrorClass
        {
            get;
        }
        public long IdentityContextVersion
        {
            get;
        }
        public long MinimumUtcTicks
        {
            get;
            internal set;
        }
        public long MaximumUtcTicks
        {
            get;
            internal set;
        }
        public long MinimumSequence
        {
            get;
            internal set;
        }
        public long MaximumSequence
        {
            get;
            internal set;
        }
        internal readonly LogContext Context;
        internal readonly int ContextBytes;
        internal byte[] Digests
        {
            get;
        }
        = new byte[128];
        internal byte[] Fingerprint
        {
            get;
        }
        internal byte[] FileHeaderHash
        {
            get;
        }

        internal BlockMetadata(VerifiedFileHeader h, LogRecord r, byte[] fingerprint)
        {
            Context = r.Context;
            ContextBytes = Context.BlockSnapshotBytes;
            FileHeaderHash = (byte[])h.Hash.Clone();
            DayId = h.DayId;
            PolicyVersion = h.PolicyVersion;
            IsErrorClass = r.Level >= LogLevel.Error;
            IdentityContextVersion = r.Context.IdentityContextVersion;
            RecordCount = 1;
            MinimumUtcTicks = MaximumUtcTicks = r.UtcTimestamp.Ticks;
            MinimumSequence = MaximumSequence = r.Sequence;
            Fingerprint = fingerprint;
        }
    }
}
