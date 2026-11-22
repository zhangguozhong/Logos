using System;
using System.Threading;
namespace Logos
{
    internal enum RateLimitDecision
    {
        InvalidInput, RateLimited, ContentionRejected, Granted
    }
    // 仅使用启动时固定的桶。Granted 只授权准入处理，不表示队列已接收。
    internal sealed class RateLimiter
    {
        private readonly object gate = new object();
        private readonly IMonotonicClock clock;
        private readonly int maximumPayloadBytes;
        private readonly Bucket[] partitions;
        private readonly Bucket[] sources;
        private readonly Bucket[] levels;
        // 从普通分区、Application 普通来源和 Info 等级三份预算同时划出同一份额度。
        private static readonly RateBudget KeyInfoRate = new RateBudget(4, 8192, 4, 8192);
        private Bucket keyInfoBucket;
        // 高频不同 Info 也能耗尽普通分区。连续策略仅在可容纳最大合法记录的大桶内
        // 预留两条、合计 4 KiB 的瞬时余量，Warn 仍须支付原有三层速率额度。
        private const int WarnHeadroomCount = 2, WarnHeadroomBytes = 4096;
        private readonly bool protectWarnings;
        private long lastTicks;
        private struct Bucket
        {
            private readonly RateBudget budget;
            private bool hasWarnHeadroom;
            private long refillTicks;
            private double count;
            private double bytes;
            public Bucket(RateBudget budget)
            {
                this.budget = budget;
                hasWarnHeadroom = false;
                refillTicks = 0;
                count = budget.CountCapacity;
                bytes = budget.ByteCapacity;
            }
            public void Refill(long now)
            {
                double seconds = (now - refillTicks) / (double)TimeSpan.TicksPerSecond;
                refillTicks = now;
                count = Math.Min(budget.CountCapacity, count + seconds * budget.CountsPerSecond);
                bytes = Math.Min(budget.ByteCapacity, bytes + seconds * budget.BytesPerSecond);
            }
            public bool CanTake(int cost) => count >= 1 && bytes >= cost;
            public bool CanTakeWithWarnHeadroom(int cost) => !hasWarnHeadroom ? CanTake(cost)
                : count >= 1 + WarnHeadroomCount && bytes >= cost + (long)WarnHeadroomBytes;
            public void EnableWarnHeadroom(int maximumPayload)
            {
                hasWarnHeadroom = budget.CountCapacity > WarnHeadroomCount && budget.CountsPerSecond > WarnHeadroomCount
                    && budget.ByteCapacity - WarnHeadroomBytes >= maximumPayload && budget.BytesPerSecond > WarnHeadroomBytes;
            }
            // 仅已落盘的故障窗口在交付期间临时为普通 Info 留余量。小桶保留一半容量给正常流量。
            public bool CanSpareForIncident(int cost) => count >= 1 + Math.Min(8, budget.CountCapacity / 2)
                && bytes >= cost + (long)Math.Min(2048, budget.ByteCapacity / 2);
            public void Take(int cost)
            {
                count -= 1;
                bytes -= cost;
            }
        }
        public RateLimiter(LoggingOptions options, IMonotonicClock clock)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
            LoggingOptions frozen = options.Freeze();
            maximumPayloadBytes = LoggingOptions.RatePayloadBytes(frozen.EffectiveRecordByteLimit);
            partitions = new[] { new Bucket(frozen.NormalRate), new Bucket(frozen.ErrorRate) };
            sources = new Bucket[PolicySnapshot.SourceCount * 2];
            levels = new Bucket[6];
            for (int i = 0; i < PolicySnapshot.SourceCount; i++)
            {
                sources[i] = new Bucket(frozen.SourceNormalBudgets[i]);
                sources[PolicySnapshot.SourceCount + i] = new Bucket(frozen.SourceErrorBudgets[i]);
            }
            for (int i = 0; i < levels.Length; i++)
                levels[i] = new Bucket(frozen.LevelBudgets[i]);
            int app = PolicySnapshot.SourceIndex(LogSource.Application);
            int info = (int)LogLevel.Info;
            if (CanPartition(frozen.NormalRate) && CanPartition(frozen.SourceNormalBudgets[app]) && CanPartition(frozen.LevelBudgets[info]))
            {
                partitions[0] = OrdinaryBucket(frozen.NormalRate);
                sources[app] = OrdinaryBucket(frozen.SourceNormalBudgets[app]);
                levels[info] = OrdinaryBucket(frozen.LevelBudgets[info]);
                keyInfoBucket = new Bucket(KeyInfoRate);
            }
            protectWarnings = frozen.ConsecutiveUnityInfo;
            if (protectWarnings)
            {
                partitions[0].EnableWarnHeadroom(maximumPayloadBytes);
                for (int i = 0; i < PolicySnapshot.SourceCount; i++)
                    sources[i].EnableWarnHeadroom(maximumPayloadBytes);
            }
            lastTicks = Math.Max(0, clock.TimestampTicks);
        }
        // 自定义小预算必须仍能接收一条最大合法记录；无法切分时关键事件走普通路径。
        private bool CanPartition(RateBudget b) =>
            b.CountCapacity > KeyInfoRate.CountCapacity && b.ByteCapacity - KeyInfoRate.ByteCapacity >= maximumPayloadBytes
            && b.CountsPerSecond > KeyInfoRate.CountsPerSecond && b.BytesPerSecond > KeyInfoRate.BytesPerSecond;

