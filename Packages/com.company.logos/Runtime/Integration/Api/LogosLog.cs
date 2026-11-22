using System;
using System.Threading;
namespace Logos
{
    public static class LogosLog
    {
        private static LoggingPipeline bound;
        /// <summary>仅在显式请求时分配自有观察快照；日志调用不分配该快照。</summary>
        public static LoggingHealthSnapshot GetHealthSnapshot() => LoggingHealthSnapshot.Capture(Volatile.Read(ref bound));
        // 只有 Unity 生命周期所有者可以绑定；公共日志调用不会启动工作线程或订阅事件。
        // 公开入口通过绑定的流水线接受业务日志；会话卸载时必须核对绑定对象，以免迟到的清理移除新会话。
        internal static bool Bind(LoggingPipeline pipeline)
        {
            if (pipeline == null || pipeline.Completion != PipelineCompletion.Running)
                return false;
            var previous = Interlocked.CompareExchange(ref bound, pipeline, null);
            return previous == null || ReferenceEquals(previous, pipeline);
        }
        internal static bool UnbindIfMatches(LoggingPipeline pipeline)
            => pipeline != null && ReferenceEquals(Interlocked.CompareExchange(ref bound, null, pipeline), pipeline);
        public static bool IsEnabled(LogLevel level, LogSource source = LogSource.Application)
        {
            try
            {
                return Volatile.Read(ref bound)?.IsEnabled(level, source) ?? false;
            }
            catch (Exception)
            {
                return false;
            }
        }
        /// <summary>Accepted 表示队列提交。未绑定时返回 Stopping；不隐式初始化。</summary>
        public static AdmissionResult TryLog(LogLevel level, string message, string stack = null, string eventTypeId = null, LogSource source = LogSource.Application)
            => TryLogCore(level, message, stack, eventTypeId, source, false);
        /// <summary>显式标记需要重建现场的业务步骤；事件类型不能为空，仍受普通总预算限制。</summary>
        public static AdmissionResult KeyInfo(string message, string eventTypeId)
            => TryLogCore(LogLevel.Info, message, null, eventTypeId, LogSource.Application, true);

