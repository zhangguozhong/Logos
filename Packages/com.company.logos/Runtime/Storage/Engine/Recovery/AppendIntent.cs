namespace Logos.Storage
{
    // 先于数据块写入日状态。它绑定原提交终点、预期结束位置和块身份，恢复时只能据此处理对应的一次追加。
    internal sealed class AppendIntent
    {
        internal long CleanupGeneration;
        internal long DayId;
        internal long Start;
        internal long ExpectedEnd;
        internal long PreviousCommittedEnd;
        internal bool IsErrorClass;
        internal byte[] Epoch = new byte[16];
        internal ulong BlockSequence;
        internal byte[] WireHash = new byte[32];
    }
}
