using System;
using System.Threading;
namespace Logos
{
    internal enum PipelineCompletion
    {
        Created, Running, Stopping, StopTimedOut, Stopped, Faulted
    }
    // 托管准入门的寿命长于工作线程句柄，因此释放后仍可安全拒绝迟到的回调。
    // 持有任一元数据门锁时，都不执行处理器、委托或文本复制。
    internal sealed partial class LoggingPipeline : IDisposable, ILogDiagnosticsSource, IDiagnosticControlBridge
    {
        private readonly object lifecycle = new object();
        private readonly LoggingOptions options;
        private readonly AdmissionPolicy policy;
        private readonly RateLimiter rateLimiter;
        private readonly BoundedLogQueue queue;
        private readonly BackgroundLogWorker worker;
        private readonly IMonotonicClock clock;
        private readonly HealthCounters counters = new HealthCounters();
        private readonly string instanceId, recordPrefix;
        private PipelineCompletion state;
        private int producers;
        private readonly ProducerRegistry producerRegistry = new ProducerRegistry();
        private bool disposed, stopRequested;
        private long stopDeadline, sequence;
        internal long Generation
        {
            get;
        }
        internal LoggingPipeline(LoggingOptions options, PolicySnapshot policy, LogContext context, ILogBatchProcessor processor, IMonotonicClock clock = null, long generation = 1, bool explicitWritesOnly = false)
        {
            if (generation < 1)
                throw new ArgumentOutOfRangeException(nameof(generation));
            this.options = (options ?? throw new ArgumentNullException(nameof(options))).Freeze();
            // 自动 Info 摘要需要能容纳首条标识、次数和观测时间。如果文本或记录上限太小，保留普通采集路径，避免制造永远无法入队的摘要。
            repeatSummaryChars = this.options.ConsecutiveUnityInfo ? MaxRunText
                : this.options.LegacyRepeatSummaryForBenchmark ? LegacyRepeatText : MaxRepeatText;
            if (!explicitWritesOnly && (this.options.ConsecutiveUnityInfo || !this.options.Diagnostics.Enabled) && this.options.MaxMessageChars >= repeatSummaryChars
                && this.options.EffectiveRecordByteLimit >= LoggingOptions.FixedRecordBytes + 2 * repeatSummaryChars)
            {
                repeatSlots = new RepeatSlot[2 * RepeatSlotCount];
                for (int i = 0; i < repeatSlots.Length; i++)
                    repeatSlots[i] = this.options.ConsecutiveUnityInfo ? new RunSlot() : new RepeatSlot();
            }
            this.policy = new AdmissionPolicy(policy ?? throw new ArgumentNullException(nameof(policy)));
            if (context == null) throw new ArgumentNullException(nameof(context));
            instanceId = context.UnityInstanceId;
            Generation = generation;
            diagnosticControl = new DiagnosticControlState(generation, 0, 0, context, 0, null, this.options.Diagnostics.Enabled, this.options.Diagnostics.BudgetEnabled);
            if (this.options.EffectiveIncidentInfoContext && repeatSlots != null && processor is IIncidentInfoDurableProcessor)
            {
                incidentInfo = new IncidentInfoWindow(diagnosticControl);
                publishIncidentEntry = PublishIncidentEntry;
            }
            this.clock = clock ?? new StopwatchClock();
            recordPrefix = Guid.NewGuid().ToString("N") + "-";
            rateLimiter = new RateLimiter(this.options, this.clock);
            queue = new BoundedLogQueue(this.options, generation, incidentInfo != null);
            // 缺少处理器明确表示流水线尚未就绪；不创建线程或信号句柄。
            if (processor != null)
            {
                worker = new BackgroundLogWorker(queue, this.options, processor, this.clock) { Producers = producerRegistry };
                worker.IncidentWindow = incidentInfo;
                if (incidentInfo != null) worker.DrainIncident = DrainIncidentEntries;
                if (repeatSlots != null)
                {
                    worker.DrainRepeatTail = () => FlushUnityRepeats(true);
                    worker.DrainIdentityTails = DrainIdentityTails;
                    if (this.options.ConsecutiveUnityInfo) worker.DrainRunTails = () => FlushUnityRepeats(false);
                }
            }
            if (processor is ILogDiagnosticsReceiver receiver)
                receiver.BindDiagnostics(this);
        }
        public long GetCumulativeDiscarded(LogLevel level, AdmissionReason reason)
        {
            long total = counters.Read(HealthMetric.Filtered, level, reason) + counters.Read(HealthMetric.RejectedBeforeAccept, level, reason);
            total += queue.GetControlDropped(level, reason);
            if (worker != null)
                total += worker.GetBufferedDiscarded(level, reason);
            return total;
        }
        internal bool Start()
        {
            lock (lifecycle)
            {
                if (disposed || state != PipelineCompletion.Created || worker == null)
                    return false;
                try
                {
                    worker.Start();
                    state = PipelineCompletion.Running;
                    return true;
                }
                catch (Exception)
                {
                    queue.Close();
                    state = PipelineCompletion.Faulted;
                    return false;
                }
            }
        }
        internal void ReplacePolicy(PolicySnapshot snapshot)
        {
            lock (lifecycle)
            {
                policy.Replace(snapshot);
                UpdateDiagnosticControl(true, false, null, null);
            }
            // 频繁准入也使用生命周期门，等待后台擦除必须放在门外。
            incidentInfo?.Reset(ReadControlState());
            NotifyControl();
        }
        // 生命周期标记复用现有工作线程信号。生命周期门锁阻止释放后继续发信号。
        // 供受信任组合使用的同源 100 ns 时钟桥；不创建额外时钟或工作线程。
        internal bool TryGetControlTimestamp(long generation, out long now)
        {
            lock (lifecycle)
            {
                now = 0;
                ObserveFault();
                if (disposed || stopRequested || state != PipelineCompletion.Running || generation != Generation)
                    return false;
                now = clock.TimestampTicks;
                return now >= 0;
            }
        }
        internal void NotifyControl()
        {
            lock (lifecycle)
            {
                if (!disposed && state == PipelineCompletion.Running && !stopRequested)
                    worker?.NotifyControl();
            }
        }
        internal bool ReplaceContext(LogContext value)
        {
            if (value == null)
                throw new ArgumentNullException(nameof(value));
            if (!string.Equals(instanceId, value.UnityInstanceId, StringComparison.Ordinal))
                return false;
            lock (lifecycle)
            {
                if (disposed || stopRequested || diagnosticControl.RequestRevision == long.MaxValue || diagnosticControl.ContextRevision == long.MaxValue)
                    return false;
                bool changed = IdentityChanged(diagnosticControl.Context, value);
                UpdateDiagnosticControl(false, true, value, null);
                if (changed && repeatSlots != null) worker?.MarkIdentityTails();
            }
            // 频繁准入也使用生命周期门，等待后台擦除必须放在门外。
            incidentInfo?.Reset(ReadControlState());
            NotifyControl();
            return true;
        }
        internal bool IsEnabled(LogLevel level, LogSource source)
        {
            lock (lifecycle)
                return state == PipelineCompletion.Running && !worker.HasFaulted && policy.IsEnabled(level, source);
        }
        internal bool HasFaulted
        {
            get
            {
                lock (lifecycle)
                    return state == PipelineCompletion.Faulted || (worker != null && worker.HasFaulted);
            }
        }
        internal bool IsSafeToUnload
        {
            get
            {
                lock (lifecycle)
                {
                    ObserveFault();
                    return SafeToReleaseUnderGate();
                }
            }
        }
        private bool SafeToReleaseUnderGate()
            => state != PipelineCompletion.Running && producers == 0 && WorkerTerminated() && queue.Snapshot().ReservedCount == 0;
        internal PipelineCompletion Completion
        {
            get
            {
                lock (lifecycle)
                {
                    ObserveFault();
                    if (state == PipelineCompletion.Created || state == PipelineCompletion.Running)
                        return state;
                    if (SafeToReleaseUnderGate())
                        return state == PipelineCompletion.Faulted ? PipelineCompletion.Faulted : PipelineCompletion.Stopped;
                    if (stopRequested && clock.TimestampTicks >= stopDeadline)
                        return PipelineCompletion.StopTimedOut;
                    return PipelineCompletion.Stopping;
                }
            }
        }
        internal AdmissionResult TryAccept(LogLevel level, LogSource source, string message, string stack, string eventTypeId = null, long? expectedGeneration = null, DiagnosticRecordMetadata diagnosticMetadata = null, bool diagnosticCall = false, bool keyInfo = false, bool automaticInfo = false, LogContext capturedContext = null)
            => TryAcceptCore(level, source, message, stack, eventTypeId, expectedGeneration, diagnosticMetadata, diagnosticCall, keyInfo, automaticInfo, null, capturedContext: capturedContext);

