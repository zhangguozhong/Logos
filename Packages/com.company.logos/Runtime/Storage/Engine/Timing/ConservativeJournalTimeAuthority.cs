using System;
using System.Diagnostics;
namespace Logos.Storage
{
    // 每个样本同时携带 UTC 和本实例秒表的经过时间，供日期策略比较墙上时间增量与单调时间增量。
    /// <summary>本地墙上时间未经确认；JournalDayClock 提供持久日期和连续时间策略。</summary>
    public sealed class ConservativeJournalTimeAuthority : IJournalTimeAuthority
    {
        private readonly Stopwatch monotonic = new Stopwatch();
        public ConservativeJournalTimeAuthority()
        {
            monotonic.Start();
        }
        public JournalTimeObservation Observe() => new JournalTimeObservation(DateTime.UtcNow, monotonic.Elapsed.Ticks);
    }
}
