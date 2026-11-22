using System;
using System.Diagnostics;
using System.Threading;
namespace Logos
{
    internal sealed class StopwatchClock : IMonotonicClock
    {
        private readonly long origin = Stopwatch.GetTimestamp();
        private long last;
        public long TimestampTicks
        {
            get
            {
                double elapsed = ((double)Stopwatch.GetTimestamp() - origin) * TimeSpan.TicksPerSecond / Stopwatch.Frequency;
                long candidate = elapsed >= long.MaxValue ? long.MaxValue : Math.Max(0, (long)elapsed);
                // 并发调用可能按不同于采样的顺序返回。只允许把共享水位向前推进，较早取得样本的线程会采用已发布的较大值。
                while (true)
                {
                    long observed = Interlocked.Read(ref last);
                    if (candidate <= observed)
                        return observed;
                    if (Interlocked.CompareExchange(ref last, candidate, observed) == observed)
                        return candidate;
                }
            }
        }
    }
}
