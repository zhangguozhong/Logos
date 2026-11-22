using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Logos.Storage
{
    // 争用表示另一个所有者仍持锁，平台不支持表示没有可用的锁实现，两者对启动重试有不同含义。
    public enum DirectoryLeaseStatus
    {
        Acquired,
        DirectoryBusy,
        UnsupportedPlatform,
        IoError,
        InvalidPath,
    }
}
