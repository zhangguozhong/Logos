using System;
using System.Threading;
namespace Logos
{
    internal enum HealthMetric
    {
        Attempted, Filtered, RejectedBeforeAccept, Accepted, Queued, InFlight, ConsumedForTest, DroppedAfterAccept, PendingAdmission, WorkerInFlight, StorageInFlight, CommittedRecords, RepresentedRecords
    }
    // 单项读取是原子的。实时快照仅供观察，不是多个计数器的事务。
    // 流水线合并队列和工作线程的指标，不重复统计消费者结果。
    internal sealed class HealthCounters
    {
        internal const int MetricCount = 13;
        internal const int LevelCount = 6;
        internal const int ReasonCount = 32;
        // 指标、等级、原因共同定位一个格子。按原因累加 DroppedAfterAccept 时，仍然保留被丢弃记录的原始等级。
        private readonly long[] values = new long[MetricCount * LevelCount * ReasonCount];
        public void Add(HealthMetric metric, LogLevel level, AdmissionReason reason = AdmissionReason.InvalidInput, long amount = 1)
        {
            int index = Index(metric, level, reason);
            if (amount < 0 && metric != HealthMetric.Queued && metric != HealthMetric.InFlight && metric != HealthMetric.PendingAdmission)
                throw new ArgumentOutOfRangeException(nameof(amount));
            Interlocked.Add(ref values[index], amount);
        }
        internal static int Index(HealthMetric metric, LogLevel level, AdmissionReason reason)
        {
            if ((int)metric < 0 || (int)metric >= MetricCount)
                throw new ArgumentOutOfRangeException(nameof(metric));
            if (level < LogLevel.Trace || level > LogLevel.Fatal)
                throw new ArgumentOutOfRangeException(nameof(level));
            if ((int)reason < 0 || (int)reason >= ReasonCount)
                throw new ArgumentOutOfRangeException(nameof(reason));
            return ((int)metric * LevelCount + (int)level) * ReasonCount + (int)reason;
        }
        internal long Read(HealthMetric metric, LogLevel level, AdmissionReason reason) => Interlocked.Read(ref values[Index(metric, level, reason)]);
        // 这是构造后唯一的分配路径；不输出日志、不调用回调，也不创建动态桶。
        public HealthSnapshot Snapshot()
        {
            var copy = new long[values.Length];
            for (int i = 0; i < values.Length; i++)
                copy[i] = Interlocked.Read(ref values[i]);
            return new HealthSnapshot(copy);
        }
    }
    internal sealed class HealthSnapshot
    {
        private readonly long[] values;
        internal long CoalescedUnityInfo, RecoveredUnityInfoObservations, UnityRepeatSummaryAttempts;
        public long ProducerWakeSets
        {
            get;
            internal set;
        }
        public long ControlWakeSets
        {
            get;
            internal set;
        }
        public long WakeSets => ProducerWakeSets + ControlWakeSets;
        public PipelineCompletionKind CompletionKind
        {
            get;
            internal set;
        }
        internal HealthSnapshot(long[] ownedValues)
        {
            values = ownedValues;
        }
        public long Get(HealthMetric metric, LogLevel level, AdmissionReason reason = AdmissionReason.InvalidInput)
            => values[HealthCounters.Index(metric, level, reason)];
        // 仅在构造尚未发布且由自身持有的快照时调用。
        internal void Set(HealthMetric metric, LogLevel level, long value, AdmissionReason reason = AdmissionReason.InvalidInput)
            => values[HealthCounters.Index(metric, level, reason)] = value;
        public long Total(HealthMetric metric)
        {
            int offset = HealthCounters.Index(metric, LogLevel.Trace, AdmissionReason.InvalidInput);
            long total = 0;
            for (int i = 0; i < HealthCounters.LevelCount * HealthCounters.ReasonCount; i++)
                total += values[offset + i];
            return total;
        }
    }
}
