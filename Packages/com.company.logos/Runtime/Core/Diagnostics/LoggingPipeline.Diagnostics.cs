using System;
using System.Threading;
namespace Logos
{
    internal sealed partial class LoggingPipeline
    {
        private DiagnosticControlState diagnosticControl;
        private DiagnosticBudgetSnapshot diagnosticBudget;
        private readonly long[] diagnosticSamples = new long[PolicySnapshot.SourceCount * 6];
        private readonly long[] diagnosticFiltered = new long[(int)DiagnosticFilterDetail.Count];
        private long diagnosticLastTicks;
        public DiagnosticControlState ReadControlState() => Volatile.Read(ref diagnosticControl);
        public void PublishBudgetSnapshot(DiagnosticBudgetSnapshot snapshot)
        {
            // 发布不是授权：回调仍核对当前信箱、队列 tag 和 Core 时钟。
            if (snapshot == null)
            {
                Interlocked.Exchange(ref diagnosticBudget, null)?.Permit?.Invalidate();
                return;
            }
            if (ReferenceEquals(snapshot.Control, ReadControlState()))
                Volatile.Write(ref diagnosticBudget, snapshot);
        }
        internal DiagnosticBudgetSnapshot BudgetSnapshot => Volatile.Read(ref diagnosticBudget);
        internal long GetDiagnosticFiltered(DiagnosticFilterDetail detail)
            => (uint)detail < (uint)DiagnosticFilterDetail.Count ? Interlocked.Read(ref diagnosticFiltered[(int)detail]) : 0;
        private void UpdateDiagnosticControl(bool policyChanged, bool contextChanged, LogContext nextContext, TemporaryDiagnosticRequest request)
        {
            var old = diagnosticControl;
            // 版本耗尽保守终止临时授权，不回绕。
            if (old.RequestRevision == long.MaxValue || (policyChanged && old.PolicyRevision == long.MaxValue) || (contextChanged && old.ContextRevision == long.MaxValue))
                throw new InvalidOperationException("Diagnostic control revision exhausted.");
            Volatile.Write(ref diagnosticControl, new DiagnosticControlState(Generation, old.PolicyRevision + (policyChanged ? 1 : 0), old.ContextRevision + (contextChanged ? 1 : 0), nextContext ?? old.Context,
                old.RequestRevision + 1, request, old.Enabled, old.BudgetEnabled));
            PublishBudgetSnapshot(null);
        }
        internal bool TryRequestDiagnostics(long expectedContextRevision, TemporaryDiagnosticRequest request)
        {
            lock (lifecycle)
            {
                if (disposed || stopRequested || state != PipelineCompletion.Running || !options.Diagnostics.Enabled || !options.Diagnostics.BudgetEnabled
                    || expectedContextRevision != diagnosticControl.ContextRevision || diagnosticControl.RequestRevision == long.MaxValue)
                    return false;
                UpdateDiagnosticControl(false, false, null, request);
            }
            incidentInfo?.Reset(ReadControlState());
            NotifyControl();
            return true;
        }
        private bool FilterDiagnostic(DiagnosticFilterDetail detail)
        {
            Interlocked.Increment(ref diagnosticFiltered[(int)detail]);
            return false;
        }
        private bool AcceptDiagnosticBudget(DiagnosticControlState control, DiagnosticBudgetSnapshot snapshot, QuotaDayTag? tag,
            LogLevel level, LogSource source, string eventTypeId, int bytes, bool explicitDiagnostic)
        {
            long previous = Interlocked.Read(ref diagnosticLastTicks);
            long now = clock.TimestampTicks;
            // 有界 CAS；竞争或逆时钟采用保守策略，不以重试循环阻塞回调。
            if (now < previous)
                snapshot?.Permit?.Invalidate();
            bool monotonic = now >= previous && Interlocked.CompareExchange(ref diagnosticLastTicks, now, previous) == previous;
            bool valid = monotonic && snapshot != null && snapshot.IsCurrent && snapshot.Trusted && ReferenceEquals(control, ReadControlState()) && ReferenceEquals(snapshot.Control, control)
                && control.PipelineGeneration == Generation && tag.HasValue && tag.Value.Equals(snapshot.QuotaDayTag) && now >= snapshot.IssuedTicks && now < snapshot.ExpiresTicks;
            if (explicitDiagnostic && level < LogLevel.Info)
            {
                var permit = valid ? snapshot.Permit : null;
                if (permit == null || permit.RequestRevision != control.RequestRevision || level < permit.MinimumLevel)
                    return FilterDiagnostic(DiagnosticFilterDetail.DetailFloor);
                var charge = permit.Charge(now, bytes);
                if (charge != DiagnosticPermitCharge.Granted)
                    return FilterDiagnostic(charge == DiagnosticPermitCharge.Contention ? DiagnosticFilterDetail.PermitContention : DiagnosticFilterDetail.PermitExhausted);
                // 临时明细已收费；后续速率、预留、复制或提交失败不退款。
                return true;
            }
            if (level >= LogLevel.Error || (explicitDiagnostic && DiagnosticRecordMetadata.IsCriticalEvent(eventTypeId)))
                return true;
            // 普通明细按来源和等级分别编号。采样间隔只允许 1、2、4、8，因此可以用位掩码保留各组的第一条以及之后的等距样本。
            int every = valid ? snapshot.SampleEvery : 8;
            long ordinal = Interlocked.Increment(ref diagnosticSamples[PolicySnapshot.SourceIndex(source) * 6 + (int)level]);
            return ((ordinal - 1) & (every - 1)) == 0 || FilterDiagnostic(DiagnosticFilterDetail.Sampled);
        }
    }
}
