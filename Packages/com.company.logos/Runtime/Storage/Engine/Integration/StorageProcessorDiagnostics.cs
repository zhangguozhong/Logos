using System;
namespace Logos.Storage
{
    // 诊断源给出累计值，游标只合并相对上次观察的增量，避免周期性采样把同一批丢弃重复记账。
    public sealed partial class StorageBatchProcessor
    {
        private ILogDiagnosticsSource diagnostics;
        private readonly long[] statisticsCursor = new long[32 * 6];
        private long nextStatistics;
        public void BindDiagnostics(ILogDiagnosticsSource source)
        {
            if (ownerThread != 0 || diagnostics != null)
                throw new InvalidOperationException("Bind diagnostics once before worker start.");
            diagnostics = source ?? throw new ArgumentNullException(nameof(source));
            diagnosticBridge = source as IDiagnosticControlBridge;
        }
        private void MergeDiagnostics(long now)
        {
            if (diagnostics == null || now < nextStatistics)
                return;
            nextStatistics = Add(now, StorageRuntimeLimits.MaintenanceIntervalTicks);
            for (int reason = 0; reason < 32; reason++)
                for (int level = 0; level < 6; level++)
                {
                    int index = reason * 6 + level;
                    long current = diagnostics.GetCumulativeDiscarded((LogLevel)level, (AdmissionReason)reason);
                    long previous = statisticsCursor[index];
                    if (current > previous)
                        journal.DropStatistics.Record(reason, (LogLevel)level, current - previous);
                    if (current >= previous)
                        statisticsCursor[index] = current;
                }
        }
        /// <summary>由所有者工作线程显式分配诊断数据，绝不从周期控制路径调用。</summary>
        public long[] GetDropStatisticsSnapshot()
        {
            CheckWorker();
            return journal.DropStatistics.Snapshot();
        }
    }
}
