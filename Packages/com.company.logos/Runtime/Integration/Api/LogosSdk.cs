using System;
using Logos.Storage;
using Logos.Transport;

namespace Logos
{
    public enum LoggingInitializationStatus
    {
        Installed, AlreadyInitialized, WrongThread, InvalidSettings, ConfigurationConflict, Stopping, UnsupportedPlatform, InitializationFailed
    }

    public interface ILogosSession
    {
        LogosSession UnitySession { get; }
    }

    /// <summary>在 Unity 主线程按需启动一套日志会话。</summary>
    public static class LogosSdk
    {
        public static LogosSession Current => LoggingBootstrap.Current;
        public static bool IsSafeToUnload => TransportBootstrap.IsSafeToUnload;

        public static bool TryInitialize(LogosSettings settings, ILogBatchProcessor processor, out LogosSession session)
            => LoggingBootstrap.TryInitialize(settings, processor, out session);

        public static bool TryInitialize(LogosSettings settings, ILogBatchProcessor processor,
            out LogosSession session, out LoggingInitializationStatus status)
            => LoggingBootstrap.TryInitialize(settings, processor, out session, out status);

        /// <summary>主线程按帧调用。只请求停止传入的会话，本地与全局均已释放才允许卸载。</summary>
        public static bool TryStopForUnload(ILogosSession session, TimeSpan budget, bool dropPending = false)
        {
            if (!LoggingBootstrap.IsMainThread || budget < TimeSpan.Zero)
                return false;
            // 停止请求归属于传入句柄。旧句柄释放后仍需核对全局状态，因为另一会话可能已接管资源。
            var owner = session?.UnitySession;
            if (owner != null && !owner.IsSafeToUnload)
            {
                if (owner.NeedsStopPolling)
                    owner.Poll();
                else
                    owner.RequestDetach(budget, dropPending);
            }
            return (owner == null || owner.IsSafeToUnload) && IsSafeToUnload;
        }

        public static bool TryInitialize(LogosSettings logging, StorageSettings storage,
            IStorageKeyProvider keys, IJournalTimeAuthority time,
            out StorageLoggingSession session, out StorageInitializationStatus status)
            => StorageBootstrap.TryInitialize(logging, storage, keys, time, out session, out status);

        public static bool TryInitialize(LogosSettings logging, StorageSettings storage,
            IStorageKeyProvider storageKeys, IJournalTimeAuthority time, LogosUploadOptions upload,
            out TransportLoggingSession session, out TransportInitializationStatus status)
            => TransportBootstrap.TryInitialize(logging, storage, upload?.Settings, storageKeys, upload?.TransferKeys, time,
                upload?.Service, upload?.InstallationId, upload?.DestinationId, out session, out status);

        public static bool TryInitialize(LogosSettings logging, StorageSettings storage, TransportSettings transport,
            IStorageKeyProvider storageKeys, IStorageKeyProvider transferKeys, IJournalTimeAuthority time,
            IUploadService upload, string installationId, string destinationId,
            out TransportLoggingSession session, out TransportInitializationStatus status)
            => TransportBootstrap.TryInitialize(logging, storage, transport, storageKeys, transferKeys, time,
                upload, installationId, destinationId, out session, out status);
    }
}
