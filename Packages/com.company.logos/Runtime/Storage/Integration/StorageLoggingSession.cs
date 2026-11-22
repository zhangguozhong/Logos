namespace Logos.Storage
{
    // 将 Unity 生命周期所有者与实际存储处理器成对保存，宿主通过这个对象取得同一处理器发布的健康快照。
    public sealed class StorageLoggingSession : ILogosSession
    {
        public LogosSession UnitySession
        {
            get;
        }
        private readonly StorageBatchProcessor processor;
        internal readonly StorageConfiguration Configuration;
        internal StorageLoggingSession(LogosSession unitySession, StorageBatchProcessor processor, StorageConfiguration configuration)
        {
            UnitySession = unitySession;
            this.processor = processor;
            Configuration = configuration;
        }
        internal bool Owns(LogosSession owner) => ReferenceEquals(UnitySession, owner) && owner.UsesProcessor(processor);
        public StorageHealthSnapshot GetHealthSnapshot() => processor.GetHealthSnapshot();
    }
}
