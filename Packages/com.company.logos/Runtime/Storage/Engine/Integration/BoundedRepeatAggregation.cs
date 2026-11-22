using System;
namespace Logos.Storage
{
    // 固定两桶，每桶至多八个重复输入；首条直接追加成功是摘要的前置条件。
    // 以脱敏后的完整签名和上下文识别重复记录。每个桶追踪首条及待汇总成员，首条结果决定能否生成摘要。
    internal sealed class BoundedRepeatAggregation : IDisposable
    {
        internal const int BucketCount = 2, MemberLimit = 8;
        internal const long LifetimeTicks = 5 * TimeSpan.TicksPerSecond;
        internal sealed class Bucket
        {
            internal readonly byte[] Signature = new byte[2048];
            internal readonly DiagnosticRecordReference[] Members = new DiagnosticRecordReference[MemberLimit];
            internal int SignatureLength, Count;
            internal long Generation, Deadline;
            internal LogRecord First;
            internal string Sample;
            internal DiagnosticRecordReference SummaryTarget;
            internal bool Frozen, FirstCommitted, FirstResolved, SummaryOwned;
            internal LogStorageOutcome Terminal;
            internal bool Active => First != null;
            internal void Clear()
            {
                Array.Clear(Signature, 0, Signature.Length);
                Array.Clear(Members, 0, Members.Length);
                First = null;
                Sample = null;
                SummaryTarget = null;
                SignatureLength = Count = 0;
                Deadline = 0;
                Frozen = FirstCommitted = FirstResolved = SummaryOwned = false;
                Terminal = default;
            }
        }
        internal readonly Bucket[] Buckets = { new Bucket(), new Bucket() };
        // 包括所有身份字符串、引用对象、描述符、样本及临时摘要元数据的保守上界。
        internal int WorkspaceBytes => BucketCount * (2048 + 24576);
        private readonly int coreLimit;
        private readonly bool consecutiveUnityInfo;
        internal BoundedRepeatAggregation(int coreLimit, bool consecutiveUnityInfo = false)
        {
            this.coreLimit = coreLimit;
            this.consecutiveUnityInfo = consecutiveUnityInfo;
        }
        internal bool IsEmpty
        {
            get
            {
                foreach (var b in Buckets)
                    if (b.Active)
                        return false;
                return true;
            }
        }
        internal bool Consume(PreparedRecordSlot prepared, long now, bool freeze)
        {
            if (prepared.Classified)
                return false;
            prepared.Classified = true;
            var record = prepared.Descriptor;
            foreach (var b in Buckets)
                if (b.Active && (!SameScope(b.First, record) || now >= b.Deadline || freeze))
                    b.Frozen = true;
            if (freeze || prepared.SignatureLength == 0 || record.DiagnosticMetadata?.IsSynthetic == true)
                return false;
            // 候选模式的 Unity Info 段已经在入口定界，不能在落盘前再次跨段合并。
            if (consecutiveUnityInfo && record.SourceId == "Unity" && record.Level == LogLevel.Info)
                return false;
            int vacant = -1;
            for (int i = 0; i < Buckets.Length; i++)
            {
                var b = Buckets[i];
                if (!b.Active)
                {
                    if (b.Generation < long.MaxValue)
                        vacant = i;
                    continue;
                }
                if (!SameScope(b.First, record) || b.First.SourceId != record.SourceId || b.First.EventTypeId != record.EventTypeId || b.Frozen || b.Terminal.Sequence != 0 || b.SignatureLength != prepared.SignatureLength)
                    continue;
                bool equal = true;
                for (int j = 0; j < b.SignatureLength; j++)
                    if (b.Signature[j] != prepared.Signature[j])
                    {
                        equal = false;
                        break;
                    }
                if (!equal)
                    continue;
                var member = Reference(record, DiagnosticReferenceOutcome.Pending);
                for (int j = 0; j < b.Count; j++)
                    if (b.Members[j].RecordId == member.RecordId || b.Members[j].Sequence == member.Sequence)
                        return false;
                if (b.First.RecordId == member.RecordId || b.First.Sequence == member.Sequence)
                    return false;
                b.Members[b.Count] = member;
                string previous = b.Sample;
                b.Sample = prepared.Sample;
                if (!CanSummarize(b, b.Count + 1))
                {
                    b.Members[b.Count] = null;
                    b.Sample = previous;
                    b.Frozen = true;
                    return false;
                }
                b.Count++;
                if (b.Count == MemberLimit)
                    b.Frozen = true;
                prepared.Clear();
                return true;
            }
            if (vacant < 0)
                return false;
            var target = Buckets[vacant];
            target.Generation++;
            target.First = record;
            target.SignatureLength = prepared.SignatureLength;
            Buffer.BlockCopy(prepared.Signature, 0, target.Signature, 0, target.SignatureLength);
            target.Deadline = now > long.MaxValue - LifetimeTicks ? long.MaxValue : now + LifetimeTicks;
            prepared.Token = new StorageOwnershipToken(StorageOwnershipRole.FirstOfAggregate, vacant, target.Generation);
            return false;
        }
        private bool CanSummarize(Bucket bucket, int count)
        {
            var first = bucket.First;
            var context = first.Context;
            var source = first.DiagnosticMetadata;
            var utf8 = System.Text.Encoding.UTF8;
            int firstCost = 192 + 2 * (2 * first.RecordId.Length + context.AppSessionId.Length + context.UnityInstanceId.Length + context.UnityVisitId.Length);
            int firstWire = 64 + 2 * utf8.GetByteCount(first.RecordId) + utf8.GetByteCount(context.AppSessionId) + utf8.GetByteCount(context.UnityInstanceId) + utf8.GetByteCount(context.UnityVisitId);
            int tokenChars = (source?.OperationId.Length ?? 0) + (source?.ResultCode.Length ?? 0);
            int cost = 128 + 2 * (tokenChars + bucket.Sample.Length) + firstCost + DiagnosticRecordMetadata.ReferenceCost(bucket.Members[count - 1]);
            int wire = 24 + tokenChars + 2 + firstWire + DiagnosticRecordMetadata.ReferenceWire(bucket.Members[count - 1]) + 4 + utf8.GetByteCount(bucket.Sample);
            for (int i = 0; i < count; i++)
            {
                cost += DiagnosticRecordMetadata.ReferenceCost(bucket.Members[i]);
                wire += DiagnosticRecordMetadata.ReferenceWire(bucket.Members[i]);
            }
            return LoggingOptions.FixedRecordBytes + cost <= coreLimit && wire - 8 <= DiagnosticRecordMetadata.MaxPayloadBytes
                && wire + 4096 <= StorageRuntimeLimits.MaxPlainBlockBytes - 4;
        }
        private static DiagnosticRecordMetadata Metadata(Bucket bucket, int count)
        {
            var members = new DiagnosticRecordReference[count];
            Array.Copy(bucket.Members, members, count);
            var source = bucket.First.DiagnosticMetadata;
            return new DiagnosticRecordMetadata(DiagnosticRecordKind.RepeatSummary, source?.OperationId, source?.ResultCode,
                source?.DurationTicks ?? 0, members, Reference(bucket.First, DiagnosticReferenceOutcome.Committed), members[count - 1], bucket.Sample);
        }
        internal static DiagnosticRecordReference Reference(LogRecord record, DiagnosticReferenceOutcome outcome)
            => new DiagnosticRecordReference(record.RecordId, record.Sequence, record.UtcTimestamp, record.Context, record.Level, outcome);
        private static bool SameScope(LogRecord a, LogRecord b) => a.QuotaDayTag?.DayId == b.QuotaDayTag?.DayId
            && BoundedIncidentContext.SameContext(a.Context, b.Context);
        internal void Freeze()
        {
            foreach (var b in Buckets)
                if (b.Active)
                    b.Frozen = true;
        }
        // 纯只读门禁：给已到期摘要保留唯一 prepared 槽的下一次使用机会。
        internal bool HasReadySummary(long now)
        {
            foreach (var b in Buckets)
                if (b.Active && (b.Frozen || now >= b.Deadline) && b.FirstCommitted && b.Count > 0
                    && !b.SummaryOwned && b.Terminal.Sequence == 0)
                    return true;
            return false;
        }
        internal int Ready(long now)
        {
            for (int i = 0; i < Buckets.Length; i++)
            {
                var b = Buckets[i];
                if (!b.Active)
                    continue;
                if (now >= b.Deadline)
                    b.Frozen = true;
                if (b.Frozen && b.FirstResolved && b.Count == 0)
                {
                    b.Clear();
                    continue;
                }
                if (b.Frozen && b.FirstCommitted && b.Count > 0 && !b.SummaryOwned && b.Terminal.Sequence == 0)
                    return i;
            }
            return -1;
        }
        internal LogRecord Summary(int index)
        {
            var b = Buckets[index];
            var first = b.First;
            var member = b.Members[0];
            return new LogRecord(first.Level, first.SourceId, first.EventTypeId, DiagnosticRecordMetadata.NewSyntheticId(),
                member.Sequence, member.UtcTimestamp, first.MonotonicTimestamp, "", "", first.Context, false, false,
                first.QuotaDayTag, false, Metadata(b, b.Count));
        }
        internal bool Peek(out LogStorageOutcome outcome)
        {
            outcome = default;
            foreach (var b in Buckets)
                if (b.Terminal.Sequence != 0 && (outcome.Sequence == 0 || b.Terminal.Sequence < outcome.Sequence))
                    outcome = b.Terminal;
            return outcome.Sequence != 0;
        }
        internal bool Ack(ulong sequence)
        {
            foreach (var b in Buckets)
                if (b.Terminal.Sequence == sequence && sequence != 0)
                {
                    b.Clear();
                    return true;
                }
            return false;
        }
        public void Dispose()
        {
            foreach (var b in Buckets)
                b.Clear();
        }
    }
}
