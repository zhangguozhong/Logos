using System;
namespace Logos
{
    public readonly struct RateBudget
    {
        public int CountCapacity
        {
            get;
        }
        public int ByteCapacity
        {
            get;
        }
        public int CountsPerSecond
        {
            get;
        }
        public int BytesPerSecond
        {
            get;
        }
        public RateBudget(int countCapacity, int byteCapacity, int countsPerSecond, int bytesPerSecond)
        {
            CountCapacity = countCapacity;
            ByteCapacity = byteCapacity;
            CountsPerSecond = countsPerSecond;
            BytesPerSecond = bytesPerSecond;
        }
    }
    public sealed class LoggingOptions
    {
        // 保守估算托管成本：记录、上下文、队列槽位、六个有界 ID 和字符串头。
        // 载荷按 UTF-16 计费，而非编码后的 UTF-8。这是预算估算，不是堆内存遥测。
        public const int FixedRecordBytes = 2048;
        /// <summary>默认仅合并连续重复的普通 Unity Info。关闭后使用跨段匹配策略。</summary>
        public bool ConsecutiveUnityInfo { get; set; } = true;
        /// <summary>显式开启故障窗口，在普通队列预算内暂存因入口限流未入队的短消息。</summary>
        public bool IncidentInfoContext { get; set; }
        internal const int IncidentReservedSlots = 40, IncidentReservedBytes = 32 * 1024;
        // 即使划出固定窗口，普通分区仍须可接收一个最大合法记录。
        internal bool EffectiveIncidentInfoContext => IncidentInfoContext && ConsecutiveUnityInfo
            && MaxMessageChars >= LoggingPipeline.MaxRunText
            && EffectiveRecordByteLimit >= FixedRecordBytes + 2 * LoggingPipeline.MaxRunText
            && NormalQueueCount > IncidentReservedSlots && NormalQueueBytes - IncidentReservedBytes >= EffectiveRecordByteLimit;
#if UNITY_EDITOR || !UNITY_5_3_OR_NEWER
        internal bool LegacyRepeatSummaryForBenchmark { get; set; }
        internal bool LegacyRunWorkerForBenchmark { get; set; }
#else
        internal bool LegacyRepeatSummaryForBenchmark => false;
        internal bool LegacyRunWorkerForBenchmark => false;
#endif
        public DiagnosticOptions Diagnostics
        {
            get;
            set;
        }
        = new DiagnosticOptions();
        public int NormalQueueCount
        {
            get;
            set;
        }
        = 512;
        public int ErrorQueueCount
        {
            get;
            set;
        }
        = 128;
        public int NormalQueueBytes
        {
            get;
            set;
        }
        = 512 * 1024;
        public int ErrorQueueBytes
        {
            get;
            set;
        }
        = 128 * 1024;
        public int MaxMessageChars
        {
            get;
            set;
        }
        = 1024;
        public int MaxStackChars
        {
            get;
            set;
        }
        = 4096;
        public int BatchCount
        {
            get;
            set;
        }
        = 64;
        public int BatchBytes
        {
            get;
            set;
        }
        = 64 * 1024;
        // 使用 TimeSpan tick（100 ns），与 IMonotonicClock 一致；不是原始 Stopwatch tick。
        public long WorkerBudgetTicks
        {
            get;
            set;
        }
        = TimeSpan.TicksPerMillisecond * 2;
        public long WorkerWindowTicks
        {
            get;
            set;
        }
        = TimeSpan.TicksPerMillisecond * 100;
        public long WorkerWindowBudgetTicks
        {
            get;
            set;
        }
        = TimeSpan.TicksPerMillisecond * 10;
        // 零表示采用完整的配置文本上限。显式设置更小的上限会导致 RecordTooLarge。
        public int RecordByteLimit
        {
            get;
            set;
        }
        public RateBudget NormalRate
        {
            get;
            set;
        }
        = new RateBudget(100, 64 * 1024, 80, 48 * 1024);
        public RateBudget ErrorRate
        {
            get;
            set;
        }
        = new RateBudget(20, 64 * 1024, 20, 16 * 1024);
        public RateBudget[] SourceNormalBudgets
        {
            get;
            set;
        }
        = CreateBudgets(4);
        public RateBudget[] SourceErrorBudgets
        {
            get;
            set;
        }
        = CreateBudgets(4);
        public RateBudget[] LevelBudgets
        {
            get;
            set;
        }
        = CreateBudgets(6);
        public int MaximumConfiguredRecordBytes => checked(FixedRecordBytes + 2 * checked(MaxMessageChars + MaxStackChars));
        public int EffectiveRecordByteLimit => RecordByteLimit == 0 ? MaximumConfiguredRecordBytes : Math.Min(RecordByteLimit, MaximumConfiguredRecordBytes);
        // 条数桶负责每条记录的固定开销，速率字节桶仅对有界文本和诊断载荷计费。
        internal static int RatePayloadBytes(int estimatedMemoryBytes) => Math.Max(1, estimatedMemoryBytes - FixedRecordBytes);

