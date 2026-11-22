using System;
namespace Logos.Storage
{
    // 冷启动时先对照持久日期；出现时钟异常后需要可信证据解除隔离，单纯重复同一异常日期无法自行确认。
    /// <summary>工作线程持有、常量空间的日期策略。只有已接纳基线才能证实连续跨越午夜。</summary>
    public sealed class JournalDayClock
    {
        public const long MaximumDeviationTicks = 5 * TimeSpan.TicksPerSecond;
        private long highest;
        private bool confirmed;
        private bool anchored;
        private bool quarantined;
        private long previousUtc;
        private long previousMono;
        public JournalTimeDecision Decision
        {
            get;
            private set;
        }
        public JournalDayClock(long persistedHighestDay, bool persistedConfirmed)
        {
            if (persistedHighestDay < 0 || persistedHighestDay > DateTime.MaxValue.Ticks / TimeSpan.TicksPerDay)
                throw new ArgumentOutOfRangeException(nameof(persistedHighestDay));
            highest = persistedHighestDay;
            confirmed = persistedConfirmed;
            Decision = new JournalTimeDecision(highest, confirmed, JournalTimeDecisionKind.TimeConfirmationRequired, default);
        }
        internal static JournalDayClock Virgin(JournalTimeObservation observation)
        {
            var clock = new JournalDayClock(observation.UtcNow.Ticks / TimeSpan.TicksPerDay, false);
            clock.Anchor(observation);
            clock.Decision = new JournalTimeDecision(clock.highest, false, JournalTimeDecisionKind.Provisional, observation.EvidenceId);
            return clock;
        }
        public JournalTimeDecision Observe(JournalTimeObservation observation)
        {
            long utc = observation.UtcNow.Ticks;
            long day = utc / TimeSpan.TicksPerDay;
            var kind = quarantined ? JournalTimeDecisionKind.TimeUncertain : JournalTimeDecisionKind.TimeConfirmationRequired;
            if (observation.Trust == JournalTimeTrust.Confirmed)
            {
                if (day >= highest)
                {
                    highest = day;
                    confirmed = true;
                    quarantined = false;
                    Anchor(observation);
                    kind = JournalTimeDecisionKind.Confirmed;
                }
                else
                {
                    anchored = false;
                    quarantined = true;
                    kind = JournalTimeDecisionKind.TimeUncertain;
                }
            }
            else if (!anchored)
            {
                if (day == highest && !quarantined)
                {
                    Anchor(observation);
                    kind = JournalTimeDecisionKind.SameDayContinuation;
                }
            }
            else
            {
                // 只比较时长：不将任一起点与 Core 或 Stopwatch 绝对值比较。
                decimal monoDelta = (decimal)observation.MonotonicTicks - previousMono;
                long utcDelta = utc - previousUtc;
                if (monoDelta < 0 || utcDelta < 0 || Math.Abs((decimal)utcDelta - monoDelta) > MaximumDeviationTicks)
                {
                    anchored = false;
                    quarantined = true;
                    kind = JournalTimeDecisionKind.TimeUncertain;
                }
                else
                {
                    Anchor(observation);
                    kind = day > highest ? JournalTimeDecisionKind.Continuous : JournalTimeDecisionKind.SameDayContinuation;
                    if (day > highest)
                    {
                        highest = day;
                        confirmed = true;
                    }
                }
            }
            Decision = new JournalTimeDecision(highest, confirmed, kind, observation.EvidenceId);
            return Decision;
        }
        private void Anchor(JournalTimeObservation observation)
        {
            anchored = true;
            previousUtc = observation.UtcNow.Ticks;
            previousMono = observation.MonotonicTicks;
        }
    }
}
