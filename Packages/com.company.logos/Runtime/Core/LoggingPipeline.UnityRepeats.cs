using System;
using System.Globalization;
using System.Threading;

namespace Logos
{
    internal sealed partial class LoggingPipeline
    {
        // 每组容量必须是 2 的幂。生产者最多探测一组，worker 的访问位图覆盖两组。
        private const int RepeatSlotCount = 8;
        private const int LegacyRepeatText = 72;
        // GUID 前缀(33)、四个非负 Int64(各 19) 和三个空格。
        private const int MaxRepeatText = 33 + 4 * 19 + 3;
        // s + 首条序号 + 空格 + 增量 + 空格 + 带符号的 UTC tick 差值。
        internal const int MaxRunText = 1 + 19 + 1 + 19 + 1 + 20;
        private readonly int repeatSummaryChars;
        private readonly RepeatSlot[] repeatSlots;
        private DiagnosticControlState terminalRepeatControl;
        private long coalescedUnityInfo, recoveredUnityInfoObservations, unityRepeatSummaryAttempts;
#if LOGOS_WORKER_BENCHMARK
        private long liveSummaryAttempts, stopSummaryAttempts;
#endif

        private class RepeatSlot
        {
            // 槽位只弱引用首条正文和堆栈，避免为了识别重复而延长原文本的寿命。任一文本已被回收时，匹配会失败。
            private readonly WeakReference<string> message = new WeakReference<string>(null);
            private readonly WeakReference<string> stack = new WeakReference<string>(null);
            private int owner;
            internal DiagnosticControlState Origin;
            internal long FirstSequence, Delta, LastAttempt, LastUtcTicks;
            internal int FirstQueueSlot;
            internal long FailedEvictionAt = -TimeSpan.TicksPerSecond;
            internal QuotaDayTag? Tag;

            internal DateTime FirstUtc;
            internal long PendingTicks, PendingCount;
            internal bool HasPendingFirst => PendingCount != 0;

            // 仅暂存争用拒绝的原始观测；下一次匹配仍须完整经过速率和队列准入。
            internal void ObserveContention(string text, string trace, DiagnosticControlState control, QuotaDayTag? tag, long ticks)
            {
                if (HasPendingFirst && SameScope(control, tag) && Matches(text, trace ?? string.Empty))
                {
                    if (PendingCount < long.MaxValue) PendingCount++;
                    return;
                }
                FirstSequence = Delta = 0;
                PendingCount = 1;
                FirstUtc = DateTime.UtcNow;
                PendingTicks = ticks;
                Tag = tag;
                Origin = control;
                message.SetTarget(text);
                stack.SetTarget(trace ?? string.Empty);
            }

            internal bool TryReadOriginal(out string text, out string trace)
            {
                trace = null;
                return message.TryGetTarget(out text) && stack.TryGetTarget(out trace);
            }

            internal bool TryEnter() => Interlocked.CompareExchange(ref owner, 1, 0) == 0;
            internal void Exit() => Volatile.Write(ref owner, 0);

            internal bool SameScope(DiagnosticControlState control, QuotaDayTag? tag) =>
                (FirstSequence != 0 || HasPendingFirst) && ReferenceEquals(Origin, control) && Nullable.Equals(Tag, tag);

            internal bool Matches(string text, string trace) =>
                message.TryGetTarget(out var previous) && string.Equals(text, previous, StringComparison.Ordinal)
                && stack.TryGetTarget(out var previousStack) && string.Equals(trace, previousStack, StringComparison.Ordinal);

            internal void ObserveRepeat(bool timed)
            {
                if (timed) LastUtcTicks = DateTime.UtcNow.Ticks;
                if (Delta < long.MaxValue) Delta++;
            }

            internal void Remember(LogRecord record, DiagnosticControlState control, long repeats, long now, int queueSlot)
            {
                FirstSequence = record.Sequence;
                FirstUtc = record.UtcTimestamp;
                FirstQueueSlot = queueSlot;
                Delta = repeats;
                PendingCount = 0;
                FailedEvictionAt = -TimeSpan.TicksPerSecond;
                Origin = control;
                Tag = record.QuotaDayTag;
                message.SetTarget(record.Message);
                stack.SetTarget(record.Stack);
                LastAttempt = now;
            }
        }

