using System;
using System.Threading;
namespace Logos
{
    public enum UserUpdateResult
    {
        Updated, Unchanged, Conflict, InvalidInput, StaleSession, WrongThread, Stopping
    }

    public sealed class LogContextHandle
    {
        internal readonly LogContext Snapshot;
        internal LogContextHandle(LogContext snapshot) { Snapshot = snapshot; }
    }

    public enum LogosSessionState
    {
        Running, Stopping, StopTimedOut, Stopped, Faulted
    }

    /// <summary>拥有显式业务日志流水线。生命周期操作在捕获的 Unity 主线程执行；
    /// TryLog 和健康查询可在任意线程执行。在 IsSafeToUnload 前必须保留此所有者。</summary>
    public sealed partial class LogosSession : ILogosSession
    {
        LogosSession ILogosSession.UnitySession => this;
        private readonly LoggingPipeline pipeline;
        private readonly LoggingOptions initialOptions;
        private readonly PolicySnapshot initialPolicy;
        private readonly ILogBatchProcessor processor;
        private readonly ILogAppLifecycleReceiver lifecycleReceiver;
        private readonly int mainThreadId;
        private bool detachRequested, disposed;
        // 旧访问接口仅在宿主首次采用 UpdateUser 前可推进匿名身份版本。
        private bool userUpdatesOwnVersion;
        private bool? hostForeground;
        public string UnityInstanceId => pipelineContextId;
        private readonly string pipelineContextId;
        public long Generation => pipeline.Generation;
        public LogContext Context => pipeline.ReadControlState().Context;
        public long ContextRevision => pipeline.ReadControlState().ContextRevision;
        public bool UnityIsPaused
        {
            get;
            private set;
        }
        public bool UnityHasFocus
        {
            get;
            private set;
        }
        = true;
        public bool IsForeground => hostForeground ?? !UnityIsPaused;
        public bool IsSafeToUnload => Volatile.Read(ref disposed);
        public LogosSessionState State
        {
            get
            {
                if (IsSafeToUnload)
                    return LogosSessionState.Stopped;
                switch (pipeline.Completion)
                {
                    case PipelineCompletion.StopTimedOut:
                        return LogosSessionState.StopTimedOut;
                    case PipelineCompletion.Faulted:
                        return LogosSessionState.Faulted;
                    case PipelineCompletion.Running:
                        return LogosSessionState.Running;
                    default:
                        return LogosSessionState.Stopping;
                }
            }
        }
        internal bool NeedsStopPolling => detachRequested && !IsSafeToUnload;
        private bool OnMainThread => Thread.CurrentThread.ManagedThreadId == mainThreadId;
        internal LogosSession(LoggingOptions options, PolicySnapshot policy, ILogBatchProcessor processor, int mainThreadId, long generation, IMonotonicClock clock = null)
        {
            initialOptions = options;
            initialPolicy = policy;
            this.mainThreadId = mainThreadId;
            this.processor = processor;
            lifecycleReceiver = processor as ILogAppLifecycleReceiver;
            var device = OnMainThread ? CaptureDevice() : null;
            var context = new LogContext(null, Guid.NewGuid().ToString("N"), null, 0, device: device);
            pipelineContextId = context.UnityInstanceId;
            pipeline = new LoggingPipeline(options, policy, context, processor, clock, generation: generation, explicitWritesOnly: true);
            if (options.Diagnostics.Enabled && OnMainThread)
                CaptureDiagnosticVersion();
        }
        internal static LogDeviceSnapshot CaptureDevice(Func<string> platform = null, Func<string> model = null,
            Func<string> os = null, Func<string> app = null, Func<string> unity = null)
            => new LogDeviceSnapshot(ReadDeviceField(platform ?? (() => UnityEngine.Application.platform.ToString())),
                ReadDeviceField(model ?? (() => UnityEngine.SystemInfo.deviceModel)),
                ReadDeviceField(os ?? (() => UnityEngine.SystemInfo.operatingSystem)),
                ReadDeviceField(app ?? (() => UnityEngine.Application.version)),
                ReadDeviceField(unity ?? (() => UnityEngine.Application.unityVersion)));

