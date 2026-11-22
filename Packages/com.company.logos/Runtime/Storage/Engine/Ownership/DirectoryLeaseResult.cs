using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Logos.Storage
{
    // 取得租约时返回可释放的所有权对象；其余状态只说明争用、路径、平台能力或 I/O 问题。
    public sealed class DirectoryLeaseResult
    {
        public DirectoryLeaseStatus Status
        {
            get;
        }
        public IDirectoryLease Lease
        {
            get;
        }

        internal DirectoryLeaseResult(DirectoryLeaseStatus status, IDirectoryLease lease = null)
        {
            Status = status;
            Lease = lease;
        }
    }
}
