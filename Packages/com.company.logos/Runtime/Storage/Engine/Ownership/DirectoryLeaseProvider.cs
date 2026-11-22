using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Logos.Storage
{
    /// <summary>本地文件系统协作式所有者锁。永不删除或替换固定锁 inode。</summary>
    public sealed class DirectoryLeaseProvider : IDirectoryLeaseProvider
    {
        private readonly bool platformEnabled;

        public DirectoryLeaseProvider()
            : this(true)
        {
        }

        internal DirectoryLeaseProvider(bool platformEnabled)
        {
            this.platformEnabled = platformEnabled;
        }

        public DirectoryLeaseResult TryAcquire(string path)
        {
            IDisposable owned = null;
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                    return new DirectoryLeaseResult(DirectoryLeaseStatus.InvalidPath);
                string full = Path.GetFullPath(path);
                if (!platformEnabled || !NativeLock.IsSupported)
                    return new DirectoryLeaseResult(DirectoryLeaseStatus.UnsupportedPlatform);
                Directory.CreateDirectory(full);
                full = NativeLock.Canonicalize(full);
                string lockPath = Path.Combine(full, ".owner.lock");
                SafeHandle handle;
                if (NativeLock.IsWindows)
                {
                    var stream = new FileStream(
                        lockPath,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None
                    );
                    owned = stream;
                    handle = stream.SafeFileHandle;
                }
                else
                {
                    var native = NativeLock.OpenLock(lockPath);
                    owned = native;
                    handle = native;
                    if (!NativeLock.TryLock(handle))
                        return new DirectoryLeaseResult(DirectoryLeaseStatus.DirectoryBusy);
                }
                var lease = new DirectoryLease(full, owned, handle);
                owned = null;
                if (!lease.ValidateIdentity())
                {
                    lease.Dispose();
                    return new DirectoryLeaseResult(DirectoryLeaseStatus.IoError);
                }
                return new DirectoryLeaseResult(DirectoryLeaseStatus.Acquired, lease);
            }
            catch (ArgumentException)
            {
                return new DirectoryLeaseResult(DirectoryLeaseStatus.InvalidPath);
            }
            catch (NotSupportedException)
            {
                return new DirectoryLeaseResult(DirectoryLeaseStatus.UnsupportedPlatform);
            }
            catch (DllNotFoundException)
            {
                return new DirectoryLeaseResult(DirectoryLeaseStatus.UnsupportedPlatform);
            }
            catch (EntryPointNotFoundException)
            {
                return new DirectoryLeaseResult(DirectoryLeaseStatus.UnsupportedPlatform);
            }
            catch (IOException ex)
            {
                return new DirectoryLeaseResult(
                    NativeLock.IsWindows
                    && ((ex.HResult & 0xffff) == 32 || (ex.HResult & 0xffff) == 33)
                        ? DirectoryLeaseStatus.DirectoryBusy
                        : DirectoryLeaseStatus.IoError
                );
            }
            catch (UnauthorizedAccessException)
            {
                return new DirectoryLeaseResult(DirectoryLeaseStatus.IoError);
            }
            finally
            {
                owned?.Dispose();
            }
        }

        private sealed class DirectoryLease : IDirectoryLease
        {
            private IDisposable owned;
            private readonly object identityGate = new object();
            private readonly string lockPath;
            private readonly byte[] directoryPathUtf8, lockPathUtf8, identityScratch;
            private readonly SafeHandle handle;
            private readonly byte[] directoryIdentity,
                lockIdentity;
            public string CanonicalPath
            {
                get;
            }

            internal DirectoryLease(string path, IDisposable owned, SafeHandle handle)
            {
                CanonicalPath = path;
                this.owned = owned;
                this.handle = handle;
                lockPath = Path.Combine(path, ".owner.lock");
                if (!NativeLock.IsWindows)
                {
                    directoryPathUtf8 = System.Text.Encoding.UTF8.GetBytes(path + "\0");
                    lockPathUtf8 = System.Text.Encoding.UTF8.GetBytes(lockPath + "\0");
                    identityScratch = new byte[512];
                }
                directoryIdentity = NativeLock.PathIdentity(path, true);
                lockIdentity = NativeLock.HandleIdentity(handle);
            }

            // 租约有效期内路径仍可能被替换；重新核对目录和锁文件身份后，才允许原所有者继续使用该路径。
            public bool ValidateIdentity()
            {
                // 公共租约可能被并发查询或释放，工作区不能跨调用交错使用。
                lock (identityGate)
                {
                    if (owned == null || handle.IsClosed || handle.IsInvalid)
                        return false;
                    try
                    {
                        if (!NativeLock.IsWindows)
                            return NativeLock.MatchesPathIdentity(directoryPathUtf8, identityScratch, directoryIdentity)
                                && NativeLock.MatchesPathIdentity(lockPathUtf8, identityScratch, lockIdentity);
                        return Equal(directoryIdentity, NativeLock.PathIdentity(CanonicalPath, true))
                            && Equal(lockIdentity, NativeLock.PathIdentity(lockPath, false));
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        return false;
                    }
                }
            }

            private static bool Equal(byte[] a, byte[] b)
            {
                if (a == null || b == null || a.Length != b.Length)
                    return false;
                int diff = 0;
                for (int i = 0; i < a.Length; i++)
                    diff |= a[i] ^ b[i];
                return diff == 0;
            }

            public void Dispose()
            {
                lock (identityGate)
                {
                    var resource = owned;
                    owned = null;
                    resource?.Dispose();
                }
            }
        }
    }
}
