using Logos.Storage;

namespace Logos.Transport
{
    /// <summary>在主线程初始化时读取。服务、密钥和身份由应用显式提供。</summary>
    public sealed class LogosUploadOptions
    {
        public TransportSettings Settings { get; set; }
        // 这里注入传输用密钥提供器，应用可将其与本地日志存储密钥分别管理。
        public IStorageKeyProvider TransferKeys { get; set; }
        public IUploadService Service { get; set; }
        public string InstallationId { get; set; }
        public string DestinationId { get; set; }
    }
}
