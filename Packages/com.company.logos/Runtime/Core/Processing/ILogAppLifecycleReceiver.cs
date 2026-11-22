namespace Logos
{
    /// <summary>可选的非阻塞标记，由所属 Unity 线程调用。实现不得在此执行 I/O。</summary>
    public interface ILogAppLifecycleReceiver
    {
        void RequestBackgroundSeal();
        void RequestControlCheck();
    }
}
