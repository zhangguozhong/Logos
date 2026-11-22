namespace Logos
{
    // 严重性由低到高连续编号。策略通过大小比较判断最低等级，计数器通过等级值定位到六个固定槽。
    public enum LogLevel
    {
        Trace = 0,
        Debug = 1,
        Info = 2,
        Warn = 3,
        Error = 4,
        Fatal = 5,
        // 配置哨兵值；LogRecord 会拒绝此值。
        Off = 6
    }
}
