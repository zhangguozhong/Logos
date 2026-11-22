using System;
using System.Globalization;
using System.Threading;

namespace Logos
{
    // 已提交队列记录的序号不是结果序号。存储层仅在确认某个错误块已写入后回传原触发记录的序号。
    // 仅内建持久化后端（或显式转发它的组合）能确认单条错误的原始序号。
    internal interface IIncidentInfoDurableProcessor : IBufferedLogBatchProcessor, ILogDiagnosticsReceiver { }

    internal interface IIncidentInfoCommitObserver
    {
        bool ObservesIncidentInfo { get; }
        bool AllowTruncatedIncidentPrefix { set; }
        void InvalidateIncidentQuota();
        void ResolveIncidentTrigger(long recordSequence, bool persisted);
    }

    internal sealed partial class LoggingPipeline : IIncidentInfoCommitObserver
    {
        private readonly IncidentInfoWindow incidentInfo;
        private readonly Func<IncidentInfoWindow.Entry, bool?> publishIncidentEntry;
        private long incidentInfoAttempts, incidentInfoTransferred;
        private bool allowTruncatedIncidentPrefix;
        bool IIncidentInfoCommitObserver.AllowTruncatedIncidentPrefix { set => allowTruncatedIncidentPrefix = value; }
        bool IIncidentInfoCommitObserver.ObservesIncidentInfo => incidentInfo != null;

        void IIncidentInfoCommitObserver.ResolveIncidentTrigger(long recordSequence, bool persisted)
            => incidentInfo?.ResolveTrigger(recordSequence, persisted);

        void IIncidentInfoCommitObserver.InvalidateIncidentQuota()
        {
            queue.PrecheckAdmission(LogLevel.Info, out var closedTag);
            incidentInfo?.InvalidateQuota(closedTag);
        }

        private void DrainIncidentEntries(long now)
        {
            var current = ReadControlState();
            var gate = queue.PrecheckAdmission(LogLevel.Info, out var tag);
            incidentInfo.Drain(now, current.LatestIncidentObservation, current,
                gate == AdmissionReason.Accepted ? tag : null, publishIncidentEntry, maxAttempts: 1);
        }

        // 此委托只在 worker 的窗口借用期内执行。控制代际变更先作废窗口，等待当前移交和擦除后才返回。
        private bool? PublishIncidentEntry(IncidentInfoWindow.Entry entry)
        {
            Interlocked.Increment(ref incidentInfoAttempts);
            if (!ReferenceEquals(entry.Control, ReadControlState())
                || queue.PrecheckAdmission(LogLevel.Info, out var tag) != AdmissionReason.Accepted
                || !tag.HasValue || !Nullable.Equals(tag, entry.Tag))
                return false;
            // 需要尾部才能识别的自定义脱敏规则无法安全处理前缀。
            if (!allowTruncatedIncidentPrefix && (entry.Truncated || entry.Length > options.MaxMessageChars))
                return false;
            int cost = RecordMemoryCost.Estimate(Math.Min(entry.Length, options.MaxMessageChars), 0, null);
            if (cost > options.EffectiveRecordByteLimit)
                return false;
            if (!worker.CanTransferIncidentNow)
                return null;
            var rate = rateLimiter.TryAcquire(LogLevel.Info, LogSource.Unity, LoggingOptions.RatePayloadBytes(cost));
            if (rate != RateLimitDecision.Granted)
                return null;
            string message = BoundedCopy(entry.Message, options.MaxMessageChars, out bool truncated);
            long number = Interlocked.Increment(ref sequence);
            if (number <= 0) return false;
            string eventId = "logos.incident." + entry.TriggerSequence.ToString(CultureInfo.InvariantCulture) + "."
                + (entry.Observation - entry.TriggerObservation).ToString(CultureInfo.InvariantCulture);
            var record = new LogRecord(LogLevel.Info, "Logos.Incident", eventId, recordPrefix, number, entry.Utc,
                entry.Ticks, message, "", entry.Control.Context, truncated || entry.Truncated, false, tag, true);
            BatchTransferResult transfer = worker.TransferIncidentRecord(record);
            if (transfer.TransferredPrefix == 0)
                return transfer.NextRecordFailed ? (bool?)false : null;
            // 只登记本次真正移交产生的派生记录。原观测的拒绝和重试次数独立统计。
            counters.Add(HealthMetric.Attempted, LogLevel.Info);
            counters.Add(HealthMetric.Accepted, LogLevel.Info);
            Interlocked.Increment(ref incidentInfoTransferred);
            return true;
        }
    }
}