        private long runObservation;
        private RunSlot activeRun;

        private sealed class RunSlot : RepeatSlot
        {
            internal long Observation;
        }

        private RunSlot EnterRunSlot(string message, string stack, DiagnosticControlState control, QuotaDayTag? tag,
            long observation, out bool matched)
        {
            matched = false;
            if (repeatSlots == null || message == null || message.Length > options.MaxMessageChars
                || (stack != null && stack.Length > options.MaxStackChars)) return null;
            var previous = Volatile.Read(ref activeRun);
            if (previous != null && previous.TryEnter())
            {
                if (previous.Observation == unchecked(observation - 1) && previous.SameScope(control, tag)
                    && previous.Matches(message, stack ?? string.Empty))
                {
                    matched = true;
                    return previous;
                }
                if (previous.Delta == 0) return previous;
                previous.Exit();
            }
            // 未发布的尾数保留在固定槽中。没有可用槽时照常采集新首条。
            foreach (RunSlot slot in repeatSlots)
            {
                if (ReferenceEquals(slot, previous) || !slot.TryEnter()) continue;
                if (slot.Delta == 0) return slot;
                slot.Exit();
            }
            return null;
        }

        private void CountRun(RunSlot run, long observation)
        {
            run.Observation = observation;
            run.ObserveRepeat(true);
            if (run.Delta == 1) worker.ScheduleRunTail(clock.TimestampTicks);
        }

        private RepeatSlot EnterRepeatSlot(string message, string stack, DiagnosticControlState control, QuotaDayTag? tag, out bool matched, bool allowEviction = true)
        {
            matched = false;
            if (repeatSlots == null || message == null || message.Length > 256 || message.Length > options.MaxMessageChars
                || (stack != null && (stack.Length > 512 || stack.Length > options.MaxStackChars)))
                return null;

            // 长度和首尾字符只用于选择起点；同形文本仍需逐字符核对。
            stack = stack ?? string.Empty;
            int start = message.Length + stack.Length;
            if (message.Length > 0)
                start += message[0] * 31 + message[message.Length - 1];
            // 相邻登录版本使用不同的八槽区。上一版本尾数仍留在原槽等待 worker；
            // 连续快速切号复用同一区时，仍须遵守旧尾数保护和普通准入。
            int bankStart = (control.Context.IdentityContextVersion & 1) == 0 ? RepeatSlotCount : 0;
            bool missedBusySlot = false;
            RepeatSlot reusable = null, oldest = null;
            long oldestSequence = long.MaxValue;
            for (int i = 0; i < RepeatSlotCount; i++)
            {
                var slot = repeatSlots[bankStart + ((start + i) & (RepeatSlotCount - 1))];
                if (!slot.TryEnter())
                {
                    missedBusySlot = true;
                    continue;
                }
                if (!slot.SameScope(control, tag))
                {
                    // 已换号的尾数由工作线程接管。迟到的旧回调也不能覆盖新槽。
                    if (slot.Origin != null && (slot.Origin.ContextRevision > control.ContextRevision
                        || IsIdentityTail(slot, control)))
                    {
                        slot.Exit();
                        continue;
                    }
                    // 已排空的旧槽可能排在现有匹配项之前；先查完一圈，避免重复建首条。
                    if (reusable == null) reusable = slot;
                    slot.Exit();
                    continue;
                }
                if (slot.Matches(message, stack))
                {
                    matched = true;
                    return slot;
                }
                if (slot.FirstSequence < oldestSequence)
                {
                    oldestSequence = slot.FirstSequence;
                    oldest = slot;
                }
                slot.Exit();
            }
            if (reusable != null && reusable.TryEnter())
            {
                if (!reusable.SameScope(control, tag))
                {
                    if (reusable.Origin == null || (reusable.Origin.ContextRevision <= control.ContextRevision
                        && !IsIdentityTail(reusable, control))) return reusable;
                }
                else if (reusable.Matches(message, stack))
                {
                    matched = true;
                    return reusable;
                }
                reusable.Exit();
            }
            // 忙槽内可能已有匹配项，不能根据一次不完整扫描驱逐样本。
            if (!allowEviction || missedBusySlot || oldest == null || !oldest.TryEnter())
                return null;
            // 再次取得槽后确认仍是刚才的样本；不驱逐其他生产者新提交的首条。
            if (oldest.FirstSequence != oldestSequence)
            {
                oldest.Exit();
                return null;
            }
            if (oldest.Delta != 0)
            {
                long now = clock.TimestampTicks;
                if ((oldest.FailedEvictionAt >= 0 && now - oldest.FailedEvictionAt < TimeSpan.TicksPerSecond) || !EmitRepeatSummary(oldest))
                {
                    // 摘要仍未提交，保留差量。新日志可以通过原有普通入口采集。
                    if (oldest.FailedEvictionAt < 0 || now - oldest.FailedEvictionAt >= TimeSpan.TicksPerSecond)
                        oldest.FailedEvictionAt = now;
                    oldest.Exit();
                    return null;
                }
            }
            return oldest;
        }

