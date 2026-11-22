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

        /// <summary>上传文件占用，含进行中的写入预留和未成功删除的残留。</summary>
        public long UploadFileBytes { get; }

        /// <summary>为 false 时，已列出的字节只表示可确认的用量下界，目录暂停新增落盘。</summary>
        public bool IsUploadFileUsageKnown { get; }

        internal DirectoryUsage(DirectoryQuota q)
        {
            IsUploadFileUsageKnown = q.IsReady && !q.UploadUsageUnknown;
            UploadFileBytes = q.UploadBytes;
            ActualBytes = q.ActualBytes;
            MetadataBytes = q.MetadataBytes;
            NormalBytes = q.NormalBytes;
            ErrorBytes = q.ErrorBytes;
            UnknownBytes = q.UnknownBytes;
        }
    }
}
