using System.Collections.Generic;

namespace Logos.Storage
{
    // 请求保存范围与目标，后续工作步逐阶段发布持久意图并删除文件。中断后须根据已认证的阶段继续。
    /// <summary>有界持久化的整日清理状态；仅由会话所有者工作线程推进。</summary>
    public sealed class ClearCoordinator
    {
        private readonly JournalSession session;
        internal ClearScope Scope;
        internal long[] Targets;
        internal int TargetCount,
            ScanSlot,
            TargetIndex,
            Phase;
        internal JournalDayState Row;
        internal IEnumerator<string> UnknownEntries;
        internal bool NeedsResync;
        internal bool AppendResyncPending;

        /// <summary>两个自有日期 ID 数组、精确载荷及其读回和 SHA256。不含对象头。</summary>
        public int MaximumBufferPayloadBytes =>
            2 * 1024 * sizeof(long) + 2 * (64 + 1024 * sizeof(long)) + 32;
        public ClearStatus Status
        {
            get;
            internal set;
        }
        internal bool IsActive => Status == ClearStatus.Pending || Status == ClearStatus.Failed;

        internal ClearCoordinator(JournalSession session)
        {
            this.session = session;
        }

        public ClearRequestResult Request(ClearScope scope) => session.RequestClear(scope);

        public void Step(JournalStepContext context) => session.StepClear(context);
    }
}
