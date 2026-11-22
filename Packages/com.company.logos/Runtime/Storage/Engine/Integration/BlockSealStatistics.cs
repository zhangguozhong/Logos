namespace Logos.Storage
{
    /// <summary>首个封块请求的原因；仅观测，不改变时间、容量或持久化策略。</summary>
    public enum BlockSealReason
    {
        None, RecordCapacity, ByteCapacity, Time, FirstError, Background, Stop, DayTransition, IdentityBoundary, Backpressure, Explicit
    }
    /// <summary>固定大小的累计值快照；封块与提交分开，不保留记录或逐块列表。</summary>
    public struct BlockSealStatistics
    {
        private long recordCapacity, byteCapacity, time, firstError, background, stop, dayTransition, identityBoundary, backpressure, explicitRequest;
        public long SealedBlocks
        {
            get;
            private set;
        }
        public long SealedRecords
        {
            get;
            private set;
        }
        public long SealedPlainBytes
        {
            get;
            private set;
        }
        public long SealedWireBytes
        {
            get;
            private set;
        }
        public long SealFailures
        {
            get;
            private set;
        }
        // 按明文容量划分的互斥桶；四个计数之和等于成功封块数。
        public long BlocksUpToQuarter
        {
            get;
            private set;
        }
        public long BlocksQuarterToHalf
        {
            get;
            private set;
        }
        public long BlocksHalfToThreeQuarters
        {
            get;
            private set;
        }
        public long BlocksAboveThreeQuarters
        {
            get;
            private set;
        }
        public long CommittedBlocks
        {
            get;
            private set;
        }
        public long CommittedRecords
        {
            get;
            private set;
        }
        public long CommittedPlainBytes
        {
            get;
            private set;
        }
        public long CommittedWireBytes
        {
            get;
            private set;
        }
        public long GetSealedBlocks(BlockSealReason reason)
        {
            switch (reason)
            {
                case BlockSealReason.RecordCapacity:
                    return recordCapacity;
                case BlockSealReason.ByteCapacity:
                    return byteCapacity;
                case BlockSealReason.Time:
                    return time;
                case BlockSealReason.FirstError:
                    return firstError;
                case BlockSealReason.Background:
                    return background;
                case BlockSealReason.Stop:
                    return stop;
                case BlockSealReason.DayTransition:
                    return dayTransition;
                case BlockSealReason.IdentityBoundary:
                    return identityBoundary;
                case BlockSealReason.Backpressure:
                    return backpressure;
                case BlockSealReason.Explicit:
                    return explicitRequest;
                default:
                    return 0;
            }
        }
        internal void RecordSeal(BlockSealReason reason, int records, int plainBytes, int wireBytes)
        {
            SealedBlocks++;
            SealedRecords += records;
            SealedPlainBytes += plainBytes;
            SealedWireBytes += wireBytes;
            if (plainBytes <= StorageRuntimeLimits.MaxPlainBlockBytes / 4)
                BlocksUpToQuarter++;
            else if (plainBytes <= StorageRuntimeLimits.MaxPlainBlockBytes / 2)
                BlocksQuarterToHalf++;
            else if (plainBytes <= StorageRuntimeLimits.MaxPlainBlockBytes * 3 / 4)
                BlocksHalfToThreeQuarters++;
            else
                BlocksAboveThreeQuarters++;
            switch (reason)
            {
                case BlockSealReason.RecordCapacity:
                    recordCapacity++;
                    break;
                case BlockSealReason.ByteCapacity:
                    byteCapacity++;
                    break;
                case BlockSealReason.Time:
                    time++;
                    break;
                case BlockSealReason.FirstError:
                    firstError++;
                    break;
                case BlockSealReason.Background:
                    background++;
                    break;
                case BlockSealReason.Stop:
                    stop++;
                    break;
                case BlockSealReason.DayTransition:
                    dayTransition++;
                    break;
                case BlockSealReason.IdentityBoundary:
                    identityBoundary++;
                    break;
                case BlockSealReason.Backpressure:
                    backpressure++;
                    break;
                default:
                    explicitRequest++;
                    break;
            }
        }
        internal void RecordFailure() => SealFailures++;
        // 只有追加成功才把同一块计入提交统计；此前封块时记录的利用率也包括最终未提交的块。
        internal void RecordCommit(int records, int plainBytes, int wireBytes)
        {
            CommittedBlocks++;
            CommittedRecords += records;
            CommittedPlainBytes += plainBytes;
            CommittedWireBytes += wireBytes;
        }
    }
}
