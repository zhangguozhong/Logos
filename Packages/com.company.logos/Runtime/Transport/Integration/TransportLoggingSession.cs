using Logos.Storage;
namespace Logos.Transport
{
    /// <summary>一个保留的 Unity 所有者；手动控制仅限主线程，授权与观察可跨线程。</summary>
    public sealed class TransportLoggingSession : ILogosSession
    {
        public LogosSession UnitySession
        {
            get;
        }
        private readonly TransportBatchProcessor processor;
        private readonly TransferControlMailbox controls;
        internal readonly TransportConfiguration Configuration;
        internal TransportLoggingSession(LogosSession owner, TransportBatchProcessor processor, TransportConfiguration configuration)
        {
            UnitySession = owner;
            this.processor = processor;
            Configuration = configuration;
            controls = new TransferControlMailbox(owner.Generation);
            processor.BindControls(controls);
        }
        internal bool Owns(LogosSession owner) => ReferenceEquals(UnitySession, owner) && owner.UsesProcessor(processor);
        public StorageHealthSnapshot GetStorageHealthSnapshot() => processor.GetStorageHealthSnapshot();
        public TransferSnapshot GetTransferSnapshot() => processor.GetTransferSnapshot();
        public bool TryUpdateAuthorization(string instanceId, long generation, TransferAuthorizationSnapshot snapshot)
        {
            if (snapshot == null || !UnitySession.TryGetControlTimestamp(instanceId, generation, out var now))
                return false;
            if (!processor.Coordinator.UpdateAuthorization(snapshot, now))
                return false;
            processor.WakeControl();
            UnitySession.NotifyControl();
            return true;
        }
        public bool TryRevokeAuthorization(string instanceId, long generation, long revision)
        {
            if (!UnitySession.TryGetControlTimestamp(instanceId, generation, out _))
                return false;
            if (!processor.Coordinator.Revoke(revision))
                return false;
            processor.WakeControl();
            UnitySession.NotifyControl();
            return true;
        }
        // 实例和会话代数共同标识调用目标，返回票据后可另行查询工作线程的处理结果。
        public bool TryQueueManual(string instanceId, long generation, RetrievalRequest request, out TransferControlTicket ticket, out TransferControlAdmission admission)
        {
            ticket = default;
            if (!CanQueue(instanceId, generation, out admission))
                return false;
            if (request == null)
            {
                admission = TransferControlAdmission.Invalid;
                return false;
            }
            if (!controls.TryQueue(request, default, false, out ticket, out admission))
                return false;
            processor.WakeControl();
            UnitySession.NotifyControl();
            return true;
        }
        public bool TryQueueRelease(string instanceId, long generation, TransferTaskHandle handle, out TransferControlTicket ticket, out TransferControlAdmission admission)
        {
            ticket = default;
            if (!CanQueue(instanceId, generation, out admission))
                return false;
            if (handle.TaskId == null)
            {
                admission = TransferControlAdmission.Invalid;
                return false;
            }
            if (!controls.TryQueue(null, handle, true, out ticket, out admission))
                return false;
            processor.WakeControl();
            UnitySession.NotifyControl();
            return true;
        }
        private bool CanQueue(string instanceId, long generation, out TransferControlAdmission admission)
        {
            admission = TransferControlAdmission.WrongThread;
            if (!LoggingBootstrap.IsMainThread)
                return false;
            admission = TransferControlAdmission.StaleSession;
            if (instanceId != UnitySession.UnityInstanceId || generation != UnitySession.Generation)
                return false;
            admission = TransferControlAdmission.Stopped;
            return UnitySession.TryGetControlTimestamp(instanceId, generation, out _);
        }
        public bool TryTakeControlResult(TransferControlTicket ticket, out TransferControlResult result) => controls.TryTake(ticket, out result);
    }
}
