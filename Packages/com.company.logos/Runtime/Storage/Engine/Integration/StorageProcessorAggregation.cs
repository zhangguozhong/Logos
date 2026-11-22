using System;
namespace Logos.Storage
{
    // 重复成员由桶持有，直写记录由暂存槽持有；发布成功或失败时按令牌回到对应所有者，防止双重结算。
    public sealed partial class StorageBatchProcessor
    {
        private readonly PreparedRecordSlot prepared;
        private readonly BoundedRepeatAggregation aggregation;
        private long representedCount, derivedPersistedCount;
        private int diagnosticAttempts;
        private long diagnosticAttemptBytes, diagnosticBudgetRejections;
        public long DiagnosticBudgetRejections => diagnosticBudgetRejections;
        public long RepresentedRecords => representedCount;
        public long DerivedPersistedRecords => derivedPersistedCount;
        private int OwnedOriginalCount()
        {
            int count = normal.DirectCount + error.DirectCount;
            if (prepared != null && !prepared.IsEmpty && !prepared.Token.IsSynthetic)
                count++;
            if (aggregation != null)
                foreach (var b in aggregation.Buckets)
                    count += b.Count;
            return count;
        }
        // C 可在同一工作线程实现这两个固定通知；默认没有委托、队列或逐记录订阅分配。
        partial void ObservePreparedRecord(PreparedRecordSlot slot);
        partial void NotifyRecordTerminal(long sequence, LogStorageOutcomeKind kind, AdmissionReason reason, DiagnosticRecordReference target);
        private bool DiagnosticOwned => prepared != null && (!prepared.IsEmpty || (aggregation != null && !aggregation.IsEmpty) || (incident?.Active ?? false));
        // 两个块、prepared、两个桶以及未来现场槽都预留唯一终态序列。
        private ulong OutcomeReserve => prepared == null ? 2UL : 8UL;
        private ulong NextOutcomeSequence()
        {
            if (outcomeSequence == ulong.MaxValue)
                throw new InvalidOperationException("Outcome sequence exhausted.");
            return ++outcomeSequence;
        }
        private bool DriveDiagnostic(long now, bool freeze)
        {
            if (prepared == null)
                return false;
            if (freeze)
            {
                aggregation?.Freeze();
                incident?.Freeze();
                incident?.ClearRing();
            }
            incident?.Advance(now);
            int readyIndex = aggregation?.Ready(now) ?? -1;
            if (!prepared.IsEmpty)
            {
                if (prepared.HasOutcome)
                    return false;
                if (aggregation != null && aggregation.Consume(prepared, now, freeze))
                    return true;
                var slot = prepared.Descriptor.Level >= LogLevel.Error ? error : normal;
                if (slot.TryStagePrepared(prepared, encodingHeader, encodingKey, now) != StageResult.Staged)
                    return false;
                if (!prepared.Token.IsSynthetic && prepared.Descriptor.Level >= LogLevel.Error && now >= nextErrorSeal)
                {
                    slot.RequestSeal(BlockSealReason.FirstError);
                    nextErrorSeal = Add(now, StorageRuntimeLimits.FaultSyncIntervalTicks);
                }
                prepared.Clear();
                return true;
            }
            if (incident?.Ready == true && (readyIndex < 0 || incidentTurn))
            {
                incidentTurn = false;
                return PrepareIncident();
            }
            int index = readyIndex;
            if (index < 0)
                return false;
            incidentTurn = true;
            var bucket = aggregation.Buckets[index];
            var summary = aggregation.Summary(index);
            if (!prepared.Prepare(summary, records, true, out _))
            {
                ResolveBucket(bucket, AdmissionReason.EncodingFailed, false);
                return true;
            }
            bucket.SummaryOwned = true;
            bucket.SummaryTarget = BoundedRepeatAggregation.Reference(summary, DiagnosticReferenceOutcome.Committed);
            prepared.Token = new StorageOwnershipToken(StorageOwnershipRole.SummaryOfAggregate, index, bucket.Generation);
            prepared.Classified = true;
            return true;
        }
        private void ResolveToken(StorageOwnershipToken token, AdmissionReason reason, bool committed)
        {
            if (token.Role == StorageOwnershipRole.IncidentSynthetic)
            {
                if (incident != null && incident.Resolve(token.Generation))
                {
                    if (!committed && incidentDropped < long.MaxValue)
                        incidentDropped++;
                }
                return;
            }
            if (token.Role == StorageOwnershipRole.Direct || aggregation == null)
                return;
            var bucket = aggregation.Buckets[token.Bucket];
            if (!bucket.Active || bucket.Generation != token.Generation || bucket.Terminal.Sequence != 0)
                return;
            if (token.Role == StorageOwnershipRole.FirstOfAggregate)
            {
                if (bucket.FirstResolved)
                    return;
                bucket.FirstResolved = true;
                bucket.FirstCommitted = committed;
                if (!committed)
                {
                    bucket.Frozen = true;
                    ResolveBucket(bucket, reason, false);
                }
            }
            else
                ResolveBucket(bucket, reason, committed);
        }
        private void ResolveBucket(BoundedRepeatAggregation.Bucket bucket, AdmissionReason reason, bool represented)
        {
            if (!bucket.Active || bucket.Terminal.Sequence != 0)
                return;
            if (bucket.Count == 0)
            {
                bucket.Clear();
                return;
            }
            bucket.Frozen = true;
            bucket.Terminal = new LogStorageOutcome(NextOutcomeSequence(), represented ? LogStorageOutcomeKind.RepresentedBySummary : LogStorageOutcomeKind.Dropped,
                represented ? AdmissionReason.Accepted : reason, PreparedRecordSlot.Counts(bucket.First.Level, bucket.Count));
            for (int i = 0; i < bucket.Count; i++)
                NotifyRecordTerminal(bucket.Members[i].Sequence, bucket.Terminal.Kind, bucket.Terminal.Reason, represented ? bucket.SummaryTarget : null);
            if (represented)
                representedCount += bucket.Count;
            else
                droppedCount += bucket.Count;
        }
        // 所有块先终结，再终结尚未进块的槽与成员；已发布的成功结果不可改写。
        private void DropDiagnostic(AdmissionReason reason)
        {
            if (prepared == null)
                return;
            aggregation?.Freeze();
            DiscardIncident(reason);
            if (!prepared.IsEmpty && !prepared.HasOutcome)
            {
                var token = prepared.Token;
                if (!token.IsSynthetic)
                {
                    prepared.Publish(NextOutcomeSequence(), reason);
                    droppedCount++;
                    NotifyRecordTerminal(prepared.Descriptor.Sequence, LogStorageOutcomeKind.Dropped, reason, null);
                }
                ResolveToken(token, reason, false);
                if (token.IsSynthetic)
                    prepared.Clear();
            }
            if (aggregation != null)
                foreach (var bucket in aggregation.Buckets)
                    if (bucket.Active && bucket.Terminal.Sequence == 0)
                        ResolveBucket(bucket, reason, false);
        }
        private long DiagnosticWake(long now, long next)
        {
            if (prepared == null || disposed || completed || journal.Status != JournalStatus.Ready || encodingKey == null
                || activeDay < 0 || journal.DirectoryInventoryInProgress || journal.Clear.IsActive
                || (clearTargets != null && clearTargets.Contains(activeDay)) || (transitioning && transitionExpired))
                return next;
            if (!prepared.IsEmpty && !prepared.HasOutcome)
            {
                var slot = prepared.Descriptor.Level >= LogLevel.Error ? error : normal;
                if (slot.CanStage)
                    return Math.Min(next, now);
            }
            next = IncidentWake(now, next);
            if (aggregation != null)
                foreach (var b in aggregation.Buckets)
                {
                    if (!b.Active || b.Terminal.Sequence != 0 || b.SummaryOwned)
                        continue;
                    if (!b.Frozen && now < b.Deadline)
                        next = Math.Min(next, b.Deadline);
                    else if (b.FirstResolved && prepared.IsEmpty)
                        next = Math.Min(next, now);
                }
            return next;
        }
    }
}