        private static string ReadDeviceField(Func<string> read)
        {
            try { return read(); }
            catch (Exception) { return null; }
        }

        internal bool TryGetControlTimestamp(string instanceId, long generation, out long now)
        {
            now = 0;
            return string.Equals(instanceId, UnityInstanceId, StringComparison.Ordinal) && pipeline.TryGetControlTimestamp(generation, out now);
        }
        internal void NotifyControl() => pipeline.NotifyControl();
        internal bool MatchesStartup(LoggingOptions options, PolicySnapshot policy, ILogBatchProcessor candidate)
            => UsesProcessor(candidate) && StartupSettings.Match(initialOptions, initialPolicy, options, policy);
        internal bool UsesProcessor(ILogBatchProcessor value) => ReferenceEquals(processor, value);
        internal bool Start()
        {
            if (!OnMainThread || !pipeline.Start())
                return false;
            if (LogosLog.Bind(pipeline))
                return true;
            RequestDetach(TimeSpan.Zero, true);
            return false;
        }
        private bool Matches(string instanceId, long generation)
            => OnMainThread && !detachRequested && pipeline.Completion == PipelineCompletion.Running && generation == Generation
                && string.Equals(instanceId, UnityInstanceId, StringComparison.Ordinal);
        public bool TryAttach(string instanceId, long generation) => Matches(instanceId, generation);
        /// <summary>乐观版本检查拒绝迟到的上下文更新，包括同一代际中的过期访问。</summary>
        public bool TryUpdateContext(string instanceId, long generation, long expectedContextRevision, LogContext next)
        {
            var current = Context;
            if (!Matches(instanceId, generation) || expectedContextRevision != ContextRevision || next == null
                || next.UnityInstanceId != UnityInstanceId || next.IdentityContextVersion < current.IdentityContextVersion
                || (userUpdatesOwnVersion && next.IdentityContextVersion != current.IdentityContextVersion)
                || (next.DiagnosticUserId != null && next.DiagnosticUserId != current.DiagnosticUserId))
                return false;
            return pipeline.ReplaceContext(new LogContext(next.AppSessionId, UnityInstanceId, next.UnityVisitId,
                next.IdentityContextVersion, current.DiagnosticUserId, current.Device));
        }

        /// <summary>仅接收后台映射的诊断标识。显式传 null 表示登出。</summary>
        public UserUpdateResult UpdateUser(long generation, long expectedIdentityVersion, string diagnosticUserId, out long identityVersion)
        {
            var current = Context;
            identityVersion = current.IdentityContextVersion;
            if (!OnMainThread) return UserUpdateResult.WrongThread;
            if (generation != Generation) return UserUpdateResult.StaleSession;
            if (!Matches(UnityInstanceId, generation)) return UserUpdateResult.Stopping;
            if (identityVersion != expectedIdentityVersion) return UserUpdateResult.Conflict;
            // 即使首次显式设置的身份与当前值相同，也要把版本管理交给 UpdateUser，防止旧访问接口以后推进身份版本。
            if (diagnosticUserId == current.DiagnosticUserId)
            {
                userUpdatesOwnVersion = true;
                return UserUpdateResult.Unchanged;
            }
            if (identityVersion == long.MaxValue) return UserUpdateResult.Conflict;
            LogContext next;
            try
            {
                next = new LogContext(current.AppSessionId, UnityInstanceId, current.UnityVisitId, identityVersion + 1, diagnosticUserId, current.Device);
            }
            catch (ArgumentException) { return UserUpdateResult.InvalidInput; }
            if (!pipeline.ReplaceContext(next)) return UserUpdateResult.Conflict;
            userUpdatesOwnVersion = true;
            identityVersion = next.IdentityContextVersion;
            return UserUpdateResult.Updated;
        }

        public LogContextHandle CaptureContext() => State == LogosSessionState.Running ? new LogContextHandle(Context) : null;

