using System;

namespace Logos
{
    // 默认结构体的原因是枚举首项 InvalidInput，因此未赋值的结果不会被调用方误认为已经入队。
    public enum AdmissionReason
    {
        InvalidInput, Filtered, RateLimited, QueueFull, Stopping, Faulted, ConstructionFailed, Accepted, ContentionRejected, RecordTooLarge, StaleGeneration, ProcessorRejected, NotReady, Recovering, DayTransition, StaleQuotaDay, NormalDailyLimit, ErrorDirectoryLimit, TimeUncertain, TimeConfirmationRequired, DayRolloverDropped, ShutdownDropped, StorageFault, EncodingFailed, RedactionFailed, StorageLimit, UncertainIo, ErrorDailyLimit, DirectoryLimit, MetadataLimit, WriteRateLimited, Cleared
    }
    public enum AdmissionStage
    {
        Validation, Policy, RateLimit, Reservation, Lifecycle, Construction, QueueCommit, Processing
    }
    public readonly struct AdmissionResult
    {
        public AdmissionReason Reason
        {
            get;
        }
        public AdmissionStage Stage
        {
            get;
        }
        /// <summary>Accepted 仅表示队列提交，不表示持久化或上传。</summary>
        public bool IsAccepted => Reason == AdmissionReason.Accepted;
        // 只有争用可能发生在多个同步点。
        public static AdmissionResult ContentionRejectedAt(AdmissionStage stage)
        {
            if (stage != AdmissionStage.RateLimit && stage != AdmissionStage.Reservation && stage != AdmissionStage.QueueCommit)
                throw new ArgumentOutOfRangeException(nameof(stage));
            return new AdmissionResult(stage);
        }
        private AdmissionResult(AdmissionStage contentionStage)
        {
            Reason = AdmissionReason.ContentionRejected;
            Stage = contentionStage;
        }
        public AdmissionResult(AdmissionReason reason)
        {
            Reason = reason;
            switch (reason)
            {
                case AdmissionReason.RecordTooLarge:
                case AdmissionReason.InvalidInput:
                    Stage = AdmissionStage.Validation;
                    break;
                case AdmissionReason.Filtered:
                    Stage = AdmissionStage.Policy;
                    break;
                case AdmissionReason.RateLimited:
                    Stage = AdmissionStage.RateLimit;
                    break;
                case AdmissionReason.ContentionRejected:
                case AdmissionReason.QueueFull:
                    Stage = AdmissionStage.Reservation;
                    break;
                case AdmissionReason.StaleGeneration:
                case AdmissionReason.Stopping:
                case AdmissionReason.Faulted:
                    Stage = AdmissionStage.Lifecycle;
                    break;
                case AdmissionReason.ConstructionFailed:
                    Stage = AdmissionStage.Construction;
                    break;
                case AdmissionReason.ProcessorRejected:
                    Stage = AdmissionStage.Processing;
                    break;
                case AdmissionReason.NotReady:
                case AdmissionReason.Recovering:
                case AdmissionReason.DayTransition:
                case AdmissionReason.StaleQuotaDay:
                case AdmissionReason.NormalDailyLimit:
                case AdmissionReason.ErrorDirectoryLimit:
                case AdmissionReason.TimeUncertain:
                case AdmissionReason.TimeConfirmationRequired:
                case AdmissionReason.ErrorDailyLimit:
                case AdmissionReason.DirectoryLimit:
                case AdmissionReason.MetadataLimit:
                case AdmissionReason.Cleared:
                    Stage = AdmissionStage.Reservation;
                    break;
                case AdmissionReason.DayRolloverDropped:
                case AdmissionReason.ShutdownDropped:
                case AdmissionReason.StorageFault:
                case AdmissionReason.EncodingFailed:
                case AdmissionReason.RedactionFailed:
                case AdmissionReason.StorageLimit:
                case AdmissionReason.WriteRateLimited:
                case AdmissionReason.UncertainIo:
                    Stage = AdmissionStage.Processing;
                    break;
                case AdmissionReason.Accepted:
                    Stage = AdmissionStage.QueueCommit;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(reason));
            }
        }
    }
}
