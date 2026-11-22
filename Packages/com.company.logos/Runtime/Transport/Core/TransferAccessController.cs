using System;
using System.Threading;
namespace Logos.Transport
{
    public sealed class TransferSendPermit
    {
        internal readonly RetrievalRequest Request;
        internal readonly long Revision, Generation;
        internal int Cancelled, State;
        internal IUploadOperation Operation;
        internal TransferSendPermit(RetrievalRequest request, long revision, long generation)
        {
            Request = request;
            Revision = revision;
            Generation = generation;
        }
        public bool IsCancellationRequested => Volatile.Read(ref Cancelled) != 0;
        public bool TryGetResult(out UploadResult result)
        {
            var operation = Volatile.Read(ref Operation);
            result = null;
            return operation != null && operation.TryGetResult(out result);
        }
    }
    /// <summary>授权发布、撤销和单个许可注册共享一个短时锁。持锁期间绝不调用服务代码。</summary>
    public sealed class TransferAccessController
    {
        private readonly object gate = new object();
        private TransferAuthorizationSnapshot current;
        private long lastRevision = -1, generation, durableFloor = -1;
        private bool coordinatorEnabled = true;
        private TransferSendPermit active;
        public bool Update(TransferAuthorizationSnapshot snapshot, long? cleanupGeneration = null)
        {
            if (snapshot == null)
                throw new ArgumentNullException(nameof(snapshot));
            if (cleanupGeneration < 0)
                throw new ArgumentOutOfRangeException(nameof(cleanupGeneration));
            lock (gate)
            {
                if (snapshot.Revision <= lastRevision || cleanupGeneration < generation)
                    return false;
                current = snapshot;
                lastRevision = snapshot.Revision;
                generation = cleanupGeneration ?? generation;
                if (active != null)
                    Volatile.Write(ref active.Cancelled, 1);
                return true;
            }
        }
        public bool Revoke(long revision)
        {
            lock (gate)
            {
                if (revision < 0 || revision < lastRevision || (revision == lastRevision && current == null))
                    return false;
                current = null;
                lastRevision = revision;
                if (active != null)
                    Volatile.Write(ref active.Cancelled, 1);
                return true;
            }
        }
        private bool Allows(RetrievalRequest request, long revision, long cleanupGeneration, DateTime utc) => coordinatorEnabled && revision > durableFloor && request != null && cleanupGeneration == generation && current != null && current.Allows(request, revision, utc);
        internal void CancelPermit(TransferSendPermit permit)
        {
            lock (gate)
                if (ReferenceEquals(active, permit) && permit != null)
                    Volatile.Write(ref permit.Cancelled, 1);
        }
        internal void InitializeRevocationFloor(long floor)
        {
            if (floor < -1)
                throw new ArgumentOutOfRangeException(nameof(floor));
            lock (gate)
            {
                durableFloor = Math.Max(durableFloor, floor);
                lastRevision = Math.Max(lastRevision, floor);
                if (active != null && active.Revision <= durableFloor)
                    Volatile.Write(ref active.Cancelled, 1);
            }
        }
        internal void SetCoordinatorGate(bool enabled, long cleanupGeneration)
        {
            if (cleanupGeneration < 0)
                throw new ArgumentOutOfRangeException(nameof(cleanupGeneration));
            lock (gate)
            {
                coordinatorEnabled = enabled;
                if (cleanupGeneration > generation)
                {
                    generation = cleanupGeneration;
                    if (active != null)
                        Volatile.Write(ref active.Cancelled, 1);
                }
                if (!enabled && active != null)
                    Volatile.Write(ref active.Cancelled, 1);
            }
        }
        public bool IsAuthorized(RetrievalRequest request, long revision, long cleanupGeneration, DateTime utc)
        {
            lock (gate)
                return Allows(request, revision, cleanupGeneration, utc);
        }
        public bool Matches(RetrievalRequest request, long revision, long cleanupGeneration, DateTime utc, LogRecord record)
        {
            lock (gate)
                return Allows(request, revision, cleanupGeneration, utc) && request.Scope.Matches(record) && current.Matches(record, request.Scope.DiagnosticUserId)
                    && request.Scope.ContainsDiagnosticReferences(record) && current.ContainsDiagnosticReferences(record, request.Scope.DiagnosticUserId);
        }
        internal bool HasUnverifiableIdentity(RetrievalRequest request, long revision, long cleanupGeneration, DateTime utc, LogRecord record)
        {
            lock (gate)
            {
                var scope = request.Scope;
                if (!Allows(request, revision, cleanupGeneration, utc) || !scope.MatchesCoordinates(record.Level, record.UtcTimestamp, record.Sequence, record.Context))
                    return false;
                for (int i = 0; i < current.DomainCount; i++)
                {
                    var domain = current.DomainScope(i);
                    if (domain.DiagnosticUserId == scope.DiagnosticUserId && domain.MatchesCoordinates(record.Level, record.UtcTimestamp, record.Sequence, record.Context))
                        return true;
                }
                return false;
            }
        }
        public bool TryRegister(RetrievalRequest request, long revision, long cleanupGeneration, DateTime utc, out TransferSendPermit permit)
        {
            lock (gate)
            {
                permit = null;
                if (active != null || !Allows(request, revision, cleanupGeneration, utc))
                    return false;
                permit = active = new TransferSendPermit(request, revision, cleanupGeneration);
                return true;
            }
        }
        // 调用外部服务期间可能并发撤销，返回后仍须处理已被标记取消的许可。
        public bool TryStart(TransferSendPermit permit, IUploadService service, UploadRequest request)
            => TryStart(permit, service, request, null);

