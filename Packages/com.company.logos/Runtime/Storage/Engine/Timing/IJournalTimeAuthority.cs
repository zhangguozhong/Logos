using System;
namespace Logos.Storage
{
    // 样本的 UTC、单调时钟和证据等级共同决定日期能否推进；调用方还要保留已发布的最高日期以避免回退。
    /// <summary>仅由所有者工作线程在有界控制 tick 中调用。不隐含网络或调度操作。</summary>
    public interface IJournalTimeAuthority
    {
        JournalTimeObservation Observe();
    }

    public enum JournalTimeTrust
    {
        Unconfirmed, SameDayContinuation, Confirmed
    }

    /// <summary>固定大小的证据标识符；不保留宿主字符串或证据历史。</summary>
    public readonly struct JournalTimeObservation
    {
        public DateTime UtcNow
        {
            get;
        }
        /// <summary>以此 provider 自身单调时钟起点计量，单位为 100 ns。</summary>
        public long MonotonicTicks
        {
            get;
        }
        public JournalTimeTrust Trust
        {
            get;
        }
        public Guid EvidenceId
        {
            get;
        }
        public JournalTimeObservation(DateTime utcNow, long monotonicTicks, JournalTimeTrust trust = JournalTimeTrust.Unconfirmed, Guid evidenceId = default)
        {
            if (utcNow.Kind != DateTimeKind.Utc)
                throw new ArgumentException("UTC is required.", nameof(utcNow));
            if (trust < JournalTimeTrust.Unconfirmed || trust > JournalTimeTrust.Confirmed)
                throw new ArgumentOutOfRangeException(nameof(trust));
            UtcNow = utcNow;
            MonotonicTicks = monotonicTicks;
            Trust = trust;
            EvidenceId = evidenceId;
        }
    }

    public enum JournalTimeDecisionKind
    {
        Provisional, SameDayContinuation, Confirmed, Continuous,
        TimeConfirmationRequired, TimeUncertain
    }

    public readonly struct JournalTimeDecision
    {
        public long EffectiveDay
        {
            get;
        }
        public bool IsConfirmed
        {
            get;
        }
        public JournalTimeDecisionKind Kind
        {
            get;
        }
        public Guid EvidenceId
        {
            get;
        }
        public bool RequiresConfirmation => Kind == JournalTimeDecisionKind.TimeConfirmationRequired || Kind == JournalTimeDecisionKind.TimeUncertain;
        internal JournalTimeDecision(long day, bool confirmed, JournalTimeDecisionKind kind, Guid evidenceId)
        {
            EffectiveDay = day;
            IsConfirmed = confirmed;
            Kind = kind;
            EvidenceId = evidenceId;
        }
        /// <summary>仅比较给定日期与 EffectiveDay；不表示当前样本已授权。
        /// 适配器必须另外验证记录观察到的日期和当前时间决策。</summary>
        public bool CanContinueDay(long day) => day == EffectiveDay;
    }
}