        private static AdmissionResult TryLogCore(LogLevel level, string message, string stack, string eventTypeId, LogSource source, bool keyInfo)
        {
            try
            {
                var pipeline = Volatile.Read(ref bound);
                return pipeline == null ? new AdmissionResult(AdmissionReason.Stopping)
                    : pipeline.TryAccept(level, source, message, stack, eventTypeId, pipeline.Generation, keyInfo: keyInfo);
            }
            catch (Exception)
            {
                return new AdmissionResult(AdmissionReason.ConstructionFailed);
            }
        }
        public static AdmissionResult TryLogDiagnostic(LogLevel level, string eventTypeId, DiagnosticRecordMetadata metadata, string message = null, string stack = null, LogSource source = LogSource.Application)
        {
            try
            {
                var pipeline = Volatile.Read(ref bound);
                return pipeline == null ? new AdmissionResult(AdmissionReason.Stopping) : pipeline.TryAcceptDiagnostic(level, source, message, metadata, stack, eventTypeId, pipeline.Generation);
            }
            catch (Exception)
            {
                return new AdmissionResult(AdmissionReason.ConstructionFailed);
            }
        }
        public static AdmissionResult Trace(string message, string eventTypeId = null) => TryLog(LogLevel.Trace, message, eventTypeId: eventTypeId);
        public static AdmissionResult Debug(string message, string eventTypeId = null) => TryLog(LogLevel.Debug, message, eventTypeId: eventTypeId);
        public static AdmissionResult Info(string message, string eventTypeId = null) => TryLog(LogLevel.Info, message, eventTypeId: eventTypeId);
        public static AdmissionResult Warn(string message, string eventTypeId = null) => TryLog(LogLevel.Warn, message, eventTypeId: eventTypeId);
        public static AdmissionResult Error(string message, string eventTypeId = null) => TryLog(LogLevel.Error, message, eventTypeId: eventTypeId);
        public static AdmissionResult Fatal(string message, string eventTypeId = null) => TryLog(LogLevel.Fatal, message, eventTypeId: eventTypeId);
        public static AdmissionResult Exception(string message, string existingStack, string eventTypeId = null) => TryLog(LogLevel.Error, message, existingStack, eventTypeId);
    }
    /// <summary>固定只读计数。实时观察不是跨字段事务；已消费不等于已持久化。</summary>
    public sealed class LoggingHealthSnapshot
    {
        private readonly HealthSnapshot snapshot;
        private LoggingHealthSnapshot(HealthSnapshot snapshot)
        {
            this.snapshot = snapshot;
        }
        // 持有已复制的 Core 快照。也支持生命周期所有者最终已解除绑定的流水线。
        internal static LoggingHealthSnapshot Capture(LoggingPipeline pipeline)
        {
            try
            {
                return new LoggingHealthSnapshot(pipeline?.Snapshot());
            }
            catch (Exception)
            {
                return new LoggingHealthSnapshot(null);
            }
        }
        /// <summary>计数采集自流水线；不代表流水线正在运行或健康。</summary>
        public bool IsAvailable => snapshot != null;
        public long CoalescedUnityInfo => snapshot?.CoalescedUnityInfo ?? 0;
        public long RecoveredUnityInfoObservations => snapshot?.RecoveredUnityInfoObservations ?? 0;
        public long UnityRepeatSummaryAttempts => snapshot?.UnityRepeatSummaryAttempts ?? 0;
        public long Attempted => Total(HealthMetric.Attempted);
        public long Filtered => Total(HealthMetric.Filtered);
        public long RejectedBeforeAccept => Total(HealthMetric.RejectedBeforeAccept);
        public long Accepted => Total(HealthMetric.Accepted);
        public long Queued => Total(HealthMetric.Queued);
        public long InFlight => Total(HealthMetric.InFlight);
        public long WorkerInFlight => Total(HealthMetric.WorkerInFlight);
        public long StorageInFlight => Total(HealthMetric.StorageInFlight);
        public long CommittedRecords => Total(HealthMetric.CommittedRecords);
        public long RepresentedRecords => Total(HealthMetric.RepresentedRecords);
        public PipelineCompletionKind CompletionKind => snapshot?.CompletionKind ?? PipelineCompletionKind.SynchronousConsumedForTest;
        public long ConsumedForTest => Total(HealthMetric.ConsumedForTest);
        public long DroppedAfterAccept => Total(HealthMetric.DroppedAfterAccept);
        public long PendingAdmission => Total(HealthMetric.PendingAdmission);
        public long GetAttempted(LogLevel level) => Get(HealthMetric.Attempted, level);
        public long GetFiltered(LogLevel level) => Get(HealthMetric.Filtered, level, AdmissionReason.Filtered);
        public long GetRejectedBeforeAccept(LogLevel level, AdmissionReason reason) => Get(HealthMetric.RejectedBeforeAccept, level, reason);
        public long GetAccepted(LogLevel level) => Get(HealthMetric.Accepted, level);
        public long GetQueued(LogLevel level) => Get(HealthMetric.Queued, level);
        public long GetInFlight(LogLevel level) => Get(HealthMetric.InFlight, level);
        public long GetWorkerInFlight(LogLevel level) => Get(HealthMetric.WorkerInFlight, level);
        public long GetStorageInFlight(LogLevel level) => Get(HealthMetric.StorageInFlight, level);
        public long GetCommittedRecords(LogLevel level) => Get(HealthMetric.CommittedRecords, level);
        public long GetRepresentedRecords(LogLevel level) => Get(HealthMetric.RepresentedRecords, level);
        public long GetConsumedForTest(LogLevel level) => Get(HealthMetric.ConsumedForTest, level);
        public long GetDroppedAfterAccept(LogLevel level, AdmissionReason reason) => Get(HealthMetric.DroppedAfterAccept, level, reason);
        public long GetPendingAdmission(LogLevel level) => Get(HealthMetric.PendingAdmission, level);
        private long Total(HealthMetric metric) => snapshot?.Total(metric) ?? 0;
        // Off 或未定义维度没有计数器；观察查询安全且不分配。
        private long Get(HealthMetric metric, LogLevel level, AdmissionReason reason = AdmissionReason.InvalidInput)
            => snapshot != null && level >= LogLevel.Trace && level <= LogLevel.Fatal && (int)reason >= 0 && (int)reason < HealthCounters.ReasonCount
                ? snapshot.Get(metric, level, reason) : 0;
    }
}