        internal bool TryStart(TransferSendPermit permit, IUploadService service, UploadRequest request, FileUploadSpool spool)
        {
            if (service == null || request == null)
                throw new ArgumentNullException();
            string destination = service.DestinationId;
            lock (gate)
            {
                if (permit == null || !ReferenceEquals(active, permit) || permit.State != 0 || permit.IsCancellationRequested || destination != permit.Request.DestinationId || request.DestinationId != destination || request.TaskId != permit.Request.TaskId || request.InstallationId != permit.Request.InstallationId || request.AuthorizationRevision != permit.Revision || request.CleanupGeneration != permit.Generation)
                    return false;
                permit.State = 1;
            }
            IUploadOperation operation = null;
            bool accepted = false;
            try
            {
                if (!permit.IsCancellationRequested)
                    accepted = spool != null && service is FileUploadAdapter file
                        ? file.TryPrepare(request, spool, out operation)
                        : service.TryStart(request, out operation);
            }
            finally
            {
                lock (gate)
                {
                    Volatile.Write(ref permit.Operation, operation);
                    permit.State = operation == null ? 3 : 2;
                }
                if (operation != null && permit.IsCancellationRequested)
                    operation.RequestCancel();
            }
            return accepted && operation != null;
        }
        public bool TryClose(TransferSendPermit permit)
        {
            IUploadOperation operation;
            lock (gate)
            {
                if (permit == null || !ReferenceEquals(active, permit) || permit.State == 1)
                    return false;
                operation = permit.Operation;
                permit.State = 4;
            }
            if (operation != null)
            {
                // 取消仅是建议；适配器失败不能阻止独立的关闭探针。
                if (permit.IsCancellationRequested)
                    try
                    {
                        operation.RequestCancel();
                    }
                    catch (Exception ex) when (!(ex is OutOfMemoryException))
                    {
                    }
                if (!operation.TryClose())
                {
                    lock (gate)
                        if (ReferenceEquals(active, permit))
                            permit.State = 2;
                    return false;
                }
            }
            lock (gate)
            {
                // 过期或外来许可绝不能释放后续的活跃发送。
                if (!ReferenceEquals(active, permit))
                    return false;
                active = null;
                return true;
            }
        }
    }
}