        public AdmissionResult TryLog(LogContextHandle handle, LogLevel level, string message, string stack = null,
            string eventTypeId = null, LogSource source = LogSource.Application)
        {
            if (handle == null || handle.Snapshot.UnityInstanceId != UnityInstanceId)
                return new AdmissionResult(AdmissionReason.StaleGeneration);
            return pipeline.TryAccept(level, source, message, stack, eventTypeId, Generation, capturedContext: handle.Snapshot);
        }

        public bool TryLeaveVisit(string instanceId, long generation, long expectedContextRevision)
        {
            if (!Matches(instanceId, generation) || expectedContextRevision != ContextRevision)
                return false;
            var current = Context;
            return TryUpdateContext(instanceId, generation, expectedContextRevision,
                new LogContext(current.AppSessionId, UnityInstanceId, null, current.IdentityContextVersion));
        }
        public bool TryUpdatePolicy(string instanceId, long generation, LogosSettings settings)
        {
            if (!Matches(instanceId, generation) || settings == null || !settings.TryCreateSnapshot(mainThreadId, out _, out var policy))
                return false;
            pipeline.ReplacePolicy(policy);
            return true;
        }
        /// <summary>宿主前后台状态具有权威性；重复值幂等。Unity 焦点仅记录为事实。</summary>
        public bool TryNotifyHostForeground(string instanceId, long generation, bool foreground)
        {
            if (!Matches(instanceId, generation))
                return false;
            bool previous = IsForeground;
            hostForeground = foreground;
            NotifyEffectiveTransition(previous);
            return true;
        }
        internal void NotifyUnityPause(bool paused)
        {
            if (!OnMainThread || detachRequested)
                return;
            bool previous = IsForeground;
            UnityIsPaused = paused;
            NotifyEffectiveTransition(previous);
        }
        private void NotifyEffectiveTransition(bool previous)
        {
            if (previous == IsForeground || lifecycleReceiver == null)
                return;
            if (IsForeground)
                lifecycleReceiver.RequestControlCheck();
            else
            {
                lifecycleReceiver.RequestBackgroundSeal();
            }
            pipeline.NotifyControl();
        }
        internal void NotifyUnityFocus(bool focused)
        {
            if (OnMainThread && !detachRequested)
                UnityHasFocus = focused;
        }
        public AdmissionResult TryLogDiagnostic(LogLevel level, string eventTypeId, DiagnosticRecordMetadata metadata, string message = null, string stack = null, LogSource source = LogSource.Application)
            => pipeline.TryAcceptDiagnostic(level, source, message, metadata, stack, eventTypeId, Generation);
        public AdmissionResult TryLog(LogLevel level, string message, string stack = null, string eventTypeId = null, LogSource source = LogSource.Application)
            => pipeline.TryAccept(level, source, message, stack, eventTypeId, Generation);
        public LoggingHealthSnapshot GetHealthSnapshot() => LoggingHealthSnapshot.Capture(pipeline);
        /// <summary>先关闭准入。首次预算和模式生效。不等待，也不销毁活跃资源。</summary>
        public bool RequestDetach(TimeSpan budget, bool dropPending = false)
        {
            if (!OnMainThread || budget < TimeSpan.Zero)
                return false;
            pipeline.RequestStop(dropPending ? WorkerStopMode.DropPending : WorkerStopMode.DrainUntilDeadline, budget);
            detachRequested = true;
            LogosLog.UnbindIfMatches(pipeline);
            Poll();
            return true;
        }
        /// <summary>主线程非阻塞完成检查。仅有故障不能授权卸载。</summary>
        public LogosSessionState Poll()
        {
            if (!OnMainThread || !detachRequested || IsSafeToUnload)
                return State;
            // 等待已经进入流水线的显式调用归还资源，再公布可卸载状态。
            if (pipeline.IsSafeToUnload && pipeline.TryDispose())
                Volatile.Write(ref disposed, true);
            return State;
        }
    }
}
