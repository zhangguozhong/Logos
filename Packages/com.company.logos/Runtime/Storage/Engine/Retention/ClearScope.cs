using System;

namespace Logos.Storage
{
    // 显式日期集合限定清理目标；全目录请求还可能覆盖未知文件。按身份删除需要重写混合内容，因此不在此范围内。
    public enum ClearStatus
    {
        None,
        Pending,
        Completed,
        Failed,
    }

    public enum ClearRequestResult
    {
        Pending,
        Completed,
        Failed,
        UnsupportedScope,
        Busy,
    }

    /// <summary>整日、全身份范围；不选择性重写混合身份文件。</summary>
    public sealed class ClearScope
    {
        internal readonly byte Kind;
        internal readonly long[] Days;
        public static ClearScope AllData
        {
            get;
        }
        = new ClearScope(1, Array.Empty<long>());
        public static ClearScope UnsupportedIdentityScope
        {
            get;
        }
        =
            new ClearScope(2, Array.Empty<long>());

        private ClearScope(byte kind, long[] days)
        {
            Kind = kind;
            Days = days;
        }

        public static ClearScope ExplicitWholeDays(params long[] days)
        {
            if (days == null || days.Length > 1024)
                throw new ArgumentOutOfRangeException(nameof(days));
            var copy = (long[])days.Clone();
            Array.Sort(copy);
            for (int i = 0; i < copy.Length; i++)
                if (
                    copy[i] < 0
                    || copy[i] > DateTime.MaxValue.Ticks / TimeSpan.TicksPerDay
                    || (i > 0 && copy[i] == copy[i - 1])
                )
                    throw new ArgumentOutOfRangeException(nameof(days));
            return new ClearScope(0, copy);
        }

        internal bool Contains(long day) => Kind == 1 || Array.BinarySearch(Days, day) >= 0;
    }

    /// <summary>C 将提供准入和暂存协调。B2 仅清理自身引擎资源。</summary>
    public interface IClearMemoryBarrier
    {
        void CloseTargets(ClearScope scope);
        bool ClearOwnedMemory(long cleanupGeneration);
    }
}