        private void CountRepeat(RepeatSlot slot)
        {
            slot.ObserveRepeat(!options.LegacyRepeatSummaryForBenchmark);
            long now = clock.TimestampTicks;
            if (now - slot.LastAttempt >= TimeSpan.TicksPerSecond)
            {
                slot.LastAttempt = now;
                EmitRepeatSummary(slot);
            }
        }

        private static bool IdentityChanged(LogContext origin, LogContext current) =>
            origin.IdentityContextVersion != current.IdentityContextVersion ||
            !string.Equals(origin.DiagnosticUserId, current.DiagnosticUserId, StringComparison.Ordinal);

        private static bool IsIdentityTail(RepeatSlot slot, DiagnosticControlState current) =>
            slot.Delta > 0 && slot.Origin.ContextRevision < current.ContextRevision &&
            IdentityChanged(slot.Origin.Context, current.Context);

        // 扫描两组固定槽。短暂争用或速率不足时保留计数，让工作线程稍后再试。
        private bool DrainIdentityTails()
        {
            bool retry = false;
            int visited = 0;
            // 同一轮只发布同一上下文的摘要。各槽独立释放，生产者无需等待整组。
            for (int pass = 0; pass < repeatSlots.Length; pass++)
            {
                DiagnosticControlState group = null;
                bool anotherGroup = false;
                for (int i = 0; i < repeatSlots.Length; i++)
                {
                    int bit = 1 << i;
                    if ((visited & bit) != 0) continue;
                    visited |= bit;
                    var slot = repeatSlots[i];
                    // 当前版本或尚未建立首条的槽无需抢占。迟到的生产者在退出时补发唤醒。
                    var origin = Volatile.Read(ref slot.Origin);
                    var current = Volatile.Read(ref diagnosticControl);
                    if (origin == null || origin.ContextRevision >= current.ContextRevision) continue;
                    if (!slot.TryEnter())
                    {
                        retry = true;
                        continue;
                    }
                    try
                    {
                        if (!IsIdentityTail(slot, Volatile.Read(ref diagnosticControl))) continue;
                        if (group == null) group = slot.Origin;
                        if (!ReferenceEquals(group, slot.Origin))
                        {
                            visited &= ~bit;
                            anotherGroup = true;
                            continue;
                        }
                        AdmissionReason reason = SubmitRepeatSummary(slot, false, true);
                        if (RetryableTail(reason) || reason == AdmissionReason.Stopping)
                            retry = true;
                        else
                            slot.Delta = 0;
                    }
                    finally { slot.Exit(); }
                }
                if (!anotherGroup) break;
            }
            return retry;
        }

        private bool EmitRepeatSummary(RepeatSlot slot) =>
            SubmitRepeatSummary(slot, false, false) == AdmissionReason.Accepted;

