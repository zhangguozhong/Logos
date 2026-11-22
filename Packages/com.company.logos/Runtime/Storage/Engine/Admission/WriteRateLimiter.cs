using System;

namespace Logos.Storage
{
    // 普通日志和错误日志各有独立令牌桶。每次按完整块扣款，下一次可写时间要把上次补充不足一字节的余数算进去。
    /// <summary>由工作线程独占、按实际线格式字节计费的令牌桶。时间戳是调用方起点的单调 100 ns tick。</summary>
    internal sealed class WriteRateLimiter
    {
        internal const int NormalBytesPerMinute = 1677721;
        internal const int ErrorBytesPerMinute = 419431;
        private const long MinuteTicks = 60 * TimeSpan.TicksPerSecond;
        private const long Capacity = StorageRuntimeLimits.MaxWireBlockBytes * MinuteTicks;
        private Bucket normal, error;
        internal int ControlPayloadBytes => 64; // 保守数值预算；CLR 对象头和填充另计。

        private struct Bucket
        {
            // 额度单位为字节乘 MinuteTicks，无需浮点数即可保留每一份小数补充量。
            internal long Credits, LastTicks;
        }

        internal WriteRateLimiter(long originTicks)
        {
            normal = error = new Bucket { Credits = Capacity, LastTicks = originTicks };
        }

        internal bool TryAcquire(bool isErrorClass, int wireBytes, long nowTicks, out long nextTicks)
        {
            Validate(wireBytes);
            ref Bucket bucket = ref (isErrorClass ? ref error : ref normal);
            int rate = isErrorClass ? ErrorBytesPerMinute : NormalBytesPerMinute;
            Refill(ref bucket, rate, nowTicks);
            long cost = wireBytes * MinuteTicks;
            if (bucket.Credits < cost)
            {
                nextTicks = Next(bucket, rate, cost);
                return false;
            }
            bucket.Credits -= cost;
            nextTicks = bucket.LastTicks;
            return true;
        }

        internal long NextAvailableTicks(bool isErrorClass, int wireBytes, long nowTicks)
        {
            Validate(wireBytes);
            ref Bucket bucket = ref (isErrorClass ? ref error : ref normal);
            int rate = isErrorClass ? ErrorBytesPerMinute : NormalBytesPerMinute;
            Refill(ref bucket, rate, nowTicks);
            return Next(bucket, rate, wireBytes * MinuteTicks);
        }

        private static void Validate(int wireBytes)
        {
            if (wireBytes < 1 || wireBytes > StorageRuntimeLimits.MaxWireBlockBytes)
                throw new ArgumentOutOfRangeException(nameof(wireBytes));
        }

        private static void Refill(ref Bucket bucket, int rate, long nowTicks)
        {
            if (nowTicks <= bucket.LastTicks)
                return;
            // 无符号减法覆盖整个有符号时钟范围且不溢出。
            ulong elapsed = unchecked((ulong)nowTicks - (ulong)bucket.LastTicks);
            long missing = Capacity - bucket.Credits;
            long fillTicks = (missing + rate - 1) / rate;
            bucket.Credits = elapsed >= (ulong)fillTicks
                ? Capacity
                : bucket.Credits + (long)elapsed * rate;
            bucket.LastTicks = nowTicks;
        }

        private static long Next(Bucket bucket, int rate, long cost)
        {
            if (bucket.Credits >= cost)
                return bucket.LastTicks;
            long delay = (cost - bucket.Credits + rate - 1) / rate;
            // MaxValue 也表示饱和后的“无可表示时间戳”；获取额度时仍检查余额。
            return bucket.LastTicks > long.MaxValue - delay ? long.MaxValue : bucket.LastTicks + delay;
        }
    }
}
