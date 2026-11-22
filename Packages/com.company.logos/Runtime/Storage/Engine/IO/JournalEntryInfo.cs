namespace Logos.Storage
{
    // 枚举只提供物理长度和普通文件标记。判断哪些字节属于已提交日志，还需要对应日期的认证信息。
    public readonly struct JournalEntryInfo
    {
        public long Length
        {
            get;
        }
        public bool IsRegularFile
        {
            get;
        }

        public JournalEntryInfo(long length, bool regular)
        {
            Length = length;
            IsRegularFile = regular;
        }
    }
}
