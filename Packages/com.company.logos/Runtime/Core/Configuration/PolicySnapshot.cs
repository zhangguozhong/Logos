using System;
namespace Logos
{
    public enum LogSource
    {
        Unknown, Unity, Application, Plugin
    }
    internal readonly struct SourcePolicy
    {
        public bool Enabled
        {
            get;
        }
        public LogLevel MinimumLevel
        {
            get;
        }
        public SourcePolicy(bool enabled, LogLevel minimumLevel)
        {
            if (minimumLevel < LogLevel.Trace || minimumLevel > LogLevel.Off)
                throw new ArgumentOutOfRangeException(nameof(minimumLevel));
            Enabled = enabled;
            MinimumLevel = minimumLevel;
        }
    }
    internal sealed class PolicySnapshot
    {
        public const int SourceCount = 4;
        private readonly SourcePolicy[] sources;
        public bool Enabled
        {
            get;
        }
        public LogLevel MinimumLevel
        {
            get;
        }
        public PolicySnapshot(bool enabled = true, LogLevel minimumLevel = LogLevel.Info, SourcePolicy[] sourceRules = null)
        {
            if (minimumLevel < LogLevel.Trace || minimumLevel > LogLevel.Off)
                throw new ArgumentOutOfRangeException(nameof(minimumLevel));
            if (sourceRules != null && sourceRules.Length != SourceCount)
                throw new ArgumentException("Exactly four fixed source rules required.", nameof(sourceRules));
            Enabled = enabled;
            MinimumLevel = minimumLevel;
            sources = sourceRules == null ? DefaultSourceRules() : (SourcePolicy[])sourceRules.Clone();
        }
        public static SourcePolicy[] DefaultSourceRules()
        {
            var rules = new SourcePolicy[SourceCount];
            for (int i = 0; i < rules.Length; i++)
                rules[i] = new SourcePolicy(true, LogLevel.Trace);
            return rules;
        }
        public SourcePolicy GetSource(LogSource source) => sources[SourceIndex(source)];
        // 未定义的来源统一使用 Unknown 的固定下标；来源规则和速率桶通过同一映射查找。
        internal static int SourceIndex(LogSource source) => (int)source >= 0 && (int)source < SourceCount ? (int)source : 0;
    }
}
