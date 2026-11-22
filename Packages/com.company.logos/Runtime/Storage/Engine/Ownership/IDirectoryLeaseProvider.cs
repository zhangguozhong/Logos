using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Logos.Storage
{
    // 启动阶段先请求目录所有权；只有成功取得租约的会话才能进入文件与密钥恢复阶段。
    public interface IDirectoryLeaseProvider
    {
        DirectoryLeaseResult TryAcquire(string path);
    }
}
