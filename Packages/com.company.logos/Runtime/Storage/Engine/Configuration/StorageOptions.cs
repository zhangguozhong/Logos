using System;
using System.Numerics;

namespace Logos.Storage
{
    // 保留天数受固定日期槽数限制；即使单日配额合法，目录总量仍可能不足以容纳整个保留窗口。
    /// <summary>配置保留策略及可选文件映射。Freeze 是纯操作，不执行 I/O。</summary>
    public sealed class StorageOptions
    {
        public const long MetadataBudgetBytes = 2 * 1024 * 1024;
        public long MaxDailyFileBytes
        {
            get;
            set;
        }
        = 8 * 1024 * 1024;
        public long ErrorFatalReservedBytesPerDay
        {
            get;
            set;
        }
        = 2 * 1024 * 1024;
        public int RetentionDays
        {
            get;
            set;
        }
        = 7;
        public long MaxDirectoryBytes
        {
            get;
            set;
        }
        = 64 * 1024 * 1024;

        public bool EnableMappedSealedBuffer { get; set; }

        public FrozenStorageOptions Freeze()
        {
            long f = MaxDailyFileBytes,
                e = ErrorFatalReservedBytesPerDay,
                d = MaxDirectoryBytes;
            int r = RetentionDays;
            if (r < 1 || r > 1024)
                throw new ArgumentOutOfRangeException(
                    nameof(RetentionDays),
                    "UnsupportedRetentionSpan: require 1..1024 UTC days."
                );
            if (f <= 0 || e <= 0 || e >= f || d <= MetadataBudgetBytes)
                throw new ArgumentException("Invalid storage partitions.");
            long b = d - MetadataBudgetBytes;
            // 此处 BigInteger 最多有 128 个有效位，与目录内容无关。
            long directoryError = (long)BigInteger.Min((BigInteger)e * r, (BigInteger)b * e / f);
            long directoryNormal = b - directoryError;
            if (
                e < StorageRuntimeLimits.MinWireBlockBytes
                || f - e
                    < StorageRuntimeLimits.FileHeaderBytes + StorageRuntimeLimits.MinWireBlockBytes
                || directoryError < StorageRuntimeLimits.MinWireBlockBytes
                || directoryNormal
                    < StorageRuntimeLimits.FileHeaderBytes + StorageRuntimeLimits.MinWireBlockBytes
            )
                throw new ArgumentException(
                    "Each partition must fit its minimum valid block; the file header belongs to normal quota."
                );
            return new FrozenStorageOptions(
                f,
                e,
                r,
                d,
                directoryError,
                directoryNormal,
                (BigInteger)d < (BigInteger)f * r + MetadataBudgetBytes,
                EnableMappedSealedBuffer
            );
        }
    }
}
