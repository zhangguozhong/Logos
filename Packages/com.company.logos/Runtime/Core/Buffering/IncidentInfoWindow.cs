using System;
using System.Threading;

namespace Logos
{
    // 独立从普通队列预算中扣除。生产者只尝试取得一次门锁，复制固定前缀后即返回。
    // 原始字符串、堆栈及已经成功入队的记录都不由此对象持有。
    internal sealed class IncidentInfoWindow
    {
        internal const int ReservedCount = LoggingOptions.IncidentReservedSlots;
        private const int Side = ReservedCount / 2;
        internal const int MaxPrefix = 128;
        private const long Horizon = TimeSpan.TicksPerSecond;
        private const long MaxPending = 5 * Horizon;
        private const long Cooldown = 10 * Horizon;
        private const long RetryDelay = 10 * TimeSpan.TicksPerMillisecond;

        internal readonly struct Entry
        {
            internal readonly DiagnosticControlState Control;
            internal readonly QuotaDayTag? Tag;
            internal readonly long Observation, TriggerObservation, TriggerSequence, Ticks;
            internal readonly DateTime Utc;
            private readonly IncidentInfoWindow owner;
            private readonly int slotIndex;
            internal readonly int Length;
            // 只有实际通过发布额度才读取正文；限流重试不创建不可擦除的托管字符串。
            internal string Message => owner.CopyBorrowedMessage(slotIndex, Observation);
            internal readonly bool Truncated;

            internal Entry(DiagnosticControlState control, QuotaDayTag? tag, long observation, long triggerObservation,
                long triggerSequence, long ticks, DateTime utc, IncidentInfoWindow owner, int slotIndex, int length, bool truncated)
            {
                Control = control;
                Tag = tag;
                Observation = observation;
                TriggerObservation = triggerObservation;
                TriggerSequence = triggerSequence;
                Ticks = ticks;
                Utc = utc;
                this.owner = owner;
                this.slotIndex = slotIndex;
                Length = length;
                Truncated = truncated;
            }
        }

        private struct Slot
        {
            internal long Observation, Ticks;
            internal DateTime Utc;
            internal int Length;
            internal bool Truncated;
        }

        private readonly object gate = new object();
        private readonly Slot[] slots = new Slot[ReservedCount];
        private readonly char[] prefixes = new char[ReservedCount * MaxPrefix];
        // 错误记录可能在生产者退出 TryCommit 前即被 worker 确认。只保存序号，绝不保存正文。
        private readonly long[] earlyOutcomes = new long[8];
        private int outcomeCursor, borrowedSlot = -1;
        private DiagnosticControlState control;
        private bool hasDay, quotaClosed, confirmed;
        private QuotaDayTag? day;
        private long triggerSequence, triggerObservation, triggerTicks, cooldownUntil;
        private long due = long.MaxValue, retryAt;
        private long captured, published, missed;
        internal bool HasConfirmedWindow => Volatile.Read(ref confirmed);
        internal long NextDueTicks => Interlocked.Read(ref due);
        internal long Captured => Interlocked.Read(ref captured);
        internal long Published => Interlocked.Read(ref published);
        internal long Missed => Interlocked.Read(ref missed);

        internal IncidentInfoWindow(DiagnosticControlState initial) => control = initial;

        private static long After(long start, long delay) =>
            start > long.MaxValue - delay ? long.MaxValue : start + delay;

        private bool ScheduleEarlier(long time)
        {
            if (time >= due)
                return false;
            Volatile.Write(ref due, time);
            return true;
        }

        // 日标签的切换由 worker 在读取实际队列标签后执行，迟到的旧日回调不能覆盖新日窗口。
        private bool DayMatches(QuotaDayTag? tag)
        {
            if (!hasDay)
            {
                hasDay = true;
                day = tag;
            }
            return !quotaClosed && Nullable.Equals(day, tag);
        }

