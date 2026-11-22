namespace Logos.Storage
{
    // ActualBytes 是物理用量，分项用于判断元数据、两类日志和未识别文件对下一次追加的影响。
    public readonly struct DirectoryUsage
    {
        public long ActualBytes
        {
            get;
        }
        public long MetadataBytes
        {
            get;
        }
        public long NormalBytes
        {
            get;
        }
        public long ErrorBytes
        {
            get;
        }
        public long UnknownBytes
        {
            get;
        }

        internal DirectoryUsage(DirectoryQuota q)
        {
            ActualBytes = q.ActualBytes;
            MetadataBytes = q.MetadataBytes;
            NormalBytes = q.NormalBytes;
            ErrorBytes = q.ErrorBytes;
            UnknownBytes = q.UnknownBytes;
        }
    }
}