        // 摘要用首条记录的序号指向原文，正文携带自上次成功发布以来的次数。回到准入流程后仍需通过策略、限流和队列检查。
        private AdmissionReason SubmitRepeatSummary(RepeatSlot slot, bool terminal, bool originalUser)
        {
            if (slot.Delta == 0)
                return AdmissionReason.Accepted;
            Interlocked.Increment(ref unityRepeatSummaryAttempts);
#if LOGOS_WORKER_BENCHMARK
            if (terminal) Interlocked.Increment(ref stopSummaryAttempts);
            else Interlocked.Increment(ref liveSummaryAttempts);
#endif
            var result = TryAcceptCore(LogLevel.Info, LogSource.Unity, null, null, slot is RunSlot ? "logos.unity.run" : "logos.unity.repeat", null, null, false, false, false, slot, terminal,
                originalUser ? slot.Origin.Context : null);
            if (result.IsAccepted) slot.Delta = 0;
            return result.Reason;
        }

        internal static string FormatRunSummary(long sequence, long delta, long utcOffset)
        {
            Span<char> text = stackalloc char[MaxRunText];
            text[0] = 's';
            int offset = 1;
            sequence.TryFormat(text.Slice(offset), out int written, provider: CultureInfo.InvariantCulture);
            offset += written;
            text[offset++] = ' ';
            delta.TryFormat(text.Slice(offset), out written, provider: CultureInfo.InvariantCulture);
            offset += written;
            text[offset++] = ' ';
            utcOffset.TryFormat(text.Slice(offset), out written, provider: CultureInfo.InvariantCulture);
            return text.Slice(0, offset + written).ToString();
        }

        private string FormatRepeatSummary(RepeatSlot slot)
        {
            if (slot is RunSlot)
                return FormatRunSummary(slot.FirstSequence, slot.Delta, slot.LastUtcTicks - slot.FirstUtc.Ticks);
            return recordPrefix + slot.FirstSequence.ToString(CultureInfo.InvariantCulture)
                + " " + slot.Delta.ToString(CultureInfo.InvariantCulture)
                + (options.LegacyRepeatSummaryForBenchmark ? string.Empty : " " + slot.FirstUtc.Ticks.ToString(CultureInfo.InvariantCulture)
                    + " " + slot.LastUtcTicks.ToString(CultureInfo.InvariantCulture));
        }

        internal void FlushUnityRepeats() => FlushUnityRepeats(false);

        private static bool RetryableTail(AdmissionReason reason) =>
            reason == AdmissionReason.RateLimited || reason == AdmissionReason.QueueFull || reason == AdmissionReason.ContentionRejected;

        private bool FlushUnityRepeats(bool terminal)
        {
            if (repeatSlots == null) return false;
            bool retry = false;
            DiagnosticControlState limitedOrigin = null;
            QuotaDayTag? limitedTag = null;
            foreach (var slot in repeatSlots)
            {
                if (!slot.TryEnter())
                {
                    retry = true;
                    continue;
                }
                try
                {
                    // 同一轮同域连续段摘要支付相同的 Unity Info 额度。保留尾数到原定的下一轮。
                    if (limitedOrigin != null && ReferenceEquals(limitedOrigin, slot.Origin)
                        && Nullable.Equals(limitedTag, slot.Tag)) continue;
                    bool retired = (terminal || slot is RunSlot) && IsIdentityTail(slot, terminal ? terminalRepeatControl : Volatile.Read(ref diagnosticControl));
                    var reason = SubmitRepeatSummary(slot, terminal, retired);
                    if (RetryableTail(reason))
                    {
                        retry = true;
                        if (reason == AdmissionReason.RateLimited && slot is RunSlot
                            && !options.LegacyRunWorkerForBenchmark && worker.IsWorkerThread)
                        {
                            limitedOrigin = slot.Origin;
                            limitedTag = slot.Tag;
                        }
                    }
                    else if (slot is RunSlot && reason != AdmissionReason.Stopping) slot.Delta = 0;
                }
                finally { slot.Exit(); }
            }
            return retry || (terminal && producerRegistry.Count != 0);
        }
    }
}