        internal bool TryCapture(DiagnosticControlState scope, QuotaDayTag? tag, long observation, long ticks,
            DateTime utc, string message)
        {
            if (scope == null || !tag.HasValue || observation <= 0 || ticks < 0 || message == null)
                return false;
            if (!Monitor.TryEnter(gate))
            {
                Interlocked.Increment(ref missed);
                return false;
            }
            try
            {
                if (!ReferenceEquals(scope, control))
                    return false;
                if (!DayMatches(tag))
                    return ScheduleEarlier(0);
                bool after = triggerSequence != 0;
                if (after && (observation <= triggerObservation || observation - triggerObservation > Side
                    || ticks < triggerTicks || ticks >= After(triggerTicks, Horizon)))
                    return false;
                if (!after && ticks < cooldownUntil - Horizon)
                    return false;
                int index = (after ? Side : 0) + (int)(observation % Side);
                ref Slot slot = ref slots[index];
                if (slot.Observation >= observation)
                    return false;
                int length = Math.Min(message.Length, MaxPrefix);
                if (length < message.Length && length > 0 && char.IsHighSurrogate(message[length - 1])
                    && char.IsLowSurrogate(message[length]))
                    length--;
                int start = index * MaxPrefix;
                // 同一个槽里替换较短文字时，擦除尾部，避免前一条原文残留。
                if (slot.Length > length)
                    Array.Clear(prefixes, start + length, slot.Length - length);
                message.CopyTo(0, prefixes, start, length);
                slot.Observation = observation;
                slot.Ticks = ticks;
                slot.Utc = utc;
                slot.Length = length;
                slot.Truncated = length < message.Length;
                Interlocked.Increment(ref captured);
                if (!after)
                    return ScheduleEarlier(After(ticks, Horizon));
                return confirmed && observation - triggerObservation == Side && ScheduleEarlier(0);
            }
            finally { Monitor.Exit(gate); }
        }

        internal bool TryTrigger(DiagnosticControlState scope, QuotaDayTag? tag, long observation, long ticks,
            long sequence)
        {
            if (scope == null || !tag.HasValue || observation <= 0 || sequence <= 0 || ticks < 0 || !Monitor.TryEnter(gate))
                return false;
            try
            {
                if (!ReferenceEquals(scope, control))
                    return false;
                if (!DayMatches(tag))
                    return ScheduleEarlier(0);
                if (triggerSequence != 0 || ticks < cooldownUntil)
                    return false;
                for (int i = 0; i < earlyOutcomes.Length; i++)
                {
                    long outcome = earlyOutcomes[i];
                    if (outcome == -sequence)
                        return false;
                    if (outcome == sequence)
                        Volatile.Write(ref confirmed, true);
                }
                triggerSequence = sequence;
                triggerObservation = observation;
                triggerTicks = ticks;
                cooldownUntil = After(ticks, Cooldown);
                return ScheduleEarlier(After(ticks, Horizon));
            }
            finally { Monitor.Exit(gate); }
        }

        // worker 的持久化结果可以先于生产者冻结窗口；序号按流水线唯一，覆盖旧回执只会使窗口保守过期。
        internal void ResolveTrigger(long sequence, bool persisted)
        {
            if (sequence <= 0)
                return;
            lock (gate)
            {
                if (sequence != triggerSequence)
                {
                    earlyOutcomes[outcomeCursor] = persisted ? sequence : -sequence;
                    outcomeCursor = (outcomeCursor + 1) % earlyOutcomes.Length;
                    return;
                }
                if (persisted)
                {
                    Volatile.Write(ref confirmed, true);
                    ScheduleEarlier(0);
                }
                else ClearWindow();
            }
        }

        internal void Reset(DiagnosticControlState next)
        {
            lock (gate)
            {
                // 控制面在生命周期锁外到达此处，旧的延迟清理不得回滚到较早授权或重新开启已停止的窗口。
                if (next != null && (control == null || next.RequestRevision <= control.RequestRevision))
                    return;
                control = next;
                hasDay = false;
                quotaClosed = false;
                day = null;
                // 冷却属于整个 SDK 实例，切号不能获得额外故障预算。新用户在冷却结束前也可能缺少一个窗口。
                Array.Clear(earlyOutcomes, 0, earlyOutcomes.Length);
                ClearWindow();
            }
        }

        // 清理屏障先发布关闭的配额，再进入本方法。只允许 worker 用新的有效标签重新开启。
        internal void InvalidateQuota(QuotaDayTag? closedTag)
        {
            lock (gate)
            {
                hasDay = quotaClosed = true;
                day = closedTag;
                Array.Clear(earlyOutcomes, 0, earlyOutcomes.Length);
                ClearWindow();
            }
        }

        private void ClearSlot(int i)
        {
            if (slots[i].Length != 0)
                Array.Clear(prefixes, i * MaxPrefix, slots[i].Length);
            slots[i] = default;
        }

        private void ClearWindow()
        {
            for (int i = 0; i < slots.Length; i++)
                ClearSlot(i);
            triggerSequence = triggerObservation = triggerTicks = retryAt = 0;
            Volatile.Write(ref confirmed, false);
            Volatile.Write(ref due, long.MaxValue);
        }

