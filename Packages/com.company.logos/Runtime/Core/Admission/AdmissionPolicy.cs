using System;
using System.Threading;
namespace Logos
{
    internal enum PolicyDecision
    {
        InvalidInput, Filtered, Allow
    }
    internal sealed class AdmissionPolicy
    {
        private PolicySnapshot current;
        public AdmissionPolicy(PolicySnapshot snapshot)
        {
            current = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        }
        public PolicySnapshot Snapshot => Volatile.Read(ref current);
        // 一次交换发布整份策略。同一次 Evaluate 使用传入的快照，因此全局门槛和来源规则来自同一版配置。
        public void Replace(PolicySnapshot snapshot)
        {
            Interlocked.Exchange(ref current, snapshot ?? throw new ArgumentNullException(nameof(snapshot)));
        }
        // 仅作提示：不消耗令牌、不构造记录，也不预留容量。
        public bool IsEnabled(LogLevel level, LogSource source) => Evaluate(level, source) == PolicyDecision.Allow;
        public PolicyDecision Evaluate(LogLevel level, LogSource source) => Evaluate(Snapshot, level, source);
        public PolicyDecision Evaluate(PolicySnapshot snapshot, LogLevel level, LogSource source)
        {
            if (snapshot == null)
                throw new ArgumentNullException(nameof(snapshot));
            if (level == LogLevel.Off)
                return PolicyDecision.Filtered;
            if (level < LogLevel.Trace || level > LogLevel.Fatal)
                return PolicyDecision.InvalidInput;
            SourcePolicy rule = snapshot.GetSource(source);
            return snapshot.Enabled && level >= snapshot.MinimumLevel && rule.Enabled && level >= rule.MinimumLevel
                ? PolicyDecision.Allow : PolicyDecision.Filtered;
        }
        public int EstimateRecordBytes(LoggingOptions options, int messageChars, int stackChars, DiagnosticRecordMetadata metadata = null)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            if (messageChars < 0 || stackChars < 0)
                throw new ArgumentOutOfRangeException("Text lengths cannot be negative.");
            return RecordMemoryCost.Estimate(Math.Min(messageChars, options.MaxMessageChars), Math.Min(stackChars, options.MaxStackChars), metadata);
        }
    }
}
