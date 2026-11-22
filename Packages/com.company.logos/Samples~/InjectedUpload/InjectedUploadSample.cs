using System;
using Logos.Storage;
namespace Logos.Transport.Samples
{
    /// <summary>显式宿主组合示例。导入、场景加载或组件启用都不启动任何内容。</summary>
    public sealed class InjectedUploadSample
    {
        public TransportLoggingSession Session
        {
            get;
            private set;
        }
        public bool TryStart(LogosSettings logging, StorageSettings storage, IStorageKeyProvider storageKeys,
            IJournalTimeAuthority time, LogosUploadOptions upload, out TransportInitializationStatus status)
        {
            bool installed = LogosSdk.TryInitialize(logging, storage, storageKeys, time, upload, out var owner, out status);
            // Start 失败后可能仍保留活跃工作线程。此时保留外观接口和调用者的服务。
            Session = owner ?? Session;
            return installed;
        }
        // 读取 Session.UnitySession.UnityInstanceId 后，从应用取得可信 UTC、过期时间、身份和修订号。
        // 先向当前会话发布允许访问的身份与时间范围，后续回捞请求只能在有效授权内取数。
        public bool PublishAuthorization(TransferAuthorizationSnapshot snapshot)
        {
            var owner = Session?.UnitySession;
            return owner != null && Session.TryUpdateAuthorization(owner.UnityInstanceId, owner.Generation, snapshot);
        }
        // 这里申请提交控制请求；最终导出结果和上传回执由后台调度推进。
        public bool TryRetrieve(RetrievalRequest request, out TransferControlTicket ticket, out TransferControlAdmission admission)
        {
            ticket = default;
            admission = TransferControlAdmission.StaleSession;
            var owner = Session?.UnitySession;
            return owner != null && Session.TryQueueManual(owner.UnityInstanceId, owner.Generation, request, out ticket, out admission);
        }
        // 在 Unity 主线程调用，并在后续帧持续轮询；绝不在该线程忙等。
        public bool TryStopAndReleaseHost()
            => LogosSdk.TryStopForUnload(Session, TimeSpan.FromMilliseconds(100), dropPending: true);
    }
}
