using System;
using System.Threading;
using Logos.Storage;
namespace Logos.Transport
{
    public enum TransportInitializationStatus
    {
        Installed, AlreadyInitialized, WrongThread, InvalidSettings, MissingKeyProvider, MissingTimeAuthority, MissingUploadService, OtherOwner, ConfigurationConflict, Stopping, UnsupportedPlatform, InitializationFailed
    }
    public static class TransportBootstrap
    {
        private static TransportLoggingSession current;
        public static TransportLoggingSession Current
        {
            get
            {
                var value = Volatile.Read(ref current);
                return value != null && (ReferenceEquals(value.UnitySession, LoggingBootstrap.Current) || !value.UnitySession.IsSafeToUnload) ? value : null;
            }
        }
        public static bool IsSafeToUnload
        {
            get
            {
                var owner = Current;
                return LoggingBootstrap.IsSafeToUnload && (owner == null || owner.UnitySession.IsSafeToUnload);
            }
        }
        public static bool TryInitialize(LogosSettings loggingSettings, StorageSettings storageSettings, TransportSettings transportSettings, IStorageKeyProvider storageKeys, IStorageKeyProvider transferKeys, IJournalTimeAuthority timeAuthority, IUploadService uploadService, string installationId, string destinationId, out TransportLoggingSession session, out TransportInitializationStatus status)
         => TryInitialize(loggingSettings, storageSettings, transportSettings, storageKeys, transferKeys, timeAuthority, uploadService, installationId, destinationId, null, out session, out status);
        // 在读取 Unity 设置并发布会话前检查线程和依赖，同一所有者的重复初始化还需匹配原配置。
        internal static bool TryInitialize(LogosSettings loggingSettings, StorageSettings storageSettings, TransportSettings transportSettings, IStorageKeyProvider storageKeys, IStorageKeyProvider transferKeys, IJournalTimeAuthority timeAuthority, IUploadService uploadService, string installationId, string destinationId, UnityStorageDependencies dependencies, out TransportLoggingSession session, out TransportInitializationStatus status)
        {
            session = null;
            status = TransportInitializationStatus.WrongThread;
            if (!LoggingBootstrap.IsMainThread)
                return false;
            status = TransportInitializationStatus.InvalidSettings;
            if (loggingSettings == null || storageSettings == null || transportSettings == null || !loggingSettings.TryCreateSnapshot(LoggingBootstrap.MainThreadId, out var logging, out var capture) || !storageSettings.TryCreateSnapshot(LoggingBootstrap.MainThreadId, out var storage) || !transportSettings.TryCreateSnapshot(LoggingBootstrap.MainThreadId, out var transfer))
                return false;
            if (uploadService == null)
            {
                status = TransportInitializationStatus.MissingUploadService;
                return false;
            }
            if (storageKeys == null || transferKeys == null)
            {
                status = TransportInitializationStatus.MissingKeyProvider;
                return false;
            }
            if (timeAuthority == null)
            {
                status = TransportInitializationStatus.MissingTimeAuthority;
                return false;
            }
#if UNITY_WEBGL && !UNITY_EDITOR
   status=TransportInitializationStatus.UnsupportedPlatform;
            return false;
#else
            TransportConfiguration config;
            try
            {
                TransferValidation.Id(installationId, nameof(installationId));
                TransferValidation.Id(destinationId, nameof(destinationId));
                string serviceDestination;
                try
                {
                    serviceDestination = uploadService.DestinationId;
                }
                catch (Exception ex) when (!(ex is OutOfMemoryException))
                {
                    status = TransportInitializationStatus.InitializationFailed;
                    return false;
                }
                if (serviceDestination != destinationId)
                    return false;
                string directory = dependencies == null ? StorageBootstrap.DefaultDirectory : dependencies.Directory;
                if (string.IsNullOrWhiteSpace(directory))
                    return false;
                config = new TransportConfiguration(logging, capture, storage, transfer, storageKeys, transferKeys, timeAuthority, uploadService, installationId, destinationId, directory, dependencies);
            }
            catch (ArgumentException)
            {
                return false;
            }
            var previous = LoggingBootstrap.Current;
            if (previous != null)
            {
                previous.Poll();
                if (!previous.IsSafeToUnload)
                {
                    if (previous.State != LogosSessionState.Running)
                    {
                        status = TransportInitializationStatus.Stopping;
                        return false;
                    }
                    var retained = Volatile.Read(ref current);
                    if (retained == null || !retained.Owns(previous))
                    {
                        status = TransportInitializationStatus.ConfigurationConflict;
                        return false;
                    }
                    if (!retained.Configuration.Matches(config))
                    {
                        status = TransportInitializationStatus.ConfigurationConflict;
                        return false;
                    }
                    if (!previous.TryAttach(previous.UnityInstanceId, previous.Generation))
                    {
                        status = TransportInitializationStatus.Stopping;
                        return false;
                    }
                    session = retained;
                    status = TransportInitializationStatus.AlreadyInitialized;
                    return true;
                }
            }
            StorageBatchProcessor local = null;
            TransportBatchProcessor candidate = null;
            status = TransportInitializationStatus.InitializationFailed;
            try
            {
                local = new StorageBatchProcessor(logging, storage, config.Directory, storageKeys, timeAuthority, dependencies?.FileSystem, dependencies?.Leases);
                candidate = new TransportBatchProcessor(local, transferKeys, uploadService, transfer, installationId, destinationId);
                bool installed = LoggingBootstrap.TryInitializeFrozen(logging, capture, candidate, out _, out _);
                var owner = LoggingBootstrap.Current;
                if (owner == null || !owner.UsesProcessor(candidate))
                    return false;
                session = new TransportLoggingSession(owner, candidate, config);
                Volatile.Write(ref current, session);
                if (installed)
                    status = TransportInitializationStatus.Installed;
                return installed;
            }
            catch (Exception)
            {
                var owner = LoggingBootstrap.Current;
                if (candidate != null && owner != null && owner.UsesProcessor(candidate) && session == null)
                {
                    session = new TransportLoggingSession(owner, candidate, config);
                    Volatile.Write(ref current, session);
                }
                return false;
            }
            finally
            {
                if (candidate != null)
                {
                    if (!(LoggingBootstrap.Current?.UsesProcessor(candidate) ?? false))
                        candidate.Dispose();
                }
                else
                    local?.Dispose();
            }
#endif
        }
    }
}
