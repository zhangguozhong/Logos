namespace Logos
{
    // 使用 TimeSpan tick（100 ns）；实现必须快速，且不得调用外部回调。
    internal interface IMonotonicClock
    {
        long TimestampTicks
        {
            get;
        }
    }
}
