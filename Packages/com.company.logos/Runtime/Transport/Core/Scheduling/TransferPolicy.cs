using System;
namespace Logos.Transport
{
    internal sealed class TransferPolicy
    {
        internal const long WatchdogTicks = 30 * TimeSpan.TicksPerSecond, PollTicks = TimeSpan.TicksPerSecond / 10;
        internal long Total
        {
            get;
        }
        internal long Error
        {
            get;
        }
        internal ExportLimits AutoLimits
        {
            get;
        }
        internal ExportLimits ConfiguredAutoLimits
        {
            get;
        }
        internal TransferPolicy(long total, long error)
        : this(total, error, null)
        {
        }
        internal TransferPolicy(long total, long error, ExportLimits automaticLimits)
        : this(total, error, automaticLimits, true)
        {
        }
        internal TransferPolicy(long total, long error, ExportLimits automaticLimits, bool autoErrorEnabled)
        {
            ConfiguredAutoLimits = automaticLimits;
            AutoLimits = autoErrorEnabled ? automaticLimits : null;
            if (total <= 0 || error <= 0 || error >= total)
                throw new ArgumentException("Finite positive network budgets required.");
            Total = total;
            Error = error;
        }
        internal long RetryTicks(int attempt, ulong generation)
        {
            if (attempt < 1 || attempt > 5)
                throw new ArgumentOutOfRangeException(nameof(attempt));
            // 以尝试次数计算指数退避，用任务代数给延迟增加可复现的小幅偏移。
            long basis = (1L << attempt) * TimeSpan.TicksPerSecond;
            return Math.Min(120 * TimeSpan.TicksPerSecond, basis + (long)(generation % 5) * basis / 20);
        }
        internal long ThrottleTicks(int attempt, ulong generation, int retryAfterSeconds)
        {
            if (retryAfterSeconds < 0 || retryAfterSeconds > 3600)
                throw new ArgumentOutOfRangeException(nameof(retryAfterSeconds));
            return Math.Max(RetryTicks(attempt, generation), retryAfterSeconds * TimeSpan.TicksPerSecond);
        }
    }
}
