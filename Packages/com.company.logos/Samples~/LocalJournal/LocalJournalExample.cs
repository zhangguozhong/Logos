using System;
using UnityEngine;

namespace Logos.Storage.Samples
{
    /// <summary>显式的有界 Journal 组合。应用提供自己的密钥和时间 provider。</summary>
    public sealed class LocalJournalExample : MonoBehaviour
    {
        [SerializeField] private LogosSettings loggingSettings;
        [SerializeField] private StorageSettings storageSettings;
        private LogosSettings ownedLoggingSettings;
        private StorageSettings ownedStorageSettings;
        private StorageLoggingSession session;
        public StorageInitializationStatus LastInitializationStatus
        {
            get;
            private set;
        }

        // 应用 provider 已缓存材料或证据后，在 Unity 主线程调用。
        // Start/OnEnable 不启动任何内容；此示例不产生日志，也不运行压力循环。
        public bool Initialize(IStorageKeyProvider appKeys, IJournalTimeAuthority appTime)
        {
            if (loggingSettings == null)
                loggingSettings = ownedLoggingSettings = ScriptableObject.CreateInstance<LogosSettings>();
            if (storageSettings == null)
                storageSettings = ownedStorageSettings = ScriptableObject.CreateInstance<StorageSettings>();
            // 返回的会话关联当前初始化请求。宿主后续用它查询存储恢复进度并轮询停止。
            bool initialized = LogosSdk.TryInitialize(loggingSettings, storageSettings, appKeys, appTime, out var owner, out var status);
            LastInitializationStatus = status;
            // 即使安装失败，也可能返回已启动工作线程的保留所有者。
            if (owner != null)
                session = owner;
            return initialized;
        }
        public StorageHealthSnapshot? QueryStorageHealth() => session?.GetHealthSnapshot();
        public LogosSession UnitySession => session?.UnitySession;
        public bool TryStopForUnload() => LogosSdk.TryStopForUnload(session, TimeSpan.FromMilliseconds(100));
        public void RequestStop() => TryStopForUnload();
        public void PollStop() => session?.UnitySession.Poll();
        public bool IsSafeToUnload => (session == null || session.UnitySession.IsSafeToUnload) && LogosSdk.IsSafeToUnload;
        private void OnDisable() => RequestStop();
        private void OnDestroy()
        {
            RequestStop(); // 原持久 SDK 运行器继续轮询保留的所有者。
            if (ownedLoggingSettings != null)
                Destroy(ownedLoggingSettings);
            if (ownedStorageSettings != null)
                Destroy(ownedStorageSettings);
        }
    }
}
