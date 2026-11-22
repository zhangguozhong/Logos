using System.Collections.Generic;
using System.IO;

namespace Logos.Storage
{
    public sealed class SystemJournalFileSystem : IJournalFileSystem, IJournalDurabilityFileSystem
    {
        // 传入流必须支持文件级持久刷盘；结果区分同步成功与能力不可用，供停止流程报告。
        public JournalForceSyncResult ForceSync(Stream ownedStream)
        {
            if (!(ownedStream is FileStream file))
                return JournalForceSyncResult.Unsupported;
            try
            {
                file.Flush(true);
                return JournalForceSyncResult.Synchronized;
            }
            catch (System.PlatformNotSupportedException)
            {
                return JournalForceSyncResult.Unsupported;
            }
            catch (System.NotSupportedException)
            {
                return JournalForceSyncResult.Unsupported;
            }
            catch (IOException)
            {
                return JournalForceSyncResult.Failed;
            }
        }
        public bool Exists(string path) => File.Exists(path);

        public JournalEntryInfo GetEntryInfo(string path)
        {
            var info = new FileInfo(path);
            var attributes = info.Attributes;
            bool regular =
                (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;
            return new JournalEntryInfo(regular ? info.Length : 0, regular,
                (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == FileAttributes.Directory);
        }

        public void DeleteFile(string path)
        {
            if (!GetEntryInfo(path).IsRegularFile)
                throw new IOException("Only flat regular files may be removed.");
            File.Delete(path);
        }

        public Stream Open(string path, FileMode mode, FileAccess access) =>
            new FileStream(path, mode, access, FileShare.ReadWrite, 4096, FileOptions.None);

        public IEnumerable<string> EnumerateEntries(string directory) =>
            Directory.EnumerateFileSystemEntries(directory);
    }
}
