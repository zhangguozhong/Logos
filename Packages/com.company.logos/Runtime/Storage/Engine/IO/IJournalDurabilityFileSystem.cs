using System.IO;
namespace Logos.Storage
{
    // 关闭流程可以区分已完成持久刷盘和平台不支持该操作，并将实际结果计入健康快照。
    public enum JournalForceSyncResult
    {
        Unsupported, Synchronized, Failed
    }
    /// <summary>可选的实际持久刷盘能力。普通 Flush 不实现此契约。</summary>
    public interface IJournalDurabilityFileSystem
    {
        JournalForceSyncResult ForceSync(Stream ownedStream);
    }
}
