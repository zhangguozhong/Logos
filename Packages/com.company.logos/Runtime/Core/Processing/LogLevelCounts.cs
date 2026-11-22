using System;
namespace Logos
{
    // 一条存储结果可结算多条输入记录，分别携带六个等级的数量，以便工作线程逐级核对已转移的所有权。
    public readonly struct LogLevelCounts
    {
        public long Trace
        {
            get;
        }
        public long Debug
        {
            get;
        }
        public long Info
        {
            get;
        }
        public long Warn
        {
            get;
        }
        public long Error
        {
            get;
        }
        public long Fatal
        {
            get;
        }
        public LogLevelCounts(long trace = 0, long debug = 0, long info = 0, long warn = 0, long error = 0, long fatal = 0)
        {
            if (trace < 0 || debug < 0 || info < 0 || warn < 0 || error < 0 || fatal < 0)
                throw new ArgumentOutOfRangeException();
            Trace = trace;
            Debug = debug;
            Info = info;
            Warn = warn;
            Error = error;
            Fatal = fatal;
        }
        public long Get(LogLevel level)
        {
            switch (level)
            {
                case LogLevel.Trace:
                    return Trace;
                case LogLevel.Debug:
                    return Debug;
                case LogLevel.Info:
                    return Info;
                case LogLevel.Warn:
                    return Warn;
                case LogLevel.Error:
                    return Error;
                case LogLevel.Fatal:
                    return Fatal;
                default:
                    throw new ArgumentOutOfRangeException(nameof(level));
            }
        }
    }
}
