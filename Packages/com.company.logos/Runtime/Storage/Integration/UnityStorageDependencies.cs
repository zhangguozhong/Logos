namespace Logos.Storage
{
    // 有意保持内部可见：保留设置不暴露文件系统或所有权策略选项。
    // 注入同一目录所用的文件系统和租约提供者，使测试能够独立控制磁盘故障及所有权争用。
    internal sealed class UnityStorageDependencies
    {
        internal string Directory
        {
            get;
        }
        internal IJournalFileSystem FileSystem
        {
            get;
        }
        internal IDirectoryLeaseProvider Leases
        {
            get;
        }
        internal UnityStorageDependencies(string directory, IJournalFileSystem fileSystem = null, IDirectoryLeaseProvider leases = null)
        {
            Directory = directory;
            FileSystem = fileSystem;
            Leases = leases;
        }
    }
}
