using System;
using System.Threading;
using UnityEngine;
namespace Logos
{
    /// <summary>组合根。只有在 Unity 主线程显式提供设置和处理器后才会启动。</summary>
    public static class LoggingBootstrap
    {
        private static int mainThreadId;
        private static long generation;
        private static LogosSession current;
        private static LoggingLifecycleRunner runner;
        public static LogosSession Current => Volatile.Read(ref current);
        /// <summary>全局所有者门禁，包括初始化失败的情况。卸载前在 Unity 主线程检查，
        /// 并同时检查任何保留的会话；仅 TryInitialize 调用失败不能授权销毁。</summary>
        public static bool IsSafeToUnload
        {
            get
            {
                var owner = Current;
                return owner == null || owner.IsSafeToUnload;
            }
        }
        private static bool OnMainThread => mainThreadId > 0 && Thread.CurrentThread.ManagedThreadId == mainThreadId;
        internal static bool IsMainThread => OnMainThread;
        internal static int MainThreadId => mainThreadId;
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        internal static void ResetForRuntime()
        {
            // Unity 在主线程调用此方法，禁用 Domain Reload 时也一样。
            // 只要任何工作线程或生产者可能仍存活，就不能清除旧所有者或代际计数器。
            mainThreadId = Thread.CurrentThread.ManagedThreadId;
            var previous = current;
            if (previous != null)
            {
                previous.RequestDetach(TimeSpan.FromMilliseconds(100), true);
                previous.Poll();
            }
            if (previous == null || previous.IsSafeToUnload)
                Volatile.Write(ref current, null);
#if UNITY_EDITOR
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload -= BeforeAssemblyReload;
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += BeforeAssemblyReload;
            UnityEditor.EditorApplication.playModeStateChanged -= PlayModeChanged;
            UnityEditor.EditorApplication.playModeStateChanged += PlayModeChanged;
#endif
        }
        public static bool TryInitialize(LogosSettings settings, ILogBatchProcessor processor, out LogosSession session)
            => TryInitialize(settings, processor, out session, out _);
        public static bool TryInitialize(LogosSettings settings, ILogBatchProcessor processor, out LogosSession session, out LoggingInitializationStatus status)
        {
            session = null;
            status = LoggingInitializationStatus.WrongThread;
            if (!OnMainThread)
                return false;
#if UNITY_WEBGL && !UNITY_EDITOR
            status = LoggingInitializationStatus.UnsupportedPlatform;
            return false; // 此目标不支持专用工作线程。
#else
            status = LoggingInitializationStatus.InvalidSettings;
            if (settings == null || processor == null || !settings.TryCreateSnapshot(mainThreadId, out var options, out var policy))
                return false;
            return TryInitializeFrozen(options, policy, processor, out session, out status);
#endif
        }
        // 友元组合根提供复制后的托管值，不向工作线程传递 Unity 资源。
        internal static bool TryInitializeFrozen(LoggingOptions options, PolicySnapshot policy, ILogBatchProcessor processor, out LogosSession session, out LoggingInitializationStatus status)
        {
            session = null;
            status = LoggingInitializationStatus.InitializationFailed;
            if (!OnMainThread || options == null || policy == null || processor == null)
                return false;
#if UNITY_WEBGL && !UNITY_EDITOR
            return false;
#else
            var previous = current;
            if (previous != null)
            {
                previous.Poll();
                if (!previous.IsSafeToUnload)
                {
                    status = LoggingInitializationStatus.Stopping;
                    if (previous.State != LogosSessionState.Running)
                        return false;
                    // 当前所有者仍活动时，仅允许相同处理器和等价启动配置重新附加；不兼容的请求不能替换正在工作的会话。
                    if (!previous.MatchesStartup(options, policy, processor))
                    {
                        status = LoggingInitializationStatus.ConfigurationConflict;
                        return false;
                    }
                    if (!previous.TryAttach(previous.UnityInstanceId, previous.Generation))
                        return false;
                    status = LoggingInitializationStatus.AlreadyInitialized;
                    session = previous;
                    return true;
                }
            }
            if (generation == long.MaxValue)
                return false;
            var next = new LogosSession(options, policy, processor, mainThreadId, ++generation);
            // 先发布再启动：即使初始化失败，也不能丢失仍存活的工作线程引用。
            Volatile.Write(ref current, next);
            if (!next.Start())
            {
                session = next;
                next.RequestDetach(TimeSpan.Zero, true);
                EnsureRunner();
                return false;
            }
            EnsureRunner();
            session = next;
            status = LoggingInitializationStatus.Installed;
            return true;
#endif
        }
        private static void EnsureRunner()
        {
            if (runner != null && ReferenceEquals(runner.Owner, current))
                return;
            var previous = runner;
            var owner = new GameObject("Logos Lifecycle");
            UnityEngine.Object.DontDestroyOnLoad(owner);
            runner = owner.AddComponent<LoggingLifecycleRunner>();
            runner.Initialize(current);
            // Destroy 会延后执行。其回调保留原所有者，而不是 Current。
            if (previous != null)
                UnityEngine.Object.Destroy(previous.gameObject);
        }
        internal static void RequestRuntimeStop()
        {
            if (OnMainThread)
                current?.RequestDetach(TimeSpan.FromMilliseconds(100), true);
        }
        internal static void RunnerDestroyed(LoggingLifecycleRunner value)
        {
            if (!OnMainThread)
                return;
            if (ReferenceEquals(runner, value))
                runner = null;
        }
#if UNITY_EDITOR
        private static void BeforeAssemblyReload() => RequestRuntimeStop();
        private static void PlayModeChanged(UnityEditor.PlayModeStateChange state)
        {
            if (state == UnityEditor.PlayModeStateChange.ExitingPlayMode)
                RequestRuntimeStop();
        }
#endif
    }
    // 此运行器只转发生命周期事实，并轮询待完成的停止；不使用计时器、任务或日志。
    internal sealed class LoggingLifecycleRunner : MonoBehaviour
    {
        internal LogosSession Owner
        {
            get;
            private set;
        }
        internal void Initialize(LogosSession owner)
        {
            if (Owner == null)
                Owner = owner;
        }
        private void Update()
        {
            Owner?.TickDiagnostics();
            if (Owner != null && Owner.NeedsStopPolling)
                Owner.Poll();
        }
        private void OnApplicationPause(bool paused) => Owner?.NotifyUnityPause(paused);
        private void OnApplicationFocus(bool focused) => Owner?.NotifyUnityFocus(focused);
        private void OnApplicationQuit() => Owner?.RequestDetach(TimeSpan.FromMilliseconds(100), true);
        private void OnDestroy()
        {
            Owner?.RequestDetach(TimeSpan.FromMilliseconds(100), true);
            LoggingBootstrap.RunnerDestroyed(this);
        }
    }
}
