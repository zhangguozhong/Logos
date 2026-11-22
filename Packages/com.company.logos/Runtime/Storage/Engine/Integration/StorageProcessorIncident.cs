using System;
namespace Logos.Storage
{
    // 派生现场的成败与其引用的原始记录分别结算；清理窗口不得改写已经交付的原始记录结果。
    public sealed partial class StorageBatchProcessor
    {
        private readonly BoundedIncidentContext incident;
        private bool incidentTurn = true;
        private long incidentDropped;
        private DiagnosticControlState incidentControl;
        private long incidentDay = -1, incidentWrite, incidentCleanup, incidentFence;
        public long IncidentDroppedRecords => incidentDropped;
        public long IncidentSuppressedWindows => incident?.Suppressed ?? 0;
        private bool HasReadyDiagnostic(long now) => (aggregation?.HasReadySummary(now) ?? false)
            || (incident != null && incident.Active && !incident.Owned && (incident.Frozen || now >= incident.Deadline));
        partial void ObservePreparedRecord(PreparedRecordSlot slot)
        {
            if (incident == null)
                return;
            DiagnosticControlBoundaryChanged();
            var control = incidentControl;
            if (control != null && (!control.Enabled || !BoundedIncidentContext.SameContext(control.Context, slot.Descriptor.Context)))
                return;
            if (!diagnosticBudgetFence.IsCurrent(incidentFence) || System.Threading.Volatile.Read(ref pendingClear) != null
                || journal.Clear.IsActive || (clearTargets != null && clearTargets.Contains(activeDay)))
                return;
            incident.Observe(slot.Descriptor, lastNow, diagnosticAttempts < 256 && diagnosticAttemptBytes < 1024 * 1024);
        }
        partial void NotifyRecordTerminal(long sequence, LogStorageOutcomeKind kind, AdmissionReason reason, DiagnosticRecordReference target)
        {
            incident?.Terminal(sequence, kind, target, lastNow);
        }
        partial void DiagnosticControlBoundaryChanged()
        {
            if (incident == null)
                return;
            var control = diagnosticBridge?.ReadControlState() ?? CurrentDiagnosticControl;
            long fence = diagnosticBudgetFence.Capture();
            bool changed = incidentDay != activeDay || incidentWrite != journal.WriteGeneration || incidentCleanup != journal.CleanupGeneration
                || incidentFence != fence || (incidentControl != null && (control == null || control.Enabled != incidentControl.Enabled
                || control.PipelineGeneration != incidentControl.PipelineGeneration || control.PolicyRevision != incidentControl.PolicyRevision
                || control.ContextRevision != incidentControl.ContextRevision || control.RequestRevision != incidentControl.RequestRevision));
            if (changed)
                DiscardIncident(AdmissionReason.Cleared);
            incidentControl = control;
            incidentDay = activeDay;
            incidentWrite = journal.WriteGeneration;
            incidentCleanup = journal.CleanupGeneration;
            incidentFence = fence;
        }
        private bool PrepareIncident()
        {
            var record = incident.CreateRecord();
            if (record == null || !prepared.Prepare(record, records, true, out _))
            {
                incident.Discard();
                if (incidentDropped < long.MaxValue)
                    incidentDropped++;
                return true;
            }
            incident.MarkOwned();
            prepared.Token = new StorageOwnershipToken(StorageOwnershipRole.IncidentSynthetic, 0, incident.Generation);
            prepared.Classified = true;
            return true;
        }
        private void DiscardIncident(AdmissionReason reason)
        {
            if (incident == null)
                return;
            if (incident.Owned)
            {
                if (!prepared.IsEmpty && prepared.Token.Role == StorageOwnershipRole.IncidentSynthetic)
                {
                    ResolveToken(prepared.Token, reason, false);
                    prepared.Clear();
                }
                else
                {
                    // 有限 ledger 查找；已转交 token 必须走正常 Publish，不能裸清除。
                    if (ContainsIncident(normal))
                        Publish(normal, appendUncertainNormal ? AdmissionReason.UncertainIo : reason);
                    if (ContainsIncident(error))
                        Publish(error, appendUncertainError ? AdmissionReason.UncertainIo : reason);
                }
            }
            incident.Discard();
        }
        private static bool ContainsIncident(BoundedBlockStaging slot)
        {
            for (int i = 0; i < slot.Count; i++)
                if (slot.TokenAt(i).Role == StorageOwnershipRole.IncidentSynthetic)
                    return true;
            return false;
        }
        private long IncidentWake(long now, long next)
        {
            if (incident == null || !incident.Active || incident.Owned)
                return next;
            if (!incident.Frozen && now < incident.Deadline)
                return Math.Min(next, incident.Deadline);
            return prepared.IsEmpty ? Math.Min(next, now) : next;
        }
    }
}
