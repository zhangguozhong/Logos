using System;
namespace Logos.Transport
{
    // 工作线程独占的可信锚点；无效样本要求显式提供新锚点。
    // 用单调时钟的经过时间推进可信 UTC，并保留已观察的时间下界以识别倒退。
    internal sealed class TransferTimeAuthority
    {
        private long anchor, last, ticks;
        private bool valid;
        internal bool IsMonotonicRollback(long now) => now < last;
        internal bool TryAnchor(DateTime utc, long now) => TryAnchor(utc, now, now);
        internal bool TryAnchor(DateTime utc, long sample, long observedNow)
        {
            // 排队样本早于工作线程观察。无论将其 UTC 重定位到
            // observedNow，还是通过 TryUtc 查询样本，都无法保留这一区别。
            if (observedNow < last)
            {
                valid = false;
                return false;
            }
            last = observedNow;
            if (utc.Kind != DateTimeKind.Utc || sample < 0 || sample > observedNow ||
               (valid && (!TryProject(sample, out var previous) || utc < previous)) ||
               observedNow - sample > DateTime.MaxValue.Ticks - utc.Ticks)
            {
                valid = false;
                return false;
            }
            anchor = sample;
            ticks = utc.Ticks;
            valid = true;
            return true;
        }
        internal bool TryUtc(long now, out DateTime utc)
        {
            utc = default;
            if (now < last)
            {
                valid = false;
                return false;
            }
            // 无效期间也保留观察，使新样本无法掩盖时钟回退。
            last = now;
            if (!valid || !TryProject(now, out utc))
            {
                valid = false;
                return false;
            }
            return true;
        }
        private bool TryProject(long now, out DateTime utc)
        {
            utc = default;
            if (now < anchor)
                return false;
            long elapsed = now - anchor;
            if (elapsed > DateTime.MaxValue.Ticks - ticks)
                return false;
            utc = new DateTime(ticks + elapsed, DateTimeKind.Utc);
            return true;
        }
    }
}
