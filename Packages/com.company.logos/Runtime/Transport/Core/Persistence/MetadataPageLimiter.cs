namespace Logos.Transport
{
    // 仅运行时页面令牌；调用者预先扣除每次实际写入尝试，绝不退款。
    // 所有参数和延迟都是 100 ns 时长，不是原始 Stopwatch 值。
    internal sealed class MetadataPageLimiter
    {
        private const long OrdinaryInterval = 18750000, ControlInterval = 300000000;
        private int ordinary = 4, reserve = 2;
        private long ordinaryAnchor, reserveAnchor;
        internal MetadataPageLimiter(long nowTicks100ns)
        {
            ordinaryAnchor = reserveAnchor = nowTicks100ns;
        }
        private static void Refill(long now, long interval, int capacity, ref long anchor, ref int tokens)
        {
            if (now <= anchor)
                return;
            ulong elapsed = unchecked((ulong)now - (ulong)anchor), count = elapsed / (ulong)interval;
            if (count == 0)
                return;
            tokens += (int)System.Math.Min((ulong)(capacity - tokens), count);
            // 锚点只前进完整的补充周期，余下的时间计入下一次补充。
            anchor = now - (long)(elapsed % (ulong)interval);
        }
        private void Advance(long now)
        {
            Refill(now, OrdinaryInterval, 4, ref ordinaryAnchor, ref ordinary);
            Refill(now, ControlInterval, 2, ref reserveAnchor, ref reserve);
        }
        internal bool TryTake(long nowTicks100ns, bool control = false, bool changed = true)
        {
            Advance(nowTicks100ns);
            if (!changed)
                return false;
            if (ordinary > 0)
            {
                ordinary--;
                return true;
            }
            if (control && reserve > 0)
            {
                reserve--;
                return true;
            }
            return false;
        }
        private static long Delay(long now, long anchor, long interval) => now <= anchor ? interval : interval - (long)(unchecked((ulong)now - (ulong)anchor) % (ulong)interval);
        internal long NextDelayTicks100ns(long nowTicks100ns, bool control = false)
        {
            Advance(nowTicks100ns);
            if (ordinary > 0 || (control && reserve > 0))
                return 0;
            long delay = Delay(nowTicks100ns, ordinaryAnchor, OrdinaryInterval);
            return control ? System.Math.Min(delay, Delay(nowTicks100ns, reserveAnchor, ControlInterval)) : delay;
        }
    }
}
