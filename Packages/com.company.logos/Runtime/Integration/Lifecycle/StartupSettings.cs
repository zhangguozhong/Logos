namespace Logos
{
    // 只在主线程重复初始化时比较启动快照；运行中的策略热更新独立生效。
    internal static class StartupSettings
    {
        internal static bool Match(LoggingOptions initial, PolicySnapshot capture, LoggingOptions requested, PolicySnapshot otherCapture)
        {
            if (capture.Enabled != otherCapture.Enabled || capture.MinimumLevel != otherCapture.MinimumLevel)
                return false;
            for (int i = 0; i < 4; i++)
            {
                var x = capture.GetSource((LogSource)i);
                var y = otherCapture.GetSource((LogSource)i);
                if (x.Enabled != y.Enabled || x.MinimumLevel != y.MinimumLevel)
                    return false;
            }
            // 比较配置内容而非对象地址，因此从同一资产重复生成的新快照也能匹配；诊断开关和各级预算都参与比较。
            var a = initial.Diagnostics;
            var b = requested.Diagnostics;
            if (a.Version != b.Version || a.Enabled != b.Enabled || a.AggregationEnabled != b.AggregationEnabled ||
                a.IncidentEnabled != b.IncidentEnabled || a.BudgetEnabled != b.BudgetEnabled)
                return false;
#if UNITY_EDITOR
            if (initial.LegacyRunWorkerForBenchmark != requested.LegacyRunWorkerForBenchmark ||
                initial.LegacyRepeatSummaryForBenchmark != requested.LegacyRepeatSummaryForBenchmark)
                return false;
#endif
            if (initial.IncidentInfoContext != requested.IncidentInfoContext || initial.ConsecutiveUnityInfo != requested.ConsecutiveUnityInfo
                || initial.NormalQueueCount != requested.NormalQueueCount || initial.ErrorQueueCount != requested.ErrorQueueCount
                || initial.NormalQueueBytes != requested.NormalQueueBytes || initial.ErrorQueueBytes != requested.ErrorQueueBytes
                || initial.MaxMessageChars != requested.MaxMessageChars || initial.MaxStackChars != requested.MaxStackChars
                || initial.BatchCount != requested.BatchCount || initial.BatchBytes != requested.BatchBytes
                || initial.WorkerBudgetTicks != requested.WorkerBudgetTicks || initial.WorkerWindowTicks != requested.WorkerWindowTicks
                || initial.WorkerWindowBudgetTicks != requested.WorkerWindowBudgetTicks || initial.RecordByteLimit != requested.RecordByteLimit
                || !Same(initial.NormalRate, requested.NormalRate) || !Same(initial.ErrorRate, requested.ErrorRate))
                return false;
            for (int i = 0; i < 4; i++)
                if (!Same(initial.SourceNormalBudgets[i], requested.SourceNormalBudgets[i]) || !Same(initial.SourceErrorBudgets[i], requested.SourceErrorBudgets[i]))
                    return false;
            for (int i = 0; i < 6; i++)
                if (!Same(initial.LevelBudgets[i], requested.LevelBudgets[i]))
                    return false;
            return true;
        }
        private static bool Same(RateBudget a, RateBudget b) => a.CountCapacity == b.CountCapacity && a.ByteCapacity == b.ByteCapacity && a.CountsPerSecond == b.CountsPerSecond && a.BytesPerSecond == b.BytesPerSecond;
    }
}
