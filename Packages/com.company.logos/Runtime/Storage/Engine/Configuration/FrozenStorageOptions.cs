using System;
using System.Numerics;

namespace Logos.Storage
{
    // 目录上限拆出元数据、普通日志和错误日志三部分；快照同时保留原始日策略，供新日志日创建文件头。
    public sealed class FrozenStorageOptions
    {
        public long MaxDailyFileBytes
        {
            get;
        }
        public long ErrorFatalReservedBytesPerDay
        {
            get;
        }
        public int RetentionDays
        {
            get;
        }
        public long MaxDirectoryBytes
        {
            get;
        }
        public long DirectoryErrorBytes
        {
            get;
        }
        public long DirectoryNormalBytes
        {
            get;
        }
        public bool MayStopBeforeRetentionWindow
        {
            get;
        }

        internal FrozenStorageOptions(
            long f,
            long e,
            int r,
            long d,
            long errors,
            long normal,
            bool risk
        )
        {
            MaxDailyFileBytes = f;
            ErrorFatalReservedBytesPerDay = e;
            RetentionDays = r;
            MaxDirectoryBytes = d;
            DirectoryErrorBytes = errors;
            DirectoryNormalBytes = normal;
            MayStopBeforeRetentionWindow = risk;
        }
    }
}
