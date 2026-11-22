using System;
using System.Threading;
using UnityEngine;

namespace Logos.Samples.BasicCapture
{
    /// <summary>一个显式的主线程组合根。仅演示内存处理。</summary>
    public sealed class BasicCaptureExample : MonoBehaviour
    {
        [SerializeField] private LogosSettings settings;
        private LogosSettings ownedSettings;
        private LogosSession session;
        private readonly SampleCountingProcessor processor = new SampleCountingProcessor();
        public bool StartupSucceeded
        {
            get;
            private set;
        }
        public long ConsumedForSample => processor.Consumed;

        private void Start()
        {
            // 只在没有指定资产时创建运行期配置。销毁组件时只释放这里创建的对象，项目中的配置资产由 Unity 管理。
            if (settings == null)
            {
                ownedSettings = ScriptableObject.CreateInstance<LogosSettings>();
                settings = ownedSettings;
            }
            StartupSucceeded = LogosSdk.TryInitialize(settings, processor, out session);
            if (!StartupSucceeded)
                return;

            UnityLogger.Trace("Sample trace", "sample.trace"); // 默认策略过滤 Trace/Debug。
            UnityLogger.Debug("Sample debug", "sample.debug");
            UnityLogger.Info("Sample info", "sample.info");
            UnityLogger.Warn("Sample warning", "sample.warn");
            UnityLogger.Error("Sample error", "sample.error");
            UnityLogger.Fatal("Sample fatal", "sample.fatal");
        }

        // 登录流程开始时保存身份版本，业务确认登录后才发布；登出时传 null。
        public UserUpdateResult UpdateUser(long expectedIdentityVersion, string diagnosticUserId, out long identityVersion)
        {
            var owner = session;
            if (owner == null)
            {
                identityVersion = 0;
                return UserUpdateResult.StaleSession;
            }
            return owner.UpdateUser(owner.Generation, expectedIdentityVersion, diagnosticUserId, out identityVersion);
        }

        // 显式观察会分配快照。不要每次记录日志都查询健康状态。
        public LoggingHealthSnapshot QueryHealth() => session?.GetHealthSnapshot();
        public void RequestStop() => LogosSdk.TryStopForUnload(session, TimeSpan.FromMilliseconds(100));
        public bool IsSafeToUnload => (session == null || session.IsSafeToUnload) && LogosSdk.IsSafeToUnload;
        private void OnDisable() => RequestStop();
        private void OnDestroy()
        {
            RequestStop(); // 此组件销毁后，持久 SDK 运行器仍继续 Poll。
            if (ownedSettings != null)
                Destroy(ownedSettings);
        }
    }

    // 工作线程只接收借用的托管值。不使用 Unity 对象、Debug 输出或异步保留。
    internal sealed class SampleCountingProcessor : ILogBatchProcessor
    {
        private long consumed;
        internal long Consumed => Interlocked.Read(ref consumed);
        public BatchProcessResult ProcessBatch(LogBatchView batch, BatchProcessingContext context)
        {
            // 返回值是已完成的连续前缀。中途到达截止时间时，剩余条目仍由调用方处理。
            var done = 0;
            while (done < batch.Count)
            {
                if (context.IsCancellationRequested)
                    return new BatchProcessResult(done, budgetReason: BatchBudgetReason.CancellationRequested);
                if (context.DeadlineReached)
                    return new BatchProcessResult(done, budgetReason: BatchBudgetReason.DeadlineReached);
                Interlocked.Increment(ref consumed);
                done++;
            }
            return new BatchProcessResult(done);
        }
    }
}