        private static Bucket OrdinaryBucket(RateBudget b) => new Bucket(new RateBudget(
            b.CountCapacity - KeyInfoRate.CountCapacity, b.ByteCapacity - KeyInfoRate.ByteCapacity,
            b.CountsPerSecond - KeyInfoRate.CountsPerSecond, b.BytesPerSecond - KeyInfoRate.BytesPerSecond));

        public RateLimitDecision TryAcquire(LogLevel level, LogSource source, int payloadBytes, bool keyInfo = false, bool reserveForIncident = false)
        {
            if (level < LogLevel.Trace || level > LogLevel.Fatal || payloadBytes < 1)
                return RateLimitDecision.InvalidInput;
            // 时钟是受信任的 SDK 依赖。在同步区外读取；过期样本会被钳制。
            long now = Math.Max(0, clock.TimestampTicks);
            if (!Monitor.TryEnter(gate))
                return RateLimitDecision.ContentionRejected;
            try
            {
                now = Math.Max(lastTicks, now);
                lastTicks = now;
                if (keyInfo && level == LogLevel.Info && source == LogSource.Application)
                {
                    keyInfoBucket.Refill(now);
                    if (keyInfoBucket.CanTake(payloadBytes))
                    {
                        keyInfoBucket.Take(payloadBytes);
                        return RateLimitDecision.Granted;
                    }
                }
                int partition = level >= LogLevel.Error ? 1 : 0;
                int sourceIndex = partition * PolicySnapshot.SourceCount + PolicySnapshot.SourceIndex(source);
                int levelIndex = (int)level;
                // 只更新本次涉及的桶，空闲桶的额度在下次访问时一次补齐。
                partitions[partition].Refill(now);
                sources[sourceIndex].Refill(now);
                levels[levelIndex].Refill(now);
                // 三种桶都能支付才一起扣款，否则一次被来源限流的尝试也会消耗整个分区的额度。
                bool keepHeadroom = protectWarnings && level < LogLevel.Warn;
                bool canPay = keepHeadroom
                    ? partitions[partition].CanTakeWithWarnHeadroom(payloadBytes) && sources[sourceIndex].CanTakeWithWarnHeadroom(payloadBytes)
                    : partitions[partition].CanTake(payloadBytes) && sources[sourceIndex].CanTake(payloadBytes);
                if (reserveForIncident && level == LogLevel.Info && !keyInfo
                    && (!partitions[partition].CanSpareForIncident(payloadBytes)
                        || !levels[levelIndex].CanSpareForIncident(payloadBytes)
                        || (source == LogSource.Unity && !sources[sourceIndex].CanSpareForIncident(payloadBytes))))
                    return RateLimitDecision.RateLimited;
                if (!canPay || !levels[levelIndex].CanTake(payloadBytes))
                    return RateLimitDecision.RateLimited;
                partitions[partition].Take(payloadBytes);
                sources[sourceIndex].Take(payloadBytes);
                levels[levelIndex].Take(payloadBytes);
                return RateLimitDecision.Granted;
            }
            finally
            {
                Monitor.Exit(gate);
            }
        }
    }
}
