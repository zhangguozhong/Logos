using System;
using System.Threading;
namespace Logos.Storage
{
    public sealed partial class StorageBatchProcessor
    {
        private IDiagnosticControlBridge diagnosticBridge;
        private readonly DiagnosticBudgetFence diagnosticBudgetFence = new DiagnosticBudgetFence();
        private long observedBudgetFence;
        private DiagnosticControlState currentDiagnosticControl;
        private DiagnosticPermit diagnosticPermit;
        private QuotaDayTag publishedQuotaTag, budgetTag;
        private long nextBudget, budgetLastTicks, activatedRequestRevision, budgetWriteGeneration, budgetCleanupGeneration;
        private bool budgetWasReady;
        private int budgetSampleEvery = 1;
        // C2 只在既有 worker 上观察此值和边界；不引入回调委托或队列。
        private DiagnosticControlState CurrentDiagnosticControl => currentDiagnosticControl;
        partial void DiagnosticControlBoundaryChanged();
        // 清理或日期切换会推进授权边界。较早轮次即便在边界之后发布预算，其快照也不能重新启用旧许可。
        private void InvalidateDiagnosticBudget()
        {
            diagnosticPermit?.Invalidate();
            diagnosticPermit = null;
            diagnosticBridge?.PublishBudgetSnapshot(null);
            DiagnosticControlBoundaryChanged();
        }
        private void RefreshDiagnosticBudget(WorkerControlContext context)
        {
            if (diagnosticBridge == null)
                return;
            // 必须先取 epoch 再观察 ready；即使此轮在 clear 返回后迟到发布，也不能恢复授权。
            long capturedFence = diagnosticBudgetFence.Capture();
            bool fenceChanged = capturedFence != observedBudgetFence;
            var control = diagnosticBridge.ReadControlState();
            long now = context.NowTicks;
            bool changed = !ReferenceEquals(control, currentDiagnosticControl);
            bool backwards = now < budgetLastTicks;
            bool ready = control.Enabled && journal.Status == JournalStatus.Ready && journal.TimeDecision.IsConfirmed && !journal.TimeDecision.RequiresConfirmation
                && journal.CurrentEffectiveDay == activeDay && observation.UtcNow.Ticks / TimeSpan.TicksPerDay == activeDay
                && !transitioning && !completed && !context.StopRequested && !context.IsCancellationRequested
                && clearTargets == null && Volatile.Read(ref pendingClear) == null && !journal.Clear.IsActive;
            bool readinessChanged = budgetWasReady != ready;
            bool scopeChanged = !budgetTag.Equals(publishedQuotaTag) || budgetWriteGeneration != journal.WriteGeneration || budgetCleanupGeneration != journal.CleanupGeneration;
            bool hadActivePermit = diagnosticPermit != null;
            currentDiagnosticControl = control;
            if (changed || backwards || scopeChanged || fenceChanged || (budgetWasReady && !ready))
            {
                InvalidateDiagnosticBudget();
                if (fenceChanged || backwards || (!changed && hadActivePermit && (scopeChanged || !ready)))
                    activatedRequestRevision = Math.Max(activatedRequestRevision, control.RequestRevision);
            }
            observedBudgetFence = capturedFence;
            budgetLastTicks = now;
            budgetWasReady = ready;
            budgetTag = publishedQuotaTag;
            budgetWriteGeneration = journal.WriteGeneration;
            budgetCleanupGeneration = journal.CleanupGeneration;
            if (!control.Enabled || !control.BudgetEnabled)
                return;
            if (!changed && !backwards && !scopeChanged && !fenceChanged && !readinessChanged && now < nextBudget)
                return;
            nextBudget = Add(Math.Max(0, now), TimeSpan.TicksPerSecond);
            if (ready && !backwards && diagnosticPermit == null && control.Request != null && control.RequestRevision > activatedRequestRevision)
            {
                activatedRequestRevision = control.RequestRevision;
                if (now >= 0 && now <= long.MaxValue - control.Request.DurationTicks)
                    diagnosticPermit = new DiagnosticPermit(control.RequestRevision, control.Request, now);
            }
            if (ready)
            {
                long limit = journal.EffectiveOptions.MaxDailyFileBytes - journal.EffectiveOptions.ErrorFatalReservedBytesPerDay;
                long remaining = Math.Max(0, limit - journal.NormalBytes);
                int tier = remaining <= limit / 16 ? 8 : remaining <= limit / 8 ? 4 : remaining <= limit / 4 ? 2 : 1;
                // 仅收紧；同一实例重试、clear 或跨日不恢复已消耗的临时预算或采样宽度。
                budgetSampleEvery = Math.Max(budgetSampleEvery, tier);
            }
            diagnosticBridge.PublishBudgetSnapshot(new DiagnosticBudgetSnapshot(control, publishedQuotaTag, journal.WriteGeneration, journal.CleanupGeneration,
                Math.Max(0, now), Add(Math.Max(0, now), 2 * TimeSpan.TicksPerSecond), ready && !backwards, ready ? budgetSampleEvery : 8, ready && !backwards ? diagnosticPermit : null, diagnosticBudgetFence, capturedFence));
        }
    }
}
