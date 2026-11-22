using System;
namespace Logos.Storage
{
    // 观察到候选日期与发布新日状态分属两个步骤。时间不确定时可以恢复既有数据，新的过期授权仍需等待确认。
    public sealed partial class JournalSession
    {
        private JournalDayClock dayClock;
        private JournalTimeObservation timeObservation;
        private bool hasTimeObservation;
        private bool timeObservationPending;
        private bool safeTimeStep;
        private bool timeAuthorityDirty;
        // 在实际发布新权威状态的边界检查，包括恢复和槽位复用。
        // 保留先前已接纳候选；但当前样本不确定时冻结新授权。
        private bool CanAuthorizeNewTimeState => !safeTimeStep
            || (dayClock != null && !dayClock.Decision.RequiresConfirmation);
        /// <summary>经过认证的已发布日期；初始化或恢复发现权威状态之前为 null。</summary>
        public long? CurrentEffectiveDay => global == null ? (long?)null : global.HighestDay;
        public bool CurrentEffectiveDayConfirmed => global != null && global.TimeConfirmed;
        public JournalTimeDecision TimeDecision => dayClock == null
            ? new JournalTimeDecision(0, false, JournalTimeDecisionKind.TimeConfirmationRequired, default)
            : dayClock.Decision;

        /// <summary>仅供所有者工作线程使用。适配器安排的观察间隔最多一秒；
        /// 缓存的 Step 调用不查询 provider，构造也不执行 I/O。</summary>
        public void ObserveTime(IJournalTimeAuthority authority)
        {
            CheckThread();
            if (authority == null)
                throw new ArgumentNullException(nameof(authority));
            EnsureTimeUsable();
            ObserveTime(authority.Observe());
        }
        public void ObserveTime(JournalTimeObservation observation)
        {
            CheckThread();
            EnsureTimeUsable();
            if (observation.UtcNow.Kind != DateTimeKind.Utc)
                throw new ArgumentException("UTC is required.", nameof(observation));
            // 每个样本只应用一次；只保留最后一个样本会掩盖中间的异常。
            if (dayClock != null)
                dayClock.Observe(observation);
            timeObservation = observation;
            hasTimeObservation = true;
            timeObservationPending = dayClock == null;
        }
        /// <summary>安全恢复和时间驱动。需要工作线程观察；绝不把冷启动墙上日期视为已确认。</summary>
        public void Step(JournalStepContext context)
        {
            CheckThread();
            EnsureTimeUsable();
            if (context.ShouldYield)
                return;
            if (!hasTimeObservation)
                throw new InvalidOperationException("Observe time before driving the safe journal path.");
            safeTimeStep = true;
            try
            {
                if (global != null)
                    ApplyObservedTime();
                StepCore(dayClock?.Decision.EffectiveDay ?? 0, context);
            }
            finally
            {
                safeTimeStep = false;
            }
        }
        private void EnsureTimeUsable()
        {
            if (Status == JournalStatus.Disposed || disposalStarted)
                throw new ObjectDisposedException(nameof(JournalSession));
        }
        private void ApplyObservedTime()
        {
            if (dayClock == null)
                dayClock = new JournalDayClock(global.HighestDay, global.TimeConfirmed);
            if (timeObservationPending)
            {
                dayClock.Observe(timeObservation);
                timeObservationPending = false;
            }
            requestedDay = Math.Max(global.HighestDay, dayClock.Decision.EffectiveDay);
            if (!global.TimeConfirmed && dayClock.Decision.IsConfirmed && requestedDay == global.HighestDay)
            {
                global.TimeConfirmed = true;
                timeAuthorityDirty = true;
            }
        }
        private void ConfirmExplicitDay(long day)
        {
            if (global != null && !global.TimeConfirmed && day >= global.HighestDay)
            {
                global.TimeConfirmed = true;
                timeAuthorityDirty = true;
            }
        }
        private void EstablishVirginTime()
        {
            dayClock = JournalDayClock.Virgin(timeObservation);
            if (timeObservation.Trust == JournalTimeTrust.Confirmed)
                dayClock.Observe(timeObservation);
            timeObservationPending = false;
            requestedDay = dayClock.Decision.EffectiveDay;
        }
    }
}
