using System;
namespace Logos
{
    [Flags]
    public enum LogClassMask
    {
        None = 0, Normal = 1, ErrorFatal = 2, All = 3
    }
    public enum QuotaAdmissionState
    {
        NotReady, Recovering, Ready, DayTransition, Closed
    }
    // 日期标识配额归属，代际标识该日的准入状态版本。即使日期相同，旧版本的预留也可能失效。
    public readonly struct QuotaDayTag : IEquatable<QuotaDayTag>
    {
        public long DayId
        {
            get;
        }
        public ulong Generation
        {
            get;
        }
        public QuotaDayTag(long dayId, ulong generation)
        {
            if (dayId < 0 || dayId > DateTime.MaxValue.Ticks / TimeSpan.TicksPerDay)
                throw new ArgumentOutOfRangeException(nameof(dayId));
            DayId = dayId;
            Generation = generation;
        }
        public bool Equals(QuotaDayTag other) => DayId == other.DayId && Generation == other.Generation;
        public override bool Equals(object obj) => obj is QuotaDayTag other && Equals(other);
        public override int GetHashCode() => DayId.GetHashCode() ^ Generation.GetHashCode();
    }
    public readonly struct QuotaAdmissionGate : IEquatable<QuotaAdmissionGate>
    {
        public long DayId
        {
            get;
        }
        public QuotaAdmissionState State
        {
            get;
        }
        public AdmissionReason NormalDecision
        {
            get;
        }
        public AdmissionReason ErrorDecision
        {
            get;
        }
        public QuotaAdmissionGate(long dayId, QuotaAdmissionState state, AdmissionReason normalDecision, AdmissionReason errorDecision)
        {
            if (dayId < 0 || dayId > DateTime.MaxValue.Ticks / TimeSpan.TicksPerDay || state < QuotaAdmissionState.NotReady || state > QuotaAdmissionState.Closed)
                throw new ArgumentOutOfRangeException();
            if ((uint)normalDecision >= HealthCounters.ReasonCount || (uint)errorDecision >= HealthCounters.ReasonCount)
                throw new ArgumentOutOfRangeException();
            DayId = dayId;
            State = state;
            NormalDecision = normalDecision;
            ErrorDecision = errorDecision;
        }
        public bool Equals(QuotaAdmissionGate other) => DayId == other.DayId && State == other.State && NormalDecision == other.NormalDecision && ErrorDecision == other.ErrorDecision;
        public override bool Equals(object obj) => obj is QuotaAdmissionGate other && Equals(other);
        public override int GetHashCode() => DayId.GetHashCode() ^ (int)State ^ (int)NormalDecision ^ (int)ErrorDecision;
    }
    public enum QueueDayDropResult
    {
        NoMatchingHead, Dropped, Contended, LeaseBusy
    }
    public interface ILogAdmissionController
    {
        QuotaDayTag SetGate(QuotaAdmissionGate gate);
        QueueDayDropResult DropOneQueuedDay(long dayId, LogClassMask classes, AdmissionReason reason);
    }
}