        private static RateBudget[] CreateBudgets(int size)
        {
            var budgets = new RateBudget[size];
            for (int i = 0; i < size; i++)
                budgets[i] = new RateBudget(100, 64 * 1024, 100, 64 * 1024);
            return budgets;
        }
        public void Validate()
        {
            if (Diagnostics == null)
                throw new ArgumentNullException(nameof(Diagnostics));
            Diagnostics.Validate();
            // 复制任何用户提供的集合前，先检查固定维度。
            if (SourceNormalBudgets == null || SourceNormalBudgets.Length != PolicySnapshot.SourceCount ||
                SourceErrorBudgets == null || SourceErrorBudgets.Length != PolicySnapshot.SourceCount ||
                LevelBudgets == null || LevelBudgets.Length != 6)
                throw new ArgumentException("Fixed source/level budget dimensions required.");
            if (MaxMessageChars < 0 || MaxMessageChars > LogRecord.MaxMessageLength)
                throw new ArgumentOutOfRangeException(nameof(MaxMessageChars));
            if (MaxStackChars < 0 || MaxStackChars > LogRecord.MaxStackLength)
                throw new ArgumentOutOfRangeException(nameof(MaxStackChars));
            if (NormalQueueCount < 1 || ErrorQueueCount < 1 || NormalQueueBytes < 1 || ErrorQueueBytes < 1 || BatchCount < 1 || BatchBytes < 1 || WorkerBudgetTicks < 1 || WorkerWindowTicks < 1 || WorkerWindowBudgetTicks < 1)
                throw new ArgumentOutOfRangeException("Queue, batch and work budgets must be positive.");
            if (WorkerBudgetTicks > WorkerWindowBudgetTicks || WorkerWindowBudgetTicks > WorkerWindowTicks)
                throw new ArgumentException("Worker batch budget must fit the work allowance, which must fit its window.");
            if (RecordByteLimit != 0 && RecordByteLimit < FixedRecordBytes)
                throw new ArgumentOutOfRangeException(nameof(RecordByteLimit));
            try
            {
                checked
                {
                    // 消费者必须能够表示队列与处理中批次的合计值。
                    int count = NormalQueueCount + ErrorQueueCount + BatchCount;
                    int bytes = NormalQueueBytes + ErrorQueueBytes + BatchBytes;
                    int rateCount = NormalRate.CountCapacity + ErrorRate.CountCapacity;
                    int rateBytes = NormalRate.ByteCapacity + ErrorRate.ByteCapacity;
                    int countRate = NormalRate.CountsPerSecond + ErrorRate.CountsPerSecond;
                    int byteRate = NormalRate.BytesPerSecond + ErrorRate.BytesPerSecond;
                }
            }
            catch (OverflowException e)
            {
                throw new ArgumentException("Combined budgets exceed supported integer range.", e);
            }
            // 同时检查生产和消费两侧：允许进入任一分区的最大记录也必须能单独组成一个批次，否则队首永远无法出队。
            int cost = EffectiveRecordByteLimit;
            if (cost > NormalQueueBytes || cost > ErrorQueueBytes || cost > BatchBytes)
                throw new ArgumentException("Maximum legal record must fit each queue partition and the batch byte budget.");
            int payloadLimit = RatePayloadBytes(cost);
            ValidateBudget(NormalRate, payloadLimit);
            ValidateBudget(ErrorRate, payloadLimit);
            for (int i = 0; i < PolicySnapshot.SourceCount; i++)
            {
                ValidateBudget(SourceNormalBudgets[i], payloadLimit);
                ValidateBudget(SourceErrorBudgets[i], payloadLimit);
            }
            for (int i = 0; i < 6; i++)
                ValidateBudget(LevelBudgets[i], payloadLimit);
        }
        private static void ValidateBudget(RateBudget budget, int cost)
        {
            if (budget.CountCapacity < 1 || budget.ByteCapacity < cost || budget.CountsPerSecond < 1 || budget.BytesPerSecond < 1)
                throw new ArgumentException("Every applicable rate bucket must hold one maximum legal payload and have positive refill rates.");
        }
        // 仅可在启动前配置。调用者不得在启动期间并发修改选项。
        internal LoggingOptions Freeze()
        {
            Validate();
            var copy = (LoggingOptions)MemberwiseClone();
            copy.SourceNormalBudgets = (RateBudget[])SourceNormalBudgets.Clone();
            copy.SourceErrorBudgets = (RateBudget[])SourceErrorBudgets.Clone();
            copy.LevelBudgets = (RateBudget[])LevelBudgets.Clone();
            copy.Diagnostics = Diagnostics.Freeze();
            return copy;
        }
    }
}
