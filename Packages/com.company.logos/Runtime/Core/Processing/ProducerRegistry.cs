using System.Threading;
namespace Logos
{
    // 注册仅使用原子操作；工作线程最终清理不进入生产者的生命周期锁。
    internal sealed class ProducerRegistry
    {
        private int count;
        // 登记期也覆盖尚未取得队列预留的生产者，因此清理时仅检查队列的 ReservedCount 并不足够。
        internal int Count => Volatile.Read(ref count);
        internal void Enter() => Interlocked.Increment(ref count);
        internal void Exit() => Interlocked.Decrement(ref count);
    }
}
