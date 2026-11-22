using System.Diagnostics;

namespace Logos.Storage
{
    // 会话为统计写入提供当前控制密钥和所有者检查，使低频检查点与同一目录的其他写入共享所有权约束。
    public sealed partial class JournalSession
    {
        public DropStatisticsCheckpoint DropStatistics
        {
            get;
            private set;
        }

        public void StepStatistics(JournalStepContext context) =>
            StepStatisticsAt(context, Stopwatch.GetTimestamp());

        internal void StepStatisticsAt(JournalStepContext context, long timestamp)
        {
            CheckThread();
            if (
                store == null
                || controlKey == null
                || context.ShouldYield
                || Clear.IsActive
                || ControlKeyRotationStatus == ControlKeyRotationStatus.Pending
                || Status == JournalStatus.Disposed
            )
                return;
            VerifyOwner();
            DropStatistics.Step(store, keys, controlKey, context, timestamp);
        }
    }
}
