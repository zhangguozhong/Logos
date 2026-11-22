using System;
namespace Logos.Storage
{
    // 仅由 worker 在脱敏编码成功后观察身份；不持有 LogRecord、正文或样本。
    internal sealed class BoundedIncidentContext : IDisposable
    {
        internal const int RingLimit = 8, MemberLimit = 16, SessionLimit = 16;
        internal const long PreTicks = 30 * TimeSpan.TicksPerSecond, PostTicks = 10 * TimeSpan.TicksPerSecond;
        private readonly DiagnosticRecordReference[] ring = new DiagnosticRecordReference[RingLimit];
        private readonly long[] ringTicks = new long[RingLimit];
        private readonly DiagnosticRecordReference[] members = new DiagnosticRecordReference[MemberLimit];
        private readonly int coreLimit;
        private int cursor, count, sessions, reservedCost, reservedWire;
        private LogContext scope;
        private QuotaDayTag day;
        private DiagnosticRecordReference trigger;
        private long triggerTicks, lastTicks;
        internal long Generation
        {
            get;
            private set;
        }
        internal long Deadline
        {
            get;
            private set;
        }
        internal bool Frozen
        {
            get;
            private set;
        }
        internal bool Owned
        {
            get;
            private set;
        }
        internal bool Active => trigger != null;
        internal bool Ready => Active && Frozen && !Owned;
        internal int Count => count;
        internal int WorkspaceBytes => 49152;
        internal long Suppressed
        {
            get;
            private set;
        }
        internal BoundedIncidentContext(int coreLimit)
        {
            this.coreLimit = coreLimit;
        }
        internal static bool SameContext(LogContext a, LogContext b) => a != null && b != null
            && a.DiagnosticUserId == b.DiagnosticUserId && Equals(a.Device, b.Device)
            && a.AppSessionId == b.AppSessionId && a.UnityInstanceId == b.UnityInstanceId
            && a.UnityVisitId == b.UnityVisitId && a.IdentityContextVersion == b.IdentityContextVersion;
        internal void Observe(LogRecord record, long now, bool budgetAvailable)
        {
            if (record.DiagnosticMetadata?.IsSynthetic == true || !record.QuotaDayTag.HasValue)
                return;
            if (Owned)
                return;
            if (scope != null && (!SameContext(scope, record.Context) || day.DayId != record.QuotaDayTag.Value.DayId || now < lastTicks))
                Discard();
            lastTicks = now;
            scope = record.Context;
            day = record.QuotaDayTag.Value;
            Advance(now);
            if (record.Level < LogLevel.Error && !DiagnosticRecordMetadata.IsCriticalEvent(record.EventTypeId))
                return;
            // 子预算耗尽后，不再捕获额外身份。原输入仍沿正常链路提交。
            if (!budgetAvailable || (!Active && (sessions >= SessionLimit || Generation == long.MaxValue)))
            {
                if (record.Level >= LogLevel.Error)
                    Suppress();
                return;
            }
            var reference = BoundedRepeatAggregation.Reference(record, DiagnosticReferenceOutcome.Pending);
            if (!Active && record.Level >= LogLevel.Error)
            {
                Generation++;
                sessions++;
                trigger = reference;
                triggerTicks = now;
                Deadline = now > long.MaxValue - PostTicks ? long.MaxValue : now + PostTicks;
                // 先保留触发身份，再按环顺序加入实际可用的前窗，不能声称完整 30 秒。
                Add(reference);
                for (int i = 0; i < RingLimit; i++)
                {
                    int index = (cursor + i) % RingLimit;
                    if (ring[index] != null && ringTicks[index] <= now && now - ringTicks[index] <= PreTicks)
                        Add(ring[index]);
                }
            }
            else if (Active && !Frozen)
                Add(reference);
            ring[cursor] = reference;
            ringTicks[cursor] = now;
            cursor = (cursor + 1) % RingLimit;
        }
        private void Add(DiagnosticRecordReference value)
        {
            if (Frozen)
                return;
            for (int i = 0; i < count; i++)
                if (members[i].Sequence == value.Sequence || members[i].RecordId == value.RecordId)
                    return;
            // 为以后真实摘要目标预留最大合法 ID，避免终态增长越过 metadata/core 上限。
            int cost = 192 + 2 * (value.RecordId.Length + LogContext.MaxIdLength + value.Context.AppSessionId.Length + value.Context.UnityInstanceId.Length + value.Context.UnityVisitId.Length);
            int wire = DiagnosticRecordMetadata.ReferenceWire(value) - System.Text.Encoding.UTF8.GetByteCount(value.TargetRecordId) + 3 * LogContext.MaxIdLength;
            if (count == MemberLimit || LoggingOptions.FixedRecordBytes + 128 + reservedCost + cost > coreLimit
                || 26 + reservedWire + wire - 8 > DiagnosticRecordMetadata.MaxPayloadBytes)
            {
                Frozen = true;
                return;
            }
            members[count++] = value;
            reservedCost += cost;
            reservedWire += wire;
            if (count == MemberLimit)
                Frozen = true;
        }
        internal void Advance(long now)
        {
            if (Active && now >= Deadline)
                Frozen = true;
        }
        // 冻结时确定窗口成员的可见状态；尚无终态的成员保留为未知，迟到的结果不能改写已生成的现场。
        internal void Freeze()
        {
            if (Active)
                Frozen = true;
        }
        internal void Terminal(long sequence, LogStorageOutcomeKind kind, DiagnosticRecordReference target, long now)
        {
            Advance(now);
            for (int i = 0; i < RingLimit; i++)
                if (ring[i]?.Sequence == sequence)
                    ring[i] = Resolved(ring[i], kind, target);
            // 冻结后 pending 保持 unknown，不因迟到 ACK 改写已封存元数据。
            if (Frozen || Owned)
                return;
            for (int i = 0; i < count; i++)
                if (members[i].Sequence == sequence)
                    members[i] = Resolved(members[i], kind, target);
        }
        private static DiagnosticRecordReference Resolved(DiagnosticRecordReference input, LogStorageOutcomeKind kind, DiagnosticRecordReference target)
        {
            if (input.Outcome != DiagnosticReferenceOutcome.Pending)
                return input;
            if (kind == LogStorageOutcomeKind.RepresentedBySummary)
            {
                if (target == null || !SameContext(input.Context, target.Context) || input.Level != target.Level)
                    return input;
                return new DiagnosticRecordReference(input.RecordId, input.Sequence, input.UtcTimestamp, input.Context, input.Level,
                    DiagnosticReferenceOutcome.Represented, target.RecordId, target.Sequence, target.UtcTimestamp);
            }
            return new DiagnosticRecordReference(input.RecordId, input.Sequence, input.UtcTimestamp, input.Context, input.Level,
                kind == LogStorageOutcomeKind.Committed ? DiagnosticReferenceOutcome.Committed : DiagnosticReferenceOutcome.Dropped);
        }
        internal LogRecord CreateRecord()
        {
            if (!Ready || count == 0)
                return null;
            var copy = new DiagnosticRecordReference[count];
            Array.Copy(members, copy, count);
            var metadata = new DiagnosticRecordMetadata(DiagnosticRecordKind.IncidentReference, null, null, 0, copy, null, null, null);
            return new LogRecord(LogLevel.Error, "logos.diagnostic", "incident.context", DiagnosticRecordMetadata.NewSyntheticId(),
                trigger.Sequence, trigger.UtcTimestamp, triggerTicks, "", "", scope, false, false, day, false, metadata);
        }
        internal void MarkOwned()
        {
            if (!Ready)
                throw new InvalidOperationException();
            Owned = true;
        }
        internal bool Resolve(long generation)
        {
            if (!Owned || generation != Generation)
                return false;
            Owned = false;
            ClearActive();
            return true;
        }
        private void Suppress()
        {
            if (Suppressed < long.MaxValue)
                Suppressed++;
        }
        internal void ClearRing()
        {
            Array.Clear(ring, 0, ring.Length);
            Array.Clear(ringTicks, 0, ringTicks.Length);
            cursor = 0;
            if (!Active)
            {
                scope = null;
                day = default;
            }
        }
        internal void Discard()
        {
            ClearRing();
            if (Owned)
                return; // token 必须先经 storage Publish 结算，禁止提前复用代际。
            if (Active)
                Suppress();
            ClearActive();
            scope = null;
            day = default;
        }
        private void ClearActive()
        {
            Array.Clear(members, 0, members.Length);
            count = reservedCost = reservedWire = 0;
            trigger = null;
            Deadline = triggerTicks = 0;
            Frozen = false;
            bool hasRing = false;
            for (int i = 0; i < ring.Length; i++)
                hasRing |= ring[i] != null;
            if (!hasRing)
            {
                scope = null;
                day = default;
            }
        }
        public void Dispose()
        {
            Owned = false;
            ClearRing();
            ClearActive();
        }
    }
}
