using System.Collections.Generic;
using System.IO;

namespace Logos.Storage
{
    // 目录枚举、属性读取和流操作都可由测试替换，以复现一次写入已部分生效、随后抛出异常的情况。
    /// <summary>仅由租约所有者调用。流暴露普通 Flush，不提供 fsync 语义。</summary>
    public interface IJournalFileSystem
    {
        bool Exists(string path);
        JournalEntryInfo GetEntryInfo(string path);
        void DeleteFile(string path);
        Stream Open(string path, FileMode mode, FileAccess access);
        IEnumerable<string> EnumerateEntries(string directory);
    }
}
