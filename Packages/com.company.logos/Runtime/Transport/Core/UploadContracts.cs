using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
namespace Logos.Transport
{
    /// <summary>启动一次尝试，不等待、不重试，也不保留隐藏队列。操作借用不可变正文直到实际关闭。</summary>
    public interface IUploadService
    {
        string DestinationId
        {
            get;
        }
        bool TryStart(UploadRequest request, out IUploadOperation operation);
    }
    // 结果可先于资源释放就绪，调用方分别轮询结果与关闭状态。
    public interface IUploadOperation
    {
        bool TryGetResult(out UploadResult result);
        void RequestCancel();
        /// <summary>只有底层 I/O 和资源实际结束才为真。仅取消并不足够。</summary>
        bool TryClose();
    }
    public enum UploadStatus
    {
        Accepted, Transient, Throttled, TooLarge, Unauthorized, Permanent
    }
    public sealed class UploadResult
    {
        public UploadStatus Status
        {
            get;
        }
        public string BatchId
        {
            get;
        }
        public string BodySha256
        {
            get;
        }
        public long ReceivedLength
        {
            get;
        }
        public int RetryAfterSeconds
        {
            get;
        }
        public UploadResult(UploadStatus status, string batchId = null, string bodySha256 = null, long receivedLength = 0, int retryAfterSeconds = 0)
        {
            if (retryAfterSeconds < 0 || retryAfterSeconds > 3600 || (int)status < 0 || (int)status > (int)UploadStatus.Permanent || receivedLength < 0 || batchId?.Length > 128 || bodySha256?.Length > 128)
                throw new ArgumentException("Invalid bounded upload result.");
            Status = status;
            BatchId = batchId;
            BodySha256 = bodySha256;
            ReceivedLength = receivedLength;
            RetryAfterSeconds = retryAfterSeconds;
        }
    }
    /// <summary>不暴露可变内存或底层数组。服务适配器可流式处理，或自行复制并明确计入预算。</summary>
    public sealed class UploadBody
    {
        private readonly byte[] bytes;
        private readonly int length;
        public int Length => length;
        public byte this[int index] => index >= 0 && index < length ? bytes[index] : throw new ArgumentOutOfRangeException(nameof(index));
        public UploadBody(byte[] body) : this(body, false)
        {
        }
        internal UploadBody(byte[] body, bool takeOwnership) : this(body, body?.Length ?? 0, takeOwnership)
        {
        }
        internal UploadBody(byte[] body, int length, bool takeOwnership)
        {
            if (body == null || length < 1 || length > 131072 || length > body.Length)
                throw new ArgumentException("Body must contain 1..128 KiB.");
            bytes = takeOwnership ? body : (byte[])body.Clone();
            this.length = length;
        }
        public byte[] CopyBytes()
        {
            var copy = new byte[length];
            Buffer.BlockCopy(bytes, 0, copy, 0, length);
            return copy;
        }
        public void CopyTo(byte[] destination, int offset) => Buffer.BlockCopy(bytes, 0, destination, offset, length);
        public void WriteTo(Stream destination)
        {
            if (destination == null)
                throw new ArgumentNullException(nameof(destination));
            destination.Write(bytes, 0, length);
        }
        internal string Hash()
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(bytes, 0, length);
                var text = new StringBuilder(64);
                foreach (byte b in hash)
                    text.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
                return text.ToString();
            }
        }
    }
    public sealed class UploadRequest
    {
        public string TaskId
        {
            get;
        }
        public string DestinationId
        {
            get;
        }
        public string InstallationId
        {
            get;
        }
        public long AuthorizationRevision
        {
            get;
        }
        public long CleanupGeneration
        {
            get;
        }
        public string BatchId
        {
            get;
        }
        public string BodySha256 => BatchId;
        public UploadBody Body
        {
            get;
        }
        internal UploadRequest(string taskId, string destinationId, string installationId, long authorizationRevision, long cleanupGeneration, UploadBody body)
        {
            TaskId = TransferValidation.Id(taskId, nameof(taskId));
            DestinationId = TransferValidation.Id(destinationId, nameof(destinationId));
            InstallationId = TransferValidation.Id(installationId, nameof(installationId));
            if (authorizationRevision < 0 || cleanupGeneration < 0)
                throw new ArgumentException("Negative revision/generation.");
            Body = body ?? throw new ArgumentNullException(nameof(body));
            AuthorizationRevision = authorizationRevision;
            CleanupGeneration = cleanupGeneration;
            BatchId = body.Hash();
        }
        public bool IsAcknowledged(UploadResult result) => result != null && result.Status == UploadStatus.Accepted && result.BatchId == BatchId && result.BodySha256 == BodySha256 && result.ReceivedLength == Body.Length;
    }
}
