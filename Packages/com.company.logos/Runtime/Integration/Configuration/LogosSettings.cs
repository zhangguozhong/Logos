using System;
using System.Threading;
using UnityEngine;
namespace Logos
{
    /// <summary>在所属 Unity 主线程上读取一次。工作线程只接收自有的托管值。</summary>
    [CreateAssetMenu(menuName = "Logos/Settings", fileName = "LogosSettings")]
    public sealed class LogosSettings : ScriptableObject
    {
        [SerializeField] private bool enabled = true;
        [SerializeField] private bool diagnosticsEnabled, diagnosticAggregationEnabled, diagnosticIncidentEnabled, diagnosticBudgetEnabled;
        [SerializeField] private int diagnosticVersion = 1;
        [SerializeField] private int minimumLevel = (int)LogLevel.Info;
        [SerializeField] private int normalQueueCount = 512, errorQueueCount = 128;
        [SerializeField] private int normalQueueBytes = 512 * 1024, errorQueueBytes = 128 * 1024;
        [SerializeField] private int maxMessageChars = 1024, maxStackChars = 4096, recordByteLimit;
        [SerializeField] private int batchCount = 64, batchBytes = 64 * 1024;
        [SerializeField] private int workerBudgetMilliseconds = 2, workerWindowMilliseconds = 100, workerWindowBudgetMilliseconds = 10;
        [SerializeField] private int normalCountCapacity = 100, normalByteCapacity = 64 * 1024, normalCountsPerSecond = 80, normalBytesPerSecond = 48 * 1024;
        [SerializeField] private int errorCountCapacity = 20, errorByteCapacity = 64 * 1024, errorCountsPerSecond = 20, errorBytesPerSecond = 16 * 1024;
        // 固定来源和等级子桶保留 LoggingOptions 的开发默认值。
        // Unity 资产只能在所属主线程读取。启动前将字段转换为托管配置和策略快照，后台工作不再访问这个资产。
        internal bool TryCreateSnapshot(int mainThreadId, out LoggingOptions options, out PolicySnapshot policy)
        {
            options = null;
            policy = null;
            // 访问任何序列化 Unity 资源字段前先检查。所有者提供捕获的主线程 ID。
            if (mainThreadId <= 0 || Thread.CurrentThread.ManagedThreadId != mainThreadId)
                return false;
            try
            {
                var values = new LoggingOptions
                {
                    Diagnostics = new DiagnosticOptions { Version = diagnosticVersion, Enabled = diagnosticsEnabled, AggregationEnabled = diagnosticAggregationEnabled, IncidentEnabled = diagnosticIncidentEnabled, BudgetEnabled = diagnosticBudgetEnabled },
                    NormalQueueCount = normalQueueCount,
                    ErrorQueueCount = errorQueueCount,
                    NormalQueueBytes = normalQueueBytes,
                    ErrorQueueBytes = errorQueueBytes,
                    MaxMessageChars = maxMessageChars,
                    MaxStackChars = maxStackChars,
                    RecordByteLimit = recordByteLimit,
                    BatchCount = batchCount,
                    BatchBytes = batchBytes,
                    WorkerBudgetTicks = workerBudgetMilliseconds * TimeSpan.TicksPerMillisecond,
                    WorkerWindowTicks = workerWindowMilliseconds * TimeSpan.TicksPerMillisecond,
                    WorkerWindowBudgetTicks = workerWindowBudgetMilliseconds * TimeSpan.TicksPerMillisecond,
                    NormalRate = new RateBudget(normalCountCapacity, normalByteCapacity, normalCountsPerSecond, normalBytesPerSecond),
                    ErrorRate = new RateBudget(errorCountCapacity, errorByteCapacity, errorCountsPerSecond, errorBytesPerSecond)
                };
                var rules = new PolicySnapshot(enabled, (LogLevel)minimumLevel);
                options = values.Freeze();
                policy = rules;
                return true;
            }
            catch (Exception)
            {
                options = null;
                policy = null;
                return false;
            }
        }
    }
}
