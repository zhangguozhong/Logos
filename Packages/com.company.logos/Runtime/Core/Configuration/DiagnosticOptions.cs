using System;
namespace Logos
{
    public sealed class DiagnosticOptions
    {
        public int Version
        {
            get;
            set;
        }
        = 1;
        public bool Enabled
        {
            get;
            set;
        }
        public bool AggregationEnabled
        {
            get;
            set;
        }
        public bool IncidentEnabled
        {
            get;
            set;
        }
        public bool BudgetEnabled
        {
            get;
            set;
        }
        internal void Validate()
        {
            if (Version != 1)
                throw new ArgumentException("Unknown diagnostic options version.");
        }
        // 当前配置只有值类型字段。复制后会话持有独立的开关组合，调用方稍后调整配置不会影响已启动的实例。
        internal DiagnosticOptions Freeze()
        {
            Validate();
            return (DiagnosticOptions)MemberwiseClone();
        }
    }
}
