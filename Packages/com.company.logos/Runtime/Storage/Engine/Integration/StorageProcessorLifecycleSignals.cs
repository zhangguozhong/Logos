using System.Threading;
namespace Logos.Storage
{
    public sealed partial class StorageBatchProcessor
    {
        private int backgroundSealRequested, controlCheckRequested;
        private long nextBackgroundSeal;
        /// <summary>线程安全的合并请求。宿主也会唤醒已有 Core 工作线程；此处不执行 I/O。</summary>
        public void RequestBackgroundSeal()
        {
            Interlocked.Exchange(ref backgroundSealRequested, 1);
            Interlocked.Exchange(ref nextWake, 0);
        }
        /// <summary>线程安全的前台时间复查请求。仅在已有工作线程上进行观察。</summary>
        public void RequestControlCheck()
        {
            Interlocked.Exchange(ref controlCheckRequested, 1);
            Interlocked.Exchange(ref nextWake, 0);
        }
        // 工作线程在这里合并重复请求。前台信号提早下一次时间观察，后台信号在冷却允许时请求两个类别封块。
        private void ConsumeLifecycleSignals(long now)
        {
            if (Interlocked.Exchange(ref controlCheckRequested, 0) != 0)
                nextObservation = now;
            if (Volatile.Read(ref backgroundSealRequested) != 0 && now >= nextBackgroundSeal)
            {
                Interlocked.Exchange(ref backgroundSealRequested, 0);
                normal.RequestSeal(BlockSealReason.Background);
                error.RequestSeal(BlockSealReason.Background);
                nextBackgroundSeal = Add(now, StorageRuntimeLimits.FaultSyncIntervalTicks);
            }
        }
    }
}
