namespace Logos
{
    /// <summary>原子读取累计数值。不持有生命周期锁、不分配快照、不执行 I/O，也不回调日志系统。</summary>
    public interface ILogDiagnosticsSource
    {
        long GetCumulativeDiscarded(LogLevel level, AdmissionReason reason);
    }
    public interface ILogDiagnosticsReceiver
    {
        // 在工作线程启动前绑定一次；接收方不得在此调用期间查询。
        void BindDiagnostics(ILogDiagnosticsSource source);
    }
}
