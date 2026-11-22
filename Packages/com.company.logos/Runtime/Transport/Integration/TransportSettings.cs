using System;
using System.Threading;
using UnityEngine;
namespace Logos.Transport
{
    /// <summary>即使禁用自动上传，也必须显式配置有限网络预算和自动导出边界。</summary>
    [CreateAssetMenu(menuName = "Logos/Transport Settings", fileName = "TransportSettings")]
    public sealed class TransportSettings : ScriptableObject
    {
        [SerializeField] private long maxDailyUploadBytes, errorFatalReservedBytesPerDay, automaticSourceBytes, automaticEncodedBytes;
        [SerializeField] private int automaticBatches;
        [SerializeField] private bool autoErrorEnabled = true;
        public long MaxDailyUploadBytes
        {
            get => maxDailyUploadBytes;
            set => maxDailyUploadBytes = value;
        }
        public long ErrorFatalReservedBytesPerDay
        {
            get => errorFatalReservedBytesPerDay;
            set => errorFatalReservedBytesPerDay = value;
        }
        public long AutomaticSourceBytes
        {
            get => automaticSourceBytes;
            set => automaticSourceBytes = value;
        }
        public long AutomaticEncodedBytes
        {
            get => automaticEncodedBytes;
            set => automaticEncodedBytes = value;
        }
        public int AutomaticBatches
        {
            get => automaticBatches;
            set => automaticBatches = value;
        }
        public bool AutoErrorEnabled
        {
            get => autoErrorEnabled;
            set => autoErrorEnabled = value;
        }
        // 在主线程将序列化字段转成有限额的策略，工作线程随后只读取这份快照。
        internal bool TryCreateSnapshot(int mainThreadId, out TransferPolicy policy)
        {
            policy = null;
            if (mainThreadId <= 0 || Thread.CurrentThread.ManagedThreadId != mainThreadId)
                return false;
            try
            {
                var limits = new ExportLimits(automaticSourceBytes, automaticEncodedBytes, automaticBatches);
                policy = new TransferPolicy(maxDailyUploadBytes, errorFatalReservedBytesPerDay, limits, autoErrorEnabled);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
    }
}
