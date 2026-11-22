using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
namespace Logos
{
    public enum DiagnosticRecordKind : ushort
    {
        Event = 1, RepeatSummary = 2, IncidentReference = 3
    }
    public enum DiagnosticReferenceOutcome : ushort
    {
        Pending = 0, Committed = 1, Represented = 2, Dropped = 3
    }
    public sealed class DiagnosticRecordReference
    {
        public string RecordId
        {
            get;
        }
        public long Sequence
        {
            get;
        }
        public DateTime UtcTimestamp
        {
            get;
        }
        public LogContext Context
        {
            get;
        }
        public LogLevel Level
        {
            get;
        }
        public DiagnosticReferenceOutcome Outcome
        {
            get;
        }
        public string TargetRecordId
        {
            get;
        }
        public long TargetSequence
        {
            get;
        }
        public DateTime TargetUtcTimestamp
        {
            get;
        }
        public bool IsResolved => Outcome == DiagnosticReferenceOutcome.Committed || Outcome == DiagnosticReferenceOutcome.Represented;
        internal DiagnosticRecordReference(string recordId, long sequence, DateTime utcTimestamp, LogContext context, LogLevel level, DiagnosticReferenceOutcome outcome, string targetRecordId = null, long? targetSequence = null, DateTime? targetUtcTimestamp = null)
        {
            RecordId = LogContext.RequireId(recordId, nameof(recordId));
            if (sequence < 0 || utcTimestamp.Kind != DateTimeKind.Utc || level < LogLevel.Trace || level > LogLevel.Fatal || outcome < DiagnosticReferenceOutcome.Pending || outcome > DiagnosticReferenceOutcome.Dropped)
                throw new ArgumentException("Invalid diagnostic reference.");
            // 原样持久化时目标就是输入记录；由摘要代表时，必须给出实际摘要的标识、序号和 UTC 时间，读者据此定位证据。
            if (outcome == DiagnosticReferenceOutcome.Committed)
            {
                if (targetRecordId != null && targetRecordId != recordId || targetSequence.HasValue && targetSequence.Value != sequence || targetUtcTimestamp.HasValue && targetUtcTimestamp.Value != utcTimestamp)
                    throw new ArgumentException("Committed target differs from input.");
                TargetRecordId = recordId;
                TargetSequence = sequence;
                TargetUtcTimestamp = utcTimestamp;
            }
            else if (outcome == DiagnosticReferenceOutcome.Represented)
            {
                TargetRecordId = LogContext.RequireId(targetRecordId, nameof(targetRecordId));
                if (!TargetRecordId.StartsWith("diag-", StringComparison.Ordinal) || !targetSequence.HasValue || targetSequence.Value < 0 || !targetUtcTimestamp.HasValue || targetUtcTimestamp.Value.Kind != DateTimeKind.Utc)
                    throw new ArgumentException("Represented reference requires exact summary target.");
                TargetSequence = targetSequence.Value;
                TargetUtcTimestamp = targetUtcTimestamp.Value;
            }
            else
            {
                if (!string.IsNullOrEmpty(targetRecordId) || targetSequence.HasValue || targetUtcTimestamp.HasValue)
                    throw new ArgumentException("Unresolved reference cannot claim a target.");
                TargetRecordId = string.Empty;
            }
            Sequence = sequence;
            UtcTimestamp = utcTimestamp;
            Context = context ?? throw new ArgumentNullException(nameof(context));
            Level = level;
            Outcome = outcome;
        }
    }
    // 合成类型只允许存储工作线程或已认证解码器构造；公开入口只能创建事件。
    public sealed class DiagnosticRecordMetadata
    {
        public const int MaxTokenChars = 128, MaxMembers = 16, MaxSampleChars = 256, MaxPayloadBytes = 12288;
        public DiagnosticRecordKind Kind
        {
            get;
        }
        public string OperationId
        {
            get;
        }
        public string ResultCode
        {
            get;
        }
        public long DurationTicks
        {
            get;
        }
        public IReadOnlyList<DiagnosticRecordReference> Members
        {
            get;
        }
        public DiagnosticRecordReference FirstCommitted
        {
            get;
        }
        public DiagnosticRecordReference LatestReference
        {
            get;
        }
        public string LatestSanitizedSample
        {
            get;
        }
        public int UnresolvedCount
        {
            get;
        }
        // 内存预算和编码预算分别计算。同一份元数据挂在不同记录上时，每次准入仍须计入该记录的成本。
        public int MemoryBytes
        {
            get;
        }
        public int WireBytes
        {
            get;
        }
        public bool IsSynthetic => Kind != DiagnosticRecordKind.Event;
        public DiagnosticRecordMetadata(string operationId = null, string resultCode = null, long durationTicks = 0)
            : this(DiagnosticRecordKind.Event, operationId, resultCode, durationTicks, null, null, null, null)
        {
        }
        internal DiagnosticRecordMetadata(DiagnosticRecordKind kind, string operationId, string resultCode, long durationTicks, IReadOnlyList<DiagnosticRecordReference> members, DiagnosticRecordReference first, DiagnosticRecordReference latest, string sample)
        {
            if (kind < DiagnosticRecordKind.Event || kind > DiagnosticRecordKind.IncidentReference || durationTicks < 0)
                throw new ArgumentException("Invalid diagnostic metadata.");
            Kind = kind;
            OperationId = Token(operationId, nameof(operationId), true);
            ResultCode = Token(resultCode, nameof(resultCode), true);
            DurationTicks = durationTicks;
            int count = members?.Count ?? 0;
            if (count > MaxMembers || (kind == DiagnosticRecordKind.Event ? count != 0 || first != null || latest != null || !string.IsNullOrEmpty(sample) : count < 1))
                throw new ArgumentException("Invalid diagnostic members.");
            if (kind == DiagnosticRecordKind.RepeatSummary && (first == null || latest == null || first.Outcome != DiagnosticReferenceOutcome.Committed))
                throw new ArgumentException("Summary requires committed first reference.");
            if (kind != DiagnosticRecordKind.RepeatSummary && (first != null || latest != null || !string.IsNullOrEmpty(sample)))
                throw new ArgumentException("Unexpected sample.");
            LatestSanitizedSample = sample ?? string.Empty;
            if (LatestSanitizedSample.Length > MaxSampleChars)
                throw new ArgumentException("Sample exceeds limit.");
            var copy = new DiagnosticRecordReference[count];
            int unresolved = 0;
            int cost = checked(128 + 2 * (OperationId.Length + ResultCode.Length + LatestSanitizedSample.Length));
            for (int i = 0; i < count; i++)
            {
                copy[i] = members[i] ?? throw new ArgumentException("Null reference.");
                cost = checked(cost + ReferenceCost(copy[i]));
                if (copy[i].Outcome == DiagnosticReferenceOutcome.Pending || copy[i].Outcome == DiagnosticReferenceOutcome.Dropped)
                    unresolved++;
            }
            for (int i = 0; i < count; i++)
                for (int j = 0; j < i; j++)
                    if (copy[i].RecordId == copy[j].RecordId || (SameContext(copy[i].Context, copy[j].Context) && copy[i].Sequence == copy[j].Sequence))
                        throw new ArgumentException("Duplicate reference.");
            if (kind == DiagnosticRecordKind.RepeatSummary)
            {
                bool latestFound = false;
                for (int i = 0; i < count; i++)
                {
                    if (copy[i].Outcome != DiagnosticReferenceOutcome.Pending || copy[i].RecordId == first.RecordId || (SameContext(copy[i].Context, first.Context) && copy[i].Sequence == first.Sequence))
                        throw new ArgumentException("Summary members must be distinct pending inputs after the direct first record.");
                    latestFound |= SameReference(copy[i], latest);
                }
                if (!latestFound)
                    throw new ArgumentException("Latest sample must name an exact represented member.");
            }
            // 摘要的 pending 表示本次持久化前的输入身份，不是现场清单的未解析缺口。
            UnresolvedCount = kind == DiagnosticRecordKind.IncidentReference ? unresolved : 0;
            Members = new ReadOnlyCollection<DiagnosticRecordReference>(copy);
            FirstCommitted = first;
            LatestReference = latest;
            MemoryBytes = checked(cost + (first == null ? 0 : ReferenceCost(first)) + (latest == null ? 0 : ReferenceCost(latest)));
            int wire = checked(24 + OperationId.Length + ResultCode.Length);
            if (kind != DiagnosticRecordKind.Event)
            {
                wire += 2;
                for (int i = 0; i < count; i++)
                    wire = checked(wire + ReferenceWire(copy[i]));
            }
            if (kind == DiagnosticRecordKind.RepeatSummary)
                wire = checked(wire + ReferenceWire(first) + ReferenceWire(latest) + 4 + System.Text.Encoding.UTF8.GetByteCount(LatestSanitizedSample));
            if (wire - 8 > MaxPayloadBytes)
                throw new ArgumentException("Diagnostic payload exceeds wire limit.");
            WireBytes = wire;
        }
        internal void ValidateContainer(LogLevel level, LogContext context, string recordId)
        {
            if (!IsSynthetic)
                return;
            if (!recordId.StartsWith("diag-", StringComparison.Ordinal))
                throw new ArgumentException("Synthetic namespace required.");
            if (Kind == DiagnosticRecordKind.IncidentReference)
                foreach (var member in Members)
                    if ((context.Device != null || context.DiagnosticUserId != null || member.Context.Device != null || member.Context.DiagnosticUserId != null)
                        && !SameContext(member.Context, context))
                        throw new ArgumentException("Incident scope mismatch.");
            if (Kind == DiagnosticRecordKind.RepeatSummary)
            {
                if (!MatchesShared(FirstCommitted, level, context) || !MatchesShared(LatestReference, level, context))
                    throw new ArgumentException("Summary scope mismatch.");
                for (int i = 0; i < Members.Count; i++)
                    if (!MatchesShared(Members[i], level, context))
                        throw new ArgumentException("Summary scope mismatch.");
            }
        }
        private static bool SameReference(DiagnosticRecordReference a, DiagnosticRecordReference b) => a.RecordId == b.RecordId && a.Sequence == b.Sequence && a.UtcTimestamp == b.UtcTimestamp && a.Level == b.Level && a.Outcome == b.Outcome && SameContext(a.Context, b.Context) && a.TargetRecordId == b.TargetRecordId && a.TargetSequence == b.TargetSequence && a.TargetUtcTimestamp == b.TargetUtcTimestamp;
        private static bool MatchesShared(DiagnosticRecordReference value, LogLevel level, LogContext context) => value.Level == level && SameContext(value.Context, context);
        private static bool SameContext(LogContext a, LogContext b) => a.AppSessionId == b.AppSessionId && a.UnityInstanceId == b.UnityInstanceId && a.UnityVisitId == b.UnityVisitId && a.IdentityContextVersion == b.IdentityContextVersion && a.DiagnosticUserId == b.DiagnosticUserId && Equals(a.Device, b.Device);
        internal static int ReferenceWire(DiagnosticRecordReference value) => checked(64 + System.Text.Encoding.UTF8.GetByteCount(value.RecordId) + System.Text.Encoding.UTF8.GetByteCount(value.TargetRecordId) + System.Text.Encoding.UTF8.GetByteCount(value.Context.AppSessionId) + System.Text.Encoding.UTF8.GetByteCount(value.Context.UnityInstanceId) + System.Text.Encoding.UTF8.GetByteCount(value.Context.UnityVisitId));
        internal static int ReferenceCost(DiagnosticRecordReference value) => checked(192 + 2 * (value.RecordId.Length + value.TargetRecordId.Length + value.Context.AppSessionId.Length + value.Context.UnityInstanceId.Length + value.Context.UnityVisitId.Length));
        internal static string Token(string value, string name, bool optional)
        {
            if (string.IsNullOrEmpty(value))
            {
                if (optional)
                    return string.Empty;
                throw new ArgumentException("Token required.", name);
            }
            if (value.Length > MaxTokenChars)
                throw new ArgumentException("Token exceeds limit.", name);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (!(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '-' || c == '_' || c == '.' || c == ':'))
                    throw new ArgumentException("Unsafe token.", name);
            }
            return value;
        }
        public static bool IsCriticalEvent(string eventTypeId) => eventTypeId == "session.start" || eventTypeId == "operation.failed" || eventTypeId == "operation.completed" || eventTypeId == "identity.changed";
        internal static string NewSyntheticId() => "diag-" + Guid.NewGuid().ToString("N");
    }
    public static class RecordMemoryCost
    {
        public static int Estimate(int messageChars, int stackChars, DiagnosticRecordMetadata metadata = null)
        {
            if (messageChars < 0 || stackChars < 0)
                throw new ArgumentOutOfRangeException();
            return checked(LoggingOptions.FixedRecordBytes + 2 * checked(messageChars + stackChars) + (metadata?.MemoryBytes ?? 0));
        }
        public static int Of(LogRecord record) => Estimate(record.Message.Length, record.Stack.Length, record.DiagnosticMetadata);
    }
}
