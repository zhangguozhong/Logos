using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Logos.Storage
{
    // 会话通过 ValidateIdentity 核对路径仍指向取得所有权时的对象，并在最后一个实际资源关闭后释放租约。
    public interface IDirectoryLease : IDisposable
    {
        string CanonicalPath
        {
            get;
        }
        bool ValidateIdentity();
    }
}
