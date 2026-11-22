using Logos.Storage;
namespace Logos.Transport
{
    // 仅使用自有快照。重复附加在新构造之前比较值及 provider、service 的身份。
    internal sealed class TransportConfiguration : StorageConfiguration
    {
        internal readonly TransferPolicy Transfer;
        internal readonly IStorageKeyProvider TransferKeys;
        internal readonly IUploadService Service;
        internal readonly string Installation, Destination;
        internal TransportConfiguration(LoggingOptions logging, PolicySnapshot capture, FrozenStorageOptions storage, TransferPolicy transfer, IStorageKeyProvider storageKeys, IStorageKeyProvider transferKeys, IJournalTimeAuthority time, IUploadService service, string installation, string destination, string directory, UnityStorageDependencies dependencies)
            : base(logging, capture, storage, storageKeys, time, directory, dependencies)
        {
            Transfer = transfer;
            TransferKeys = transferKeys;
            Service = service;
            Installation = installation;
            Destination = destination;
        }
        // 预算取初始化时冻结的值，服务与密钥提供器则按对象身份参与重复初始化判断。
        internal bool Matches(TransportConfiguration b)
        {
            if (!MatchesStorage(b) || !ReferenceEquals(TransferKeys, b.TransferKeys) || !ReferenceEquals(Service, b.Service)
                || Installation != b.Installation || Destination != b.Destination || Transfer.Total != b.Transfer.Total || Transfer.Error != b.Transfer.Error)
                return false;
            if ((Transfer.AutoLimits == null) != (b.Transfer.AutoLimits == null))
                return false;
            var a = Transfer.ConfiguredAutoLimits;
            var c = b.Transfer.ConfiguredAutoLimits;
            if ((a == null) != (c == null) || a != null && (a.SourceBytes != c.SourceBytes || a.EncodedBytes != c.EncodedBytes || a.Batches != c.Batches))
                return false;
            return true;
        }
    }
}