        private AdmissionResult TryAcceptCore(LogLevel level, LogSource source, string message, string stack, string eventTypeId,
            long? expectedGeneration, DiagnosticRecordMetadata diagnosticMetadata, bool diagnosticCall, bool keyInfo, bool automaticInfo, RepeatSlot summary, bool terminal = false, LogContext capturedContext = null)
        {
            // 全部外部观测共用序号。任何拒绝或争用都会留下间隔，内部摘要不占号。
            bool externalRun = repeatSlots != null && options.ConsecutiveUnityInfo && summary == null;
            long observation = externalRun ? Interlocked.Increment(ref runObservation) : 0;
            long incidentObservation = 0;
            // 无效等级和 Off 使用固定诊断桶（Trace），不创建动态维度。
            LogLevel bucket = level >= LogLevel.Trace && level <= LogLevel.Fatal ? level : LogLevel.Trace;
            counters.Add(HealthMetric.Attempted, bucket);
            bool entered = false, reserved = false, coalesced = false;
            RepeatSlot repeatSlot = null;
            DiagnosticControlState controlView = null;
            QuotaDayTag? quotaTag = null;
            long recovered = 0;
            QueueReservation token = default;
            AdmissionResult result = new AdmissionResult(AdmissionReason.ConstructionFailed);
            try
            {
                lock (lifecycle)
                {
                    ObserveFault();
                    if (state != PipelineCompletion.Running && !(terminal && summary != null && state == PipelineCompletion.Stopping
                        && terminalRepeatControl != null && diagnosticControl.RequestRevision - 1 == terminalRepeatControl.RequestRevision
                        && clock.TimestampTicks < stopDeadline))
                        result = new AdmissionResult(state == PipelineCompletion.Faulted ? AdmissionReason.Faulted : AdmissionReason.Stopping);
                    else if (expectedGeneration.HasValue && expectedGeneration.Value != Generation)
                        result = new AdmissionResult(AdmissionReason.StaleGeneration);
                    else
                    {
                        if (incidentInfo != null && externalRun)
                        {
                            // 与切号使用同一把门。旧句柄不消耗新身份的位置，内部摘要不占号。
                            controlView = diagnosticControl;
                            if (capturedContext == null || ReferenceEquals(capturedContext, controlView.Context))
                                incidentObservation = controlView.NextIncidentObservation();
                        }
                        producers++;
                        producerRegistry.Enter();
                        entered = true;
                        counters.Add(HealthMetric.PendingAdmission, bucket);
                    }
                }
                if (!entered)
                    return result;
                // 先登记活跃生产者，再离开生命周期锁取得本次记录所用的上下文。即使关闭并发发生，清理方也必须等这次调用归还资源。
                if (controlView == null) controlView = terminal ? terminalRepeatControl : Volatile.Read(ref diagnosticControl);
                DiagnosticBudgetSnapshot budgetView = Volatile.Read(ref diagnosticBudget);
                PolicySnapshot policyView = policy.Snapshot;
                LogContext contextView = capturedContext ?? controlView.Context;
                PolicyDecision decision = policy.Evaluate(policyView, level, source);
                if (decision != PolicyDecision.Allow)
                    return result = new AdmissionResult(decision == PolicyDecision.Filtered ? AdmissionReason.Filtered : AdmissionReason.InvalidInput);
                if (diagnosticCall && !options.Diagnostics.Enabled)
                    return result = new AdmissionResult(AdmissionReason.Filtered);
                if (diagnosticCall && (diagnosticMetadata == null || diagnosticMetadata.Kind != DiagnosticRecordKind.Event))
                    return result = new AdmissionResult(AdmissionReason.InvalidInput);
                // 在速率、容量、ID 分配或有界字符串复制之前检查未截断的标识符。
                if ((keyInfo && string.IsNullOrEmpty(eventTypeId)) || (eventTypeId != null && eventTypeId.Length > LogRecord.MaxIdLength))
                    return result = new AdmissionResult(AdmissionReason.InvalidInput);
                AdmissionReason classDecision = queue.PrecheckAdmission(level, out quotaTag);
                if (classDecision != AdmissionReason.Accepted)
                    return result = new AdmissionResult(classDecision);
                if (summary != null && (capturedContext == null
                    ? !summary.SameScope(controlView, quotaTag)
                    : summary.Origin.PolicyRevision != controlView.PolicyRevision || !Nullable.Equals(summary.Tag, quotaTag)))
                    return result = new AdmissionResult(AdmissionReason.StaleGeneration);
                int memoryBytes = policy.EstimateRecordBytes(options, summary != null ? repeatSummaryChars : message?.Length ?? 0, stack?.Length ?? 0, diagnosticMetadata);
                if (memoryBytes > options.EffectiveRecordByteLimit)
                    return result = new AdmissionResult(AdmissionReason.RecordTooLarge);
                if (options.Diagnostics.Enabled && options.Diagnostics.BudgetEnabled && !AcceptDiagnosticBudget(controlView, budgetView, quotaTag, level, source, eventTypeId, memoryBytes, diagnosticCall))
                    return result = new AdmissionResult(AdmissionReason.Filtered);
                if (automaticInfo && level == LogLevel.Info && source == LogSource.Unity && ReferenceEquals(controlView.Context, contextView))
                {
                    repeatSlot = options.ConsecutiveUnityInfo
                        ? EnterRunSlot(message, stack, controlView, quotaTag, observation, out coalesced)
                        : EnterRepeatSlot(message, stack, controlView, quotaTag, out coalesced);
                    if (coalesced && repeatSlot.HasPendingFirst)
                    {
                        coalesced = false;
                        if (repeatSlot.TryReadOriginal(out var original, out var trace))
                        {
                            recovered = repeatSlot.PendingCount;
                            message = original;
                            stack = trace;
                        }
                    }
                    if (coalesced)
                    {
                        if (repeatSlot is RunSlot run)
                            CountRun(run, observation);
                        else
                            CountRepeat(repeatSlot);
                        return result = new AdmissionResult(AdmissionReason.Filtered);
                    }
                }
                RateLimitDecision rate = rateLimiter.TryAcquire(level, source, LoggingOptions.RatePayloadBytes(memoryBytes), keyInfo, incidentInfo != null && incidentInfo.HasConfirmedWindow);
                if (rate != RateLimitDecision.Granted)
                {
                    if (rate == RateLimitDecision.ContentionRejected)
                        return result = AdmissionResult.ContentionRejectedAt(AdmissionStage.RateLimit);
                    if (rate == RateLimitDecision.RateLimited && incidentInfo != null && automaticInfo && summary == null
                        && recovered == 0 && !keyInfo && level == LogLevel.Info && source == LogSource.Unity
                        && incidentObservation > 0
                        && incidentInfo.TryCapture(controlView, quotaTag, incidentObservation, clock.TimestampTicks, DateTime.UtcNow, message))
                        worker.NotifyWork();
                    return result = new AdmissionResult(rate == RateLimitDecision.RateLimited ? AdmissionReason.RateLimited : AdmissionReason.InvalidInput);
                }
                // 先占用保守估算的队列容量，再复制和构造记录。提交前的任何退出都要归还预留；已经使用的速率额度保持扣除。
                QueueDecision reserve = queue.TryReserve(level, memoryBytes, out token, out var quotaReason, terminal);
                if (reserve != QueueDecision.Reserved)
                    return result = MapQueue(reserve, AdmissionStage.Reservation, quotaReason);
                reserved = true;
                if (recovered != 0 && !Nullable.Equals(token.QuotaDayTag, quotaTag))
                    return result = new AdmissionResult(AdmissionReason.StaleQuotaDay);
                if (summary != null)
                {
                    if (!Nullable.Equals(token.QuotaDayTag, summary.Tag))
                        return result = new AdmissionResult(AdmissionReason.StaleQuotaDay);
                    message = FormatRepeatSummary(summary);
                }
                string boundedMessage = BoundedCopy(message, options.MaxMessageChars, out bool messageTruncated);
                string boundedStack = BoundedCopy(stack, options.MaxStackChars, out bool stackTruncated);
                long timestamp = clock.TimestampTicks;
                // 在预留和有界复制之后、提交之前赋值。提交失败可能留下编号间隙。
                long number = Interlocked.Increment(ref sequence);
                if (number <= 0)
                    throw new InvalidOperationException("Pipeline sequence exhausted.");
                var utc = recovered == 0 || !options.LegacyRepeatSummaryForBenchmark ? DateTime.UtcNow : repeatSlot.FirstUtc;
                var record = new LogRecord(level, SourceName(source), eventTypeId, recordPrefix, number, recovered == 0 ? utc : repeatSlot.FirstUtc,
                    recovered == 0 ? timestamp : repeatSlot.PendingTicks,
                    boundedMessage, boundedStack, contextView, messageTruncated, stackTruncated, token.QuotaDayTag, true, diagnosticMetadata);
                bool retiringTail = summary != null && capturedContext != null && !terminal;
                QueueDecision commit = queue.TryCommit(token, record, out quotaReason, terminal,
                    retiringTail ? summary.FirstQueueSlot : -1, retiringTail ? summary.FirstSequence : 0);
                if (commit != QueueDecision.Committed)
                    return result = MapQueue(commit, AdmissionStage.QueueCommit, quotaReason);
                reserved = false;
                if (incidentInfo != null && summary == null && level >= LogLevel.Error
                    && incidentObservation > 0 && Nullable.Equals(quotaTag, token.QuotaDayTag)
                    && incidentInfo.TryTrigger(controlView, token.QuotaDayTag, incidentObservation, timestamp, number))
                    worker.NotifyWork();
                repeatSlot?.Remember(record, controlView, recovered, timestamp, token.Slot);
                if (recovered != 0) repeatSlot.LastUtcTicks = utc.Ticks;
                if (repeatSlot is RunSlot first)
                {
                    first.Observation = observation;
                    Volatile.Write(ref activeRun, first);
                }
                if (recovered != 0) Interlocked.Add(ref recoveredUnityInfoObservations, recovered);
                result = new AdmissionResult(AdmissionReason.Accepted);
                // 即使消费者先于本次递增完成，也要统计成功的所有权转移。
                counters.Add(HealthMetric.Accepted, bucket);
                // 切号摘要由正在探测队列的工作线程提交；只有外部生产者需要唤醒它。
                if (!terminal && (summary == null || (capturedContext == null
                    && (options.LegacyRunWorkerForBenchmark || !worker.IsWorkerThread)))) worker.NotifyWork();
                return result;
            }
            catch (Exception)
            {
                // 生产者回调不得将异常转成字符串，也不得重入日志系统。
                if (!result.IsAccepted)
                    result = new AdmissionResult(AdmissionReason.ConstructionFailed);
                return result;
            }
            finally
            {
                if (!options.ConsecutiveUnityInfo && result.Reason == AdmissionReason.ContentionRejected)
                {
                    // 普通准入失败后仅再探测一圈。可以借用已提交的同域首条，
                    // 或暂存待准入的观测；不驱逐、不重取额度，也不再次提交队列。
                    bool matched = false;
                    if (repeatSlot == null && automaticInfo && level == LogLevel.Info && source == LogSource.Unity
                        && controlView != null && (capturedContext == null || ReferenceEquals(controlView.Context, capturedContext)))
                        repeatSlot = EnterRepeatSlot(message, stack, controlView, quotaTag, out matched, false);
                    if (matched && !repeatSlot.HasPendingFirst)
                    {
                        if (repeatSlot.Delta < long.MaxValue)
                        {
                            repeatSlot.ObserveRepeat(!options.LegacyRepeatSummaryForBenchmark);
                            Interlocked.Increment(ref recoveredUnityInfoObservations);
                        }
                    }
                    else if (repeatSlot != null)
                    {
                        repeatSlot.ObserveContention(message, stack, controlView, quotaTag, clock.TimestampTicks);
                    }
                }
                if (repeatSlot != null)
                {
                    bool hasTail = repeatSlot.Delta != 0;
                    repeatSlot.Exit();
                    // 若工作线程已扫描，最后离开旧身份的生产者补发一次提示。
                    if (hasTail && !ReferenceEquals(controlView, Volatile.Read(ref diagnosticControl)))
                        worker.NotifyLateIdentityTail();
                }
                if (reserved)
                    queue.CancelReservation(token);
                if (coalesced)
                    Interlocked.Increment(ref coalescedUnityInfo);
                else if (!result.IsAccepted)
                    counters.Add(result.Reason == AdmissionReason.Filtered ? HealthMetric.Filtered : HealthMetric.RejectedBeforeAccept, bucket, result.Reason);
                if (entered)
                {
                    counters.Add(HealthMetric.PendingAdmission, bucket, amount: -1);
                    // 通知在仍有注册时完成。注销之后不再访问工作线程句柄。
                    lock (lifecycle)
                    {
                        producers--;
                        producerRegistry.Exit();
                    }
                }
            }
        }
        internal AdmissionResult TryAcceptDiagnostic(LogLevel level, LogSource source, string message, DiagnosticRecordMetadata metadata, string stack = null, string eventTypeId = null, long? expectedGeneration = null)
            => TryAccept(level, source, message, stack, eventTypeId, expectedGeneration, metadata, true);
        private AdmissionResult MapQueue(QueueDecision decision, AdmissionStage stage, AdmissionReason quotaReason)
        {
            if (decision == QueueDecision.NotReady)
                return new AdmissionResult(AdmissionReason.NotReady);
            if (decision == QueueDecision.StaleQuotaDay)
                return new AdmissionResult(AdmissionReason.StaleQuotaDay);
            if (decision == QueueDecision.QuotaRejected)
                return new AdmissionResult(quotaReason);
            if (decision == QueueDecision.ContentionRejected)
                return AdmissionResult.ContentionRejectedAt(stage);
            if (decision == QueueDecision.Closed)
                return new AdmissionResult(worker != null && worker.HasFaulted ? AdmissionReason.Faulted : AdmissionReason.Stopping);
            return new AdmissionResult(decision == QueueDecision.Full ? AdmissionReason.QueueFull : decision == QueueDecision.RecordTooLarge ? AdmissionReason.RecordTooLarge : AdmissionReason.InvalidInput);
        }
        private static string SourceName(LogSource source)
        {
            switch (source)
            {
                case LogSource.Unity:
                    return "Unity";
                case LogSource.Application:
                    return "Application";
                case LogSource.Plugin:
                    return "Plugin";
                default:
                    return "Unknown";
            }
        }
        private static string BoundedCopy(string value, int limit, out bool truncated)
        {
            if (value == null)
            {
                truncated = false;
                return string.Empty;
            }
            truncated = value.Length > limit;
            if (!truncated)
                return value; // 字符串不可变；保留量已经有界。
            int length = limit;
            if (length > 0 && char.IsHighSurrogate(value[length - 1]) && char.IsLowSurrogate(value[length]))
                length--;
            return length == 0 ? string.Empty : value.Substring(0, length);
        }
        internal void RequestStop(WorkerStopMode mode, TimeSpan budget)
        {
            if (budget < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(budget));
            long now = Math.Max(0, clock.TimestampTicks);
            RequestStop(mode, budget.Ticks > long.MaxValue - now ? long.MaxValue : now + budget.Ticks);
        }
        internal void RequestStop(WorkerStopMode mode, long deadlineTicks)
        {
            if (mode < WorkerStopMode.DrainUntilDeadline || mode > WorkerStopMode.DropPending)
                throw new ArgumentOutOfRangeException(nameof(mode));
            if (deadlineTicks < 0)
                throw new ArgumentOutOfRangeException(nameof(deadlineTicks));
            lock (lifecycle)
            {
                if (disposed || stopRequested)
                    return;
                // Close 与提交在队列门锁下线性化，随后才发布停止或取消。
                queue.Close();
                stopDeadline = deadlineTicks;
                stopRequested = true;
                // 优雅停止保留最后一份控制快照，供已累计的重复次数在截止前汇总。立即丢弃模式无需再提交摘要。
                if (mode == WorkerStopMode.DrainUntilDeadline && state == PipelineCompletion.Running)
                    terminalRepeatControl = diagnosticControl;
                UpdateDiagnosticControl(false, false, null, null);
                ObserveFault();
                if (state != PipelineCompletion.Faulted)
                    state = PipelineCompletion.Stopping;
                worker?.RequestStop(mode, deadlineTicks);
            }
            incidentInfo?.Reset(null);
        }
        private void ObserveFault()
        {
            if (worker != null && worker.HasFaulted && state != PipelineCompletion.Faulted)
            {
                queue.Close();
                state = PipelineCompletion.Faulted;
            }
        }
        private bool WorkerTerminated()
        {
            if (worker == null)
                return true;
            WorkerCompletion completion = worker.Completion;
            return completion == WorkerCompletion.NotStarted || completion == WorkerCompletion.Stopped || completion == WorkerCompletion.Faulted;
        }
        internal bool TryDispose()
        {
            lock (lifecycle)
            {
                if (disposed)
                    return true;
                ObserveFault();
                if (!SafeToReleaseUnderGate())
                    return false;
                queue.Close();
                worker?.Dispose();
                disposed = true;
                if (state != PipelineCompletion.Faulted)
                    state = PipelineCompletion.Stopped;
            }
            incidentInfo?.Reset(null);
            return true;
        }
        public void Dispose()
        {
            if (!TryDispose())
                throw new InvalidOperationException("Worker or registered producers still own resources; request stop and poll completion.");
        }
        internal HealthSnapshot Snapshot()
        {
            HealthSnapshot result = counters.Snapshot();
            result.CoalescedUnityInfo = Interlocked.Read(ref coalescedUnityInfo);
            // 入口健康统计包含折叠的调用；磁盘丢弃统计只读取原始过滤和拒绝计数。
            long filteredInfo = result.Get(HealthMetric.Filtered, LogLevel.Info, AdmissionReason.Filtered);
            result.Set(HealthMetric.Filtered, LogLevel.Info, filteredInfo + result.CoalescedUnityInfo, AdmissionReason.Filtered);
            result.RecoveredUnityInfoObservations = Interlocked.Read(ref recoveredUnityInfoObservations);
            result.IncidentInfoAttempts = Interlocked.Read(ref incidentInfoAttempts);
            result.IncidentInfoTransferred = Interlocked.Read(ref incidentInfoTransferred);
            result.IncidentInfoMissed = incidentInfo?.Missed ?? 0;
            result.UnityRepeatSummaryAttempts = Interlocked.Read(ref unityRepeatSummaryAttempts);
#if LOGOS_WORKER_BENCHMARK
            result.LiveSummaryAttempts = Interlocked.Read(ref liveSummaryAttempts);
            result.StopSummaryAttempts = Interlocked.Read(ref stopSummaryAttempts);
            worker?.CopyBenchmarkCounters(result);
#endif
            QueueSnapshot queued = queue.Snapshot();
            WorkerSnapshot consumed = worker?.Snapshot();
            result.CompletionKind = consumed?.CompletionKind ?? PipelineCompletionKind.SynchronousConsumedForTest;
            result.ProducerWakeSets = consumed?.ProducerWakeSets ?? 0;
            result.ControlWakeSets = consumed?.ControlWakeSets ?? 0;
            for (int i = 0; i < 6; i++)
            {
                var level = (LogLevel)i;
                result.Set(HealthMetric.Queued, level, queued.GetQueued(level));
                if (consumed == null)
                    continue;
                result.Set(HealthMetric.InFlight, level, consumed.GetInFlight(level));
                result.Set(HealthMetric.ConsumedForTest, level, consumed.GetConsumed(level));
                result.Set(HealthMetric.WorkerInFlight, level, consumed.GetWorkerInFlight(level));
                result.Set(HealthMetric.StorageInFlight, level, consumed.GetStorageInFlight(level));
                result.Set(HealthMetric.CommittedRecords, level, consumed.GetCommitted(level));
                result.Set(HealthMetric.RepresentedRecords, level, consumed.GetRepresented(level));
                result.Set(HealthMetric.DroppedAfterAccept, level, consumed.GetDropped(level, WorkerDropReason.ProcessorRejected), AdmissionReason.ProcessorRejected);
                result.Set(HealthMetric.DroppedAfterAccept, level, consumed.GetDropped(level, WorkerDropReason.ProcessorFault), AdmissionReason.Faulted);
                result.Set(HealthMetric.DroppedAfterAccept, level, consumed.GetDropped(level, WorkerDropReason.Shutdown), AdmissionReason.Stopping);
                for (int j = 0; j < HealthCounters.ReasonCount; j++)
                {
                    var reason = (AdmissionReason)j;
                    result.Set(HealthMetric.DroppedAfterAccept, level, result.Get(HealthMetric.DroppedAfterAccept, level, reason) + consumed.GetBufferedDropped(level, reason) + queue.GetControlDropped(level, reason), reason);
                }
            }
            return result;
        }
    }
}
