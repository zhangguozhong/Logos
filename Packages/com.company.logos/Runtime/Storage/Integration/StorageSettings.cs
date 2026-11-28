using System;
using System.Threading;
using UnityEngine;
namespace Logos.Storage
{
    // 将 Unity 资产中的保留参数复制并验证为不可变快照，工作线程随后读取快照，无需访问序列化资产。
    [CreateAssetMenu(menuName = "Logos/Storage Settings", fileName = "StorageSettings")]
    public sealed class StorageSettings : ScriptableObject
    {
        [SerializeField] private long maxDailyFileBytes = 8 * 1024 * 1024;
        [SerializeField] private long errorFatalReservedBytesPerDay = 2 * 1024 * 1024;
        [SerializeField] private int retentionDays = 7;
        [SerializeField] private long maxDirectoryBytes = 64 * 1024 * 1024;
        [SerializeField] private bool enableMappedSealedBuffer;

        public bool EnableMappedSealedBuffer
        {
            get => enableMappedSealedBuffer;
            set => enableMappedSealedBuffer = value;
        }

        public long MaxDailyFileBytes
        {
            get => maxDailyFileBytes;
            set => maxDailyFileBytes = value;
        }
        public long ErrorFatalReservedBytesPerDay
        {
            get => errorFatalReservedBytesPerDay;
            set => errorFatalReservedBytesPerDay = value;
        }
        public int RetentionDays
        {
            get => retentionDays;
            set => retentionDays = value;
        }
        public long MaxDirectoryBytes
        {
            get => maxDirectoryBytes;
            set => maxDirectoryBytes = value;
        }
        internal bool TryCreateSnapshot(int mainThreadId, out FrozenStorageOptions options)
        {
            options = null;
            if (mainThreadId <= 0 || Thread.CurrentThread.ManagedThreadId != mainThreadId)
                return false;
            try
            {
                options = new StorageOptions
                {
                    MaxDailyFileBytes = maxDailyFileBytes,
                    ErrorFatalReservedBytesPerDay = errorFatalReservedBytesPerDay,
                    RetentionDays = retentionDays,
                    MaxDirectoryBytes = maxDirectoryBytes,
                    EnableMappedSealedBuffer = enableMappedSealedBuffer
                }.Freeze();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
