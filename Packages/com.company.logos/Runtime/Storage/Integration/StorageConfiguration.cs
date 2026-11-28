namespace Logos.Storage
{
    // 数值配置按值比较，密钥、时间和文件系统提供者按实例比较；重复初始化只有在两类条件均相同时才复用会话。
    internal class StorageConfiguration
    {
        internal readonly LoggingOptions Logging;
        internal readonly PolicySnapshot Capture;
        internal readonly FrozenStorageOptions Storage;
        internal readonly IStorageKeyProvider StorageKeys;
        internal readonly IJournalTimeAuthority Time;
        internal readonly string Directory;
        private readonly IJournalFileSystem fileSystem;
        private readonly IDirectoryLeaseProvider leases;

        internal StorageConfiguration(LoggingOptions logging, PolicySnapshot capture, FrozenStorageOptions storage,
            IStorageKeyProvider keys, IJournalTimeAuthority time, string directory, UnityStorageDependencies dependencies)
        {
            Logging = logging;
            Capture = capture;
            Storage = storage;
            StorageKeys = keys;
            Time = time;
            Directory = directory;
            fileSystem = dependencies?.FileSystem;
            leases = dependencies?.Leases;
        }
        internal bool MatchesStorage(StorageConfiguration other)
        {
            return ReferenceEquals(StorageKeys, other.StorageKeys) && ReferenceEquals(Time, other.Time)
                && ReferenceEquals(fileSystem, other.fileSystem) && ReferenceEquals(leases, other.leases)
                && Directory == other.Directory && Storage.MaxDailyFileBytes == other.Storage.MaxDailyFileBytes
                && Storage.ErrorFatalReservedBytesPerDay == other.Storage.ErrorFatalReservedBytesPerDay
                && Storage.EnableMappedSealedBuffer == other.Storage.EnableMappedSealedBuffer
                && Storage.RetentionDays == other.Storage.RetentionDays && Storage.MaxDirectoryBytes == other.Storage.MaxDirectoryBytes
                && StartupSettings.Match(Logging, Capture, other.Logging, other.Capture);
        }
    }
}
