using System;
using System.Threading;
using System.Threading.Tasks;

namespace Logos.Transport
{
    /// <summary>承接一次只读文件上传。启动与取消必须快速返回，不能自行排队重试或在进程外继续读取文件。</summary>
    public interface IFileUploader
    {
        string DestinationId { get; }
        bool TryStart(FileUploadRequest request, out IUploadOperation operation);
    }

    /// <summary>一次尝试临时借出的文件及回执校验信息。只有操作实际关闭后，SDK 才能回收文件。</summary>
    public sealed class FileUploadRequest
    {
        public string Path { get; }
        public string TaskId { get; }
        public string DestinationId { get; }
        public string InstallationId { get; }
        public long AuthorizationRevision { get; }
        public long CleanupGeneration { get; }
        public string BatchId { get; }
        public string BodySha256 { get; }
        public int Length { get; }

        internal FileUploadRequest(string path, UploadRequest request)
        {
            Path = path;
            TaskId = request.TaskId;
            DestinationId = request.DestinationId;
            InstallationId = request.InstallationId;
            AuthorizationRevision = request.AuthorizationRevision;
            CleanupGeneration = request.CleanupGeneration;
            BatchId = request.BatchId;
            BodySha256 = request.BodySha256;
            Length = request.Body.Length;
        }
    }

    /// <summary>作为唯一的上传服务注入。目录、额度和授权由当前运行中的 Logos 会话提供。</summary>
    public sealed class FileUploadAdapter : IUploadService
    {
        private readonly IFileUploader uploader;
        private int busy;
        public string DestinationId { get; }

        public FileUploadAdapter(IFileUploader uploader)
        {
            this.uploader = uploader ?? throw new ArgumentNullException(nameof(uploader));
            DestinationId = TransferValidation.Id(uploader.DestinationId, nameof(uploader));
        }

        /// <summary>独立调用缺少文件额度和授权许可；只能由 SDK 的传输调度器启动文件模式。</summary>
        public bool TryStart(UploadRequest request, out IUploadOperation operation)
        {
            operation = null;
            return false;
        }

        internal bool TryPrepare(UploadRequest request, FileUploadSpool spool, out IUploadOperation operation)
        {
            operation = null;
            if (request == null || spool == null || request.DestinationId != DestinationId
                || Interlocked.CompareExchange(ref busy, 1, 0) != 0)
                return false;

            bool reserved = false;
            try
            {
                reserved = spool.TryReserve(request.Body.Length);
                if (!reserved)
                    return false;
                var file = new Operation(this, request, spool.Delete);
                file.StartPreparation(() => spool.Prepare(request));
                // 从这里起 Prepare 自行处理成功、异常与剩余额度，外层不能提前退还。
                reserved = false;
                operation = file;
                return true;
            }
            finally
            {
                if (operation == null)
                {
                    if (reserved)
                        spool.ReleaseReservation();
                    Volatile.Write(ref busy, 0);
                }
            }
        }

        // 除取消标记外，调度方法均由原 worker 串行驱动。文件系统调用单独调度，
        // 写成文件本身不会启动业务传输，必须等授权门禁重新放行。
        internal sealed class Operation : IUploadOperation
        {
            private readonly FileUploadAdapter owner;
            private readonly UploadRequest request;
            private readonly Action<string> delete;
            private Task<string> prepare;
            private IUploadOperation inner;
            private Task cleanup;
            private UploadResult failure;
            private bool dispatched, innerClosed, closed;
            private int cancelled, innerCancelSent;

            internal Operation(FileUploadAdapter owner, UploadRequest request, Action<string> delete)
            {
                this.owner = owner;
                this.request = request;
                this.delete = delete;
            }

            internal void StartPreparation(Func<string> writeFile)
            {
                prepare = Task.Run(writeFile);
            }

            internal bool ReadyToDispatch => !dispatched && Volatile.Read(ref cancelled) == 0
                && prepare.Status == TaskStatus.RanToCompletion;

            // 仅由调度器在重新确认当下许可后调用，一次尝试最多把路径交付一次。
            internal void Dispatch()
            {
                if (!ReadyToDispatch)
                    return;
                dispatched = true;
                IUploadOperation started = null;
                bool accepted = false;
                try
                {
                    accepted = owner.uploader.TryStart(new FileUploadRequest(prepare.Result, request), out started);
                }
                catch (Exception ex) when (!(ex is OutOfMemoryException))
                {
                    accepted = false;
                }
                finally
                {
                    Volatile.Write(ref inner, started);
                    if (!accepted || started == null)
                    {
                        failure = new UploadResult(UploadStatus.Transient);
                        RequestCancel();
                    }
                    else if (Volatile.Read(ref cancelled) != 0)
                    {
                        CancelInnerOnce(started);
                    }
                }
            }

            public bool TryGetResult(out UploadResult result)
            {
                result = failure;
                if (result != null)
                    return true;
                if (prepare.IsFaulted)
                {
                    // 观察后台异常；诊断不记录用户路径和原始异常文本。
                    _ = prepare.Exception;
                    result = failure = new UploadResult(UploadStatus.Transient);
                    return true;
                }
                if (inner == null || !inner.TryGetResult(out result))
                    return false;
                if (result == null)
                    result = failure = new UploadResult(UploadStatus.Permanent);
                return true;
            }

            public void RequestCancel()
            {
                Interlocked.Exchange(ref cancelled, 1);
                CancelInnerOnce(Volatile.Read(ref inner));
            }

            private void CancelInnerOnce(IUploadOperation operation)
            {
                // 撤权可能与 TryStart 返回交错；观察到实际操作的一方只转发一次取消。
                if (operation == null || Interlocked.Exchange(ref innerCancelSent, 1) != 0)
                    return;
                try
                {
                    operation.RequestCancel();
                }
                catch (Exception ex) when (!(ex is OutOfMemoryException))
                {
                    // 无法发送取消也不授权删除；仍须由实际关闭屏障确认底层不再读取。
                }
            }

            public bool TryClose()
            {
                if (closed)
                    return true;
                if (!prepare.IsCompleted)
                    return false;
                if (prepare.IsFaulted)
                {
                    _ = prepare.Exception;
                }
                else
                {
                    if (!dispatched && Volatile.Read(ref cancelled) == 0)
                        return false;
                    if (inner != null && !innerClosed)
                    {
                        if (!inner.TryClose())
                            return false;
                        innerClosed = true;
                        Volatile.Write(ref inner, null);
                    }
                    if (cleanup == null)
                        cleanup = Task.Run(() => delete(prepare.Result));
                    if (!cleanup.IsCompleted)
                        return false;
                    // 删除失败的字节和清理责任由 spool 保留。业务回执不能因删除失败变成重发。
                    if (cleanup.IsFaulted)
                        _ = cleanup.Exception;
                }
                closed = true;
                Volatile.Write(ref owner.busy, 0);
                return true;
            }
        }
    }
}
