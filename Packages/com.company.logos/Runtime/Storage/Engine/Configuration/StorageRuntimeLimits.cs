using System;
using System.Numerics;

namespace Logos.Storage
{
    // 槽位和编解码器按这些上限预分配数组。调整块大小还会改变速率桶突发容量和最小可用分区的计算。
    /// <summary>带版本的内部资源策略；不是可变的保留选项。</summary>
    public static class StorageRuntimeLimits
    {
        public const int FileHeaderBytes = 512,
            BlockHeaderBytes = 256,
            TagBytes = 32,
            FooterBytes = 16;
        public const int MaxPlainBlockBytes = 65536,
            MaxWireBlockBytes = 65856,
            MinWireBlockBytes = 384,
            MaxRecordsPerBlock = 256;
        public const int StagingSlotCount = 2,
            MaxPreallocatedWorkspaceBytes = 1024 * 1024;
        public const long SealWaitTicks = 3 * TimeSpan.TicksPerSecond,
            WriteWaitTicks = 2 * TimeSpan.TicksPerSecond;
        public const int WriteBytesPerMinute = 2 * 1024 * 1024,
            NormalWritePercent = 80,
            ErrorWritePercent = 20;
        public const long ControlIntervalTicks = TimeSpan.TicksPerSecond,
            MaintenanceIntervalTicks = 30 * TimeSpan.TicksPerSecond,
            FaultSyncIntervalTicks = 5 * TimeSpan.TicksPerSecond;
        public const int DirectoryEntriesPerStep = 8,
            DeletesPerStep = 1,
            DaySlotBytesPerCopy = 512,
            MaxRetainedDaySlots = 1024;
    }
}
