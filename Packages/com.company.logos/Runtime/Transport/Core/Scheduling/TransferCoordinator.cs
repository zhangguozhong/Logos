using System;
using System.Threading;
using Logos.Storage;
namespace Logos.Transport
{
    internal enum TransferAdmission
    {
        Accepted, Existing, Busy, Conflict, Unauthorized, NotReady, Invalid
    }
    // 手动持久准入与一个工作线程独占的导出、发送状态机。
    // 除发布外的所有入口都属于原 Journal 工作线程。
    internal sealed partial class TransferCoordinator : IDisposable
    {
        private readonly JournalSession journal;
        private readonly TransferStateStore store;
        private readonly TransferAccessController access;
        private readonly IUploadService service;
        private readonly IStorageKeyProvider exportKeys;
        private readonly TransferPolicy policy;
        private readonly string installation, destination;
        private readonly TransferTimeAuthority time = new TransferTimeAuthority();
        private readonly object publication = new object();
        private readonly RetrievalRequest[] admitted = new RetrievalRequest[3];
        private readonly long[] admittedRevision = new long[3];
        private TransferAuthorizationSnapshot publishedSnapshot, current;
        private long publicationVersion, appliedVersion, publishedAnchor, desiredFloor = -1;
        private bool initialized;
        private volatile bool ready;
        private int owner;
        private long lastNow;
        private string reason = "AuthorizationUnknown";
        internal TransferCoordinator(JournalSession journal, TransferStateStore store, TransferAccessController access, IUploadService service, IStorageKeyProvider exportKeys, TransferPolicy policy, string installation, string destination)
        {
            this.journal = journal ?? throw new ArgumentNullException(nameof(journal));
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.access = access ?? throw new ArgumentNullException(nameof(access));
            this.service = service ?? throw new ArgumentNullException(nameof(service));
            this.exportKeys = exportKeys ?? throw new ArgumentNullException(nameof(exportKeys));
            this.policy = policy ?? throw new ArgumentNullException(nameof(policy));
            this.installation = TransferValidation.Id(installation, nameof(installation));
            this.destination = TransferValidation.Id(destination, nameof(destination));
            access.SetCoordinatorGate(false, 0);
        }
        private void CheckOwner()
        {
            int id = Thread.CurrentThread.ManagedThreadId;
            if (owner == 0)
                owner = id;
            else if (owner != id)
                throw new InvalidOperationException("Coordinator belongs to the original worker.");
        }
        internal bool IsReady => ready;
        internal TransferHealthSnapshot Health => new TransferHealthSnapshot(reason, ready, store.Status, permit != null, closing, sessionAttempts, sessionAcknowledged);
        internal bool UpdateAuthorization(TransferAuthorizationSnapshot snapshot, long now)
        {
            if (snapshot == null)
                throw new ArgumentNullException(nameof(snapshot));
            if (now < 0 || snapshot.InstallationId != installation || snapshot.DestinationId != destination)
                return false;
            lock (publication)
            {
                if (!access.Update(snapshot))
                    return false;
                ready = false;
                access.SetCoordinatorGate(false, 0);
                publishedSnapshot = snapshot;
                publishedAnchor = now;
                desiredFloor = Math.Max(desiredFloor, snapshot.Revision - 1);
                publicationVersion++;
                return true;
            }
        }
        internal bool Revoke(long revision)
        {
            lock (publication)
            {
                if (!access.Revoke(revision))
                    return false;
                ready = false;
                access.SetCoordinatorGate(false, 0);
                publishedSnapshot = null;
                desiredFloor = Math.Max(desiredFloor, revision);
                publicationVersion++;
                return true;
            }
        }
        // 手动提交先占用接纳位置，持久化任务行由后续工作线程步骤完成。
        internal TransferAdmission SubmitManual(RetrievalRequest request)
        {
            CheckOwner();
            if (request == null)
                throw new ArgumentNullException(nameof(request));
            try
            {
                TaskSlot.ValidateContext(request.Scope.AppSessionId);
                TaskSlot.ValidateContext(request.Scope.UnityInstanceId);
                TaskSlot.ValidateContext(request.Scope.UnityVisitId);
            }
            catch (ArgumentException)
            {
                return TransferAdmission.Invalid;
            }
            if (background || stopping || clearing || disposed || !ready || current == null || !time.TryUtc(lastNow, out var utc) || !access.IsAuthorized(request, current.Revision, journal.CleanupGeneration, utc))
                return TransferAdmission.Unauthorized;
            int free = -1;
            for (int i = 0; i < 4; i++)
            {
                var existing = store.Durable?.Slot(i)?.Request;
                if (existing != null && existing.TaskId == request.TaskId)
                    return TransferStateStore.SameRequest(existing, request) ? TransferAdmission.Existing : TransferAdmission.Conflict;
                if (i == 3)
                    continue;
                if (admitted[i] != null && admitted[i].TaskId == request.TaskId)
                    return TransferStateStore.SameRequest(admitted[i], request) ? TransferAdmission.Existing : TransferAdmission.Conflict;
                if (existing == null && admitted[i] == null && free < 0)
                    free = i;
            }
            if (free < 0)
                return TransferAdmission.Busy;
            admitted[free] = request;
            admittedRevision[free] = current.Revision;
            return TransferAdmission.Accepted;
        }
        internal void Step(JournalStepContext context, long now)
        {
            CheckOwner();
            if (disposed || context.ShouldYield || context.StopRequested)
                return;
            lastNow = now;
            if (current != null && (!time.TryUtc(now, out var liveUtc) || liveUtc >= current.ExpiresUtc))
            {
                ready = false;
                access.SetCoordinatorGate(false, journal.CleanupGeneration);
            }
            if (permit != null)
            {
                DriveOperation(now);
                return;
            }
            if ((stopping || clearing || exporterClosePending) && exporter != null)
            {
                CloseExporter();
                return;
            }
            if (clearing)
            {
                DrainClosedClearMemory();
                return;
            }
            if (store.Status != TransferStoreStatus.Ready)
            {
                if (store.Status != TransferStoreStatus.Saving)
                {
                    ready = false;
                    access.SetCoordinatorGate(false, journal.CleanupGeneration);
                }
                if (store.Status != TransferStoreStatus.Disposed)
                    store.Step(context, now);
                // 保存可能在本单元内失败并进入 Loading；返回前先关闭门禁。
                if (store.Status != TransferStoreStatus.Ready && store.Status != TransferStoreStatus.Saving)
                {
                    ready = false;
                    access.SetCoordinatorGate(false, journal.CleanupGeneration);
                }
                reason = store.Status == TransferStoreStatus.Blocked ? "StateBlocked" : "StatePending";
                return;
            }
            if (!initialized)
            {
                access.InitializeRevocationFloor(store.Durable.RevokedThrough);
                initialized = true;
                InitializeCounts();
            }
            if (CompleteAcknowledgment())
                return;
            long floor, version;
            lock (publication)
            {
                floor = Math.Max(desiredFloor, store.Durable.RevokedThrough);
                version = publicationVersion;
                if (appliedVersion != version)
                {
                    // 发布可能在此工作单元已运行时捕获更晚的时钟。
                    // 延后处理且不消耗版本；实际观察到的回退仍会使其失效。
                    if (publishedSnapshot != null && publishedAnchor > now && !time.IsMonotonicRollback(now))
                    {
                        reason = "AuthorizationPending";
                        return;
                    }
                    current = publishedSnapshot;
                    appliedVersion = version;
                    if (current != null && !time.TryAnchor(current.TrustedUtc, publishedAnchor, now))
                    {
                        current = null;
                        reason = "TrustedTimeInvalid";
                    }
                }
            }
            DateTime utc = default;
            bool trusted = current != null && time.TryUtc(now, out utc);
            // 不具权威性的上下文，只可在同一天发布撤销或取消。
            if (!trusted)
                utc = new DateTime(0, DateTimeKind.Utc);
            var ctx = new TransferSaveContext(utc, policy.Total, policy.Error, trusted);
            if (store.Durable.RevokedThrough < floor)
            {
                ready = false;
                access.SetCoordinatorGate(false, journal.CleanupGeneration);
                var rows = Rows();
                for (int i = 0; i < 4; i++)
                    if (rows[i] != null && rows[i].Revision <= floor)
                        rows[i] = CancelRow(rows[i]);
                if (exporter != null)
                {
                    CloseExporter();
                    return;
                }
                bool revokedPending = store.Durable.Pending != null && store.Durable.Pending.Revision <= floor;
                if (store.RequestSave(Copy(rows, revoked: floor, replacePending: revokedPending), ctx, true) && revokedPending)
                    ResetAttempt();
                reason = "RevocationPending";
                return;
            }
            access.InitializeRevocationFloor(store.Durable.RevokedThrough);
            if (stopping || clearing)
            {
                ready = false;
                access.SetCoordinatorGate(false, journal.CleanupGeneration);
                reason = clearing ? "Clearing" : "Stopped";
                return;
            }
            for (int i = 0; i < 3; i++)
                if (admitted[i] != null && store.Durable.Slot(i) != null)
                    admitted[i] = null;
            if (trusted && ExpireTask(utc, new TransferSaveContext(utc, policy.Total, policy.Error, false)))
                return;
            if (!trusted || current.Revision <= store.Durable.RevokedThrough || utc >= current.ExpiresUtc)
            {
                ready = false;
                access.SetCoordinatorGate(false, journal.CleanupGeneration);
                if (exporter != null)
                    CloseExporter();
                reason = "AuthorizationUnknownOrExpired";
                return;
            }
            long day = utc.Ticks / TimeSpan.TicksPerDay;
            if (store.Durable.NetworkDay != day)
            {
                ready = false;
                access.SetCoordinatorGate(false, journal.CleanupGeneration);
                var s = store.Durable;
                store.RequestSave(new TransferState(day, policy.Total, policy.Error, cleanupGeneration: s.CleanupGeneration, revokedThrough: s.RevokedThrough, nextSlotGeneration: s.NextSlotGeneration, tasks: Rows(), pending: s.Pending, autoBookmark: s.AutoBookmark, autoTaskCounter: s.AutoTaskCounter, globalThrottleUtc: Math.Max(s.GlobalThrottleUtc, store.KnownGlobalThrottleUtc)), ctx);
                reason = "NetworkDayPending";
                return;
            }
            if (store.Durable.UnknownUsage)
            {
                ready = false;
                access.SetCoordinatorGate(false, journal.CleanupGeneration);
                reason = "UnknownUsage";
                return;
            }
            lock (publication)
            {
                if (publicationVersion != version)
                    return;
                access.SetCoordinatorGate(true, journal.CleanupGeneration);
                ready = true;
            }
            reason = "ManualAdmissionReady";
            if (background && !settling)
                return;
            for (int i = 0; i < 3; i++)
                if (admitted[i] != null)
                {
                    var rows = Rows();
                    if (rows[i] != null)
                    {
                        admitted[i] = null;
                        continue;
                    }
                    ulong generation = store.Durable.NextSlotGeneration;
                    if (generation == ulong.MaxValue)
                    {
                        ready = false;
                        reason = "SlotSequenceExhausted";
                        return;
                    }
                    bool valid = admittedRevision[i] == current.Revision && access.IsAuthorized(admitted[i], current.Revision, journal.CleanupGeneration, utc);
                    rows[i] = new TaskSlot(admitted[i], generation, admittedRevision[i], journal.CleanupGeneration, valid ? TransferTaskState.Queued : TransferTaskState.Canceled, reason: valid ? TransferTerminalReason.None : TransferTerminalReason.ReauthorizationRequired);
                    // 不可变候选的两个页面都发布完成前，保留内存槽位预留。
                    if (store.RequestSave(Copy(rows, nextGeneration: generation + 1), ctx))
                    {
                        countGeneration[i] = generation;
                        countsKnown[i] = true;
                        ackRecords[i] = ackBatches[i] = 0;
                    }
                    return;
                }
            if (DriveAutomatic(now, utc, ctx))
                return;
            DriveExecution(context, now, utc, ctx);
        }
        private TaskSlot[] Rows() => new[] { store.Durable.Slot(0), store.Durable.Slot(1), store.Durable.Slot(2), store.Durable.Slot(3) };
        private static TaskSlot CancelRow(TaskSlot t) => new TaskSlot(t.Request, t.Generation, t.Revision, t.CleanupGeneration, TransferTaskState.Canceled, t.AutoError, t.MaxRecords, t.GeneratedBatches, t.SourceUsed, t.EncodedUsed, t.Bookmark, t.Attempts, t.Splits, TransferTerminalReason.ReauthorizationRequired, t.RetryUtc, t.IdentityCoverageGap);
        private TransferState Copy(TaskSlot[] rows, long? revoked = null, ulong? nextGeneration = null, PendingBatch pending = null, bool replacePending = false, ExportBookmark autoBookmark = null, bool replaceAutoBookmark = false)
        {
            var s = store.Durable;
            return new TransferState(s.NetworkDay, s.OriginalU, s.OriginalV, s.NormalUsed, s.ErrorUsed, s.CleanupGeneration, revoked ?? s.RevokedThrough, s.UnknownUsage, nextGeneration ?? s.NextSlotGeneration, rows, replacePending ? pending : s.Pending, replaceAutoBookmark ? autoBookmark : s.AutoBookmark, s.AutoTaskCounter, Math.Max(s.GlobalThrottleUtc, store.KnownGlobalThrottleUtc));
        }
    }
}
