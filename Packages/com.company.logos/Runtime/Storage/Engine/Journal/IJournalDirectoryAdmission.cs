namespace Logos.Storage
{
    // 一次准入携带本次块和可能新增的文件头大小，目录配额可以在任何实际写入前核对两项开销。
    /// <summary>B2 负责实际目录核算。实现必须限制工作量，并保守计入未知物理字节。</summary>
    public interface IJournalDirectoryAdmission
    {
        bool CanAppend(JournalAdmission request);
    }

    public readonly struct JournalAdmission
    {
        public bool IsErrorClass
        {
            get;
        }
        public int WireBytes
        {
            get;
        }
        public int HeaderBytes
        {
            get;
        }
        public FrozenStorageOptions EffectiveOptions
        {
            get;
        }
        public long DayId
        {
            get;
        }
        public long WriteGeneration
        {
            get;
        }

        public JournalAdmission(
            bool error,
            int wire,
            bool needsHeader,
            FrozenStorageOptions options,
            long day,
            long writeGeneration
        )
        {
            IsErrorClass = error;
            WireBytes = wire;
            HeaderBytes = needsHeader ? 512 : 0;
            EffectiveOptions = options;
            DayId = day;
            WriteGeneration = writeGeneration;
        }
    }
}