        private bool InWindow(Slot slot) =>
            slot.Observation < triggerObservation
                ? triggerObservation - slot.Observation <= Side && slot.Ticks <= triggerTicks && triggerTicks - slot.Ticks < Horizon
                : slot.Observation > triggerObservation && slot.Observation - triggerObservation <= Side
                    && slot.Ticks >= triggerTicks && slot.Ticks < After(triggerTicks, Horizon);

        // 逐条排出时按原始观测序号排序；晚到生产者和时间戳并列都不会改变顺序。
        private int Oldest()
        {
            int best = -1;
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i].Observation == 0)
                    continue;
                if (!InWindow(slots[i]))
                    ClearSlot(i);
                else if (best < 0 || slots[i].Observation < slots[best].Observation)
                    best = i;
            }
            return best;
        }

        // 获取独立托管字符串只发生在 worker 真正需要发布时。回调返回后，保存的 Entry 无法再次读取原文。
        // 成功移交使用现有 LogRecord 字符串协议；这些已物化的不可变字符串由 GC 管理，不能承诺覆写。
        private string CopyBorrowedMessage(int index, long observation)
        {
            if (!Monitor.IsEntered(gate) || borrowedSlot != index || slots[index].Observation != observation)
                throw new InvalidOperationException("Incident message lease has ended.");
            return new string(prefixes, index * MaxPrefix, slots[index].Length);
        }

        // true 已移交，false 永久拒绝，null 表示临时背压。每轮最多四次，单次回调结束即归还门锁，
        // 让控制面可在下一次移交前完成作废；生产者从不等待此锁。
        internal int Drain(long now, long latestObservation, DiagnosticControlState current, QuotaDayTag? currentTag,
            Func<Entry, bool?> publish, int maxAttempts = 4)
        {
            int attempts = 0;
            while (attempts < maxAttempts)
            {
                int count = DrainOne(now, latestObservation, current, currentTag, publish);
                attempts += count;
                if (count == 0 || NextDueTicks != 0)
                    break;
            }
            return attempts;
        }

        private int DrainOne(long now, long latestObservation, DiagnosticControlState current, QuotaDayTag? currentTag,
            Func<Entry, bool?> publish)
        {
            lock (gate)
            {
                if (!ReferenceEquals(control, current))
                    return 0;
                if (hasDay && !Nullable.Equals(day, currentTag))
                {
                    ClearWindow();
                    day = currentTag;
                    quotaClosed = !currentTag.HasValue;
                    return 0;
                }
                if (triggerSequence == 0)
                {
                    long earliest = long.MaxValue;
                    for (int i = 0; i < Side; i++)
                    {
                        if (slots[i].Observation == 0)
                            continue;
                        long expires = After(slots[i].Ticks, Horizon);
                        if (expires <= now)
                            ClearSlot(i);
                        else earliest = Math.Min(earliest, expires);
                    }
                    Volatile.Write(ref due, earliest);
                    return 0;
                }
                long expiry = After(triggerTicks, MaxPending);
                if (now >= expiry)
                {
                    ClearWindow();
                    return 0;
                }
                long end = After(triggerTicks, Horizon);
                if (!confirmed || (now < end && latestObservation - triggerObservation < Side) || now < retryAt)
                {
                    long next = now < end ? end : expiry;
                    if (now < retryAt) next = Math.Min(next, retryAt);
                    Volatile.Write(ref due, next);
                    return 0;
                }
                int slotIndex = Oldest();
                if (slotIndex < 0)
                {
                    ClearWindow();
                    return 0;
                }
                Slot slot = slots[slotIndex];
                var entry = new Entry(control, day, slot.Observation, triggerObservation, triggerSequence,
                    slot.Ticks, slot.Utc, this, slotIndex, slot.Length, slot.Truncated);
                bool? result;
                borrowedSlot = slotIndex;
                try { result = publish(entry); }
                catch
                {
                    ClearWindow();
                    throw;
                }
                finally { borrowedSlot = -1; }
                if (!result.HasValue)
                {
                    retryAt = After(now, RetryDelay);
                    Volatile.Write(ref due, Math.Min(retryAt, expiry));
                }
                else
                {
                    if (result.Value) Interlocked.Increment(ref published);
                    else Interlocked.Increment(ref missed);
                    ClearSlot(slotIndex);
                    Volatile.Write(ref due, 0);
                }
                return 1;
            }
        }
    }
}
