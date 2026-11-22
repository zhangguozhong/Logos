using System;

namespace Logos.Storage
{
    // 保留窗口包含当前确认日，向前保留其余完整 UTC 日；计算过期边界时需避免把当天提前淘汰。
    public static class RetentionPolicy
    {
        /// <summary>调用者提供显式确认的 UTC 日期，不使用暂定墙上时间。</summary>
        public static bool IsExpired(long day, long explicitlyConfirmedDay, int retentionDays)
        {
            if (day < 0 || explicitlyConfirmedDay < 0 || retentionDays < 1 || retentionDays > 1024)
                throw new ArgumentOutOfRangeException();
            return day <= explicitlyConfirmedDay - retentionDays;
        }
    }
}
