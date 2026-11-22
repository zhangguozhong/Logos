namespace Logos
{
    public enum LogStorageOutcomeKind
    {
        Committed, Dropped, RepresentedBySummary
    }
    public enum PipelineCompletionKind
    {
        SynchronousConsumedForTest, BufferedCommitted
    }
    // Sequence 标识待确认的这次结算。Counts 按输入等级计数，因此多条输入由一条摘要代表时也能分别归账。
    public readonly struct LogStorageOutcome
    {
        public ulong Sequence
        {
            get;
        }
        public LogStorageOutcomeKind Kind
        {
            get;
        }
        public AdmissionReason Reason
        {
            get;
        }
        public LogLevelCounts Counts
        {
            get;
        }
        public LogStorageOutcome(ulong sequence, LogStorageOutcomeKind kind, AdmissionReason reason, LogLevelCounts counts)
        {
            Sequence = sequence;
            Kind = kind;
            Reason = reason;
            Counts = counts;
        }
    }
}
