using System;
using System.Threading;
using UnityEngine;
namespace Logos
{
    // 生命周期所有者在捕获的 Unity 主线程上构造、安装和卸载。
    internal sealed class UnityLogCollector
    {
        private readonly LoggingPipeline pipeline;
        private readonly int mainThreadId;
        private Registration registration;
        internal UnityLogCollector(LoggingPipeline pipeline, int mainThreadId)
        {
            this.pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
            if (mainThreadId <= 0)
                throw new ArgumentOutOfRangeException(nameof(mainThreadId));
            this.mainThreadId = mainThreadId;
        }
        internal bool Install()
        {
            if (Thread.CurrentThread.ManagedThreadId != mainThreadId)
                return false;
            if (registration != null)
                return registration.IsActive;
            if (pipeline.Completion != PipelineCompletion.Running)
                return false;
            var next = new Registration(pipeline);
            try
            {
                Application.logMessageReceivedThreaded += next.Receive;
                registration = next;
                return true;
            }
            catch (Exception)
            {
                next.Deactivate();
                return false;
            }
        }
        internal bool Uninstall()
        {
            if (Thread.CurrentThread.ManagedThreadId != mainThreadId)
                return false;
            var current = registration;
            if (current == null)
                return true;
            // 处理中的回调可能已进入 Core；资源屏障由 Core 负责。
            // 本次安装捕获的委托即使在后续 Install 之后也保持禁用。
            current.Deactivate();
            try
            {
                Application.logMessageReceivedThreaded -= current.Receive;
                registration = null;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
        private sealed class Registration
        {
            private readonly LoggingPipeline pipeline;
            private int active = 1;
            internal Registration(LoggingPipeline pipeline)
            {
                this.pipeline = pipeline;
            }
            internal bool IsActive => Volatile.Read(ref active) != 0;
            internal void Deactivate() => Volatile.Write(ref active, 0);
            internal void Receive(string message, string stack, LogType type)
            {
                if (!IsActive || !UnityLogTypeMapper.TryMap(type, out var level))
                    return;
                try
                {
                    // 线程回调直接使用本次注册捕获的流水线及代际。只有映射为 Info 的自动日志走重复折叠路径。
                    pipeline.TryAccept(level, LogSource.Unity, message, stack, expectedGeneration: pipeline.Generation, automaticInfo: level == LogLevel.Info);
                }
                catch (Exception)
                { /* 任何生产者线程上的 Unity 回调都不得输出日志或让异常逸出。 */
                }
            }
        }
    }
}
