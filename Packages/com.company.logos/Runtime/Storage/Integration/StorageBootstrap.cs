using System;
using System.IO;
using System.Threading;
using UnityEngine;
namespace Logos.Storage
{
    public enum StorageInitializationStatus
    {
        Installed, AlreadyInitialized, WrongThread, InvalidSettings, MissingKeyProvider, OtherOwner, Stopping, UnsupportedPlatform, InitializationFailed, ConfigurationConflict
    }
    public static class StorageBootstrap
    {
        // 两种启动模式共用此路径，且仅在 Unity 主线程解析。
        internal static string DefaultDirectory => Path.Combine(Application.persistentDataPath, "Logos");
        private static StorageLoggingSession current;
        /// <summary>当前存储所有者，包括安全释放前仍保留的启动失败所有者。</summary>
        public static StorageLoggingSession Current
        {
            get
            {
                var value = Volatile.Read(ref current);
                return value != null && (ReferenceEquals(value.UnitySession, LoggingBootstrap.Current) || !value.UnitySession.IsSafeToUnload) ? value : null;
            }
        }
        /// <summary>仅在主线程安装。成功表示已安装流水线，不表示恢复已就绪。
        /// provider 在唯一的 Core 工作线程上调用，必须使用快速、线程安全的缓存状态。</summary>
        public static bool TryInitialize(LogosSettings loggingSettings, StorageSettings storageSettings,
            IStorageKeyProvider keyProvider, IJournalTimeAuthority timeAuthority,
            out StorageLoggingSession storageSession, out StorageInitializationStatus status)
            => TryInitialize(loggingSettings, storageSettings, keyProvider, timeAuthority, null, out storageSession, out status);
        internal static bool TryInitialize(LogosSettings loggingSettings, StorageSettings storageSettings,
            IStorageKeyProvider keyProvider, IJournalTimeAuthority timeAuthority, UnityStorageDependencies dependencies,
            out StorageLoggingSession storageSession, out StorageInitializationStatus status)
        {
            storageSession = null;
            status = StorageInitializationStatus.WrongThread;
            // 不得在捕获的主线程之外访问 UnityEngine.Object 相等比较、序列化属性或 Application。
            if (!LoggingBootstrap.IsMainThread)
                return false;
            status = StorageInitializationStatus.InvalidSettings;
            if (loggingSettings == null || storageSettings == null
                || !loggingSettings.TryCreateSnapshot(LoggingBootstrap.MainThreadId, out var options, out var policy)
                || !storageSettings.TryCreateSnapshot(LoggingBootstrap.MainThreadId, out var retention))
                return false;
            if (keyProvider == null)
            {
                status = StorageInitializationStatus.MissingKeyProvider;
                return false;
            }
#if UNITY_WEBGL && !UNITY_EDITOR
            status = StorageInitializationStatus.UnsupportedPlatform;
            return false;
#else
            StorageConfiguration config = null;
            StorageBatchProcessor candidate = null;
            status = StorageInitializationStatus.InitializationFailed;
            try
            {
                string directory = dependencies == null ? DefaultDirectory : dependencies.Directory;
                if (string.IsNullOrWhiteSpace(directory))
                {
                    status = StorageInitializationStatus.InvalidSettings;
                    return false;
                }
                config = new StorageConfiguration(options, policy, retention, keyProvider, timeAuthority, directory, dependencies);
                // 复用已有工作线程前要同时核对 Unity 所有者和冻结配置，防止相同目录被另一组密钥或参数接管。
                var previous = LoggingBootstrap.Current;
                if (previous != null)
                {
                    previous.Poll();
                    if (!previous.IsSafeToUnload)
                    {
                        if (previous.State != LogosSessionState.Running)
                        {
                            status = StorageInitializationStatus.Stopping;
                            return false;
                        }
                        var retained = Volatile.Read(ref current);
                        if (retained == null || !retained.Owns(previous) || !retained.Configuration.MatchesStorage(config))
                        {
                            status = StorageInitializationStatus.ConfigurationConflict;
                            return false;
                        }
                        if (!previous.TryAttach(previous.UnityInstanceId, previous.Generation))
                        {
                            status = StorageInitializationStatus.Stopping;
                            return false;
                        }
                        storageSession = retained;
                        status = StorageInitializationStatus.AlreadyInitialized;
                        return true;
                    }
                }
                candidate = new StorageBatchProcessor(options, retention, directory, keyProvider, timeAuthority,
                    dependencies?.FileSystem, dependencies?.Leases);
                bool installed = LoggingBootstrap.TryInitializeFrozen(options, policy, candidate, out _, out _);
                var owner = LoggingBootstrap.Current;
                if (owner == null || !owner.UsesProcessor(candidate))
                    return false;
                // 即使 Start 失败也要发布：原所有者可能仍拥有活跃工作线程。
                storageSession = new StorageLoggingSession(owner, candidate, config);
                Volatile.Write(ref current, storageSession);
                if (installed)
                    status = StorageInitializationStatus.Installed;
                return installed;
            }
            catch (Exception)
            {
                var owner = LoggingBootstrap.Current;
                if (candidate != null && owner != null && owner.UsesProcessor(candidate))
                {
                    storageSession = new StorageLoggingSession(owner, candidate, config);
                    Volatile.Write(ref current, storageSession);
                }
                return false;
            }
            finally
            {
                // 从未发布所有者时，仅构造的候选不含 I/O 资源，也没有工作线程亲和要求。
                // 不得释放已进入发布状态的 Unity 所有者候选，包括 Start 失败的情况。
                if (candidate != null && !(LoggingBootstrap.Current?.UsesProcessor(candidate) ?? false))
                    candidate.Dispose();
            }
#endif
        }
    }
}
