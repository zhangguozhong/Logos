using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Logos.Storage
{
    // 原生句柄保留锁定对象；通过路径和句柄读取文件身份，能发现同名目录或锁文件已被另一个对象替换。
    internal static class NativeLock
    {
        internal static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        private static bool Mac => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
        // Intel 与 Rosetta 进程的无后缀 Darwin 符号返回旧版 inode 布局。
        internal static readonly bool MacInode64 = Mac && RuntimeInformation.ProcessArchitecture == Architecture.X64;
        private static bool DarwinAbi =>
#if UNITY_IOS && !UNITY_EDITOR
            true;
#else
            Mac;
#endif
        internal static bool IsSupported =>
            IsWindows || Mac || RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
#if UNITY_ANDROID || UNITY_IOS
            || true
#endif
        ;
#if UNITY_IOS && !UNITY_EDITOR
        private const string PosixLibrary = "__Internal";
#elif UNITY_ANDROID && !UNITY_EDITOR
        private const string PosixLibrary = "libc";
#else
        private const string PosixLibrary = "libc";
#endif

        [DllImport(PosixLibrary, EntryPoint = "open", SetLastError = true)]
        private static extern int OpenUnix(string path, int flags);

        [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "open", SetLastError = true)]
        private static extern int OpenMac(string path, int flags);

        internal static SafeHandle OpenLock(string path)
        {
            if (!File.Exists(path))
            {
                try
                {
                    using (
                        var created = new FileStream(
                            path,
                            FileMode.CreateNew,
                            FileAccess.ReadWrite,
                            FileShare.ReadWrite
                        )
                    )
                    {
                    }
                }
                catch (IOException)
                {
                    if (!File.Exists(path))
                        throw;
                }
            }
            // 必须原子设置 close-on-exec：无关子进程绝不能继承所有权。
            int flags = 2 | (DarwinAbi ? 0x01000000 : 0x00080000);
            int fd = Mac ? OpenMac(path, flags) : OpenUnix(path, flags);
            if (fd < 0)
                throw new IOException("Owner lock open failed.");
            return new PosixOwnerHandle(fd);
        }

        [DllImport(PosixLibrary, EntryPoint = "close", SetLastError = true)]
        private static extern int CloseUnix(int fd);

        [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "close", SetLastError = true)]
        private static extern int CloseMac(int fd);

        private sealed class PosixOwnerHandle : SafeHandle
        {
            internal PosixOwnerHandle(int fd)
                : base(new IntPtr(-1), true)
            {
                SetHandle(new IntPtr(fd));
            }

            public override bool IsInvalid => handle.ToInt64() == -1;

            protected override bool ReleaseHandle()
            {
                return (Mac ? CloseMac(handle.ToInt32()) : CloseUnix(handle.ToInt32())) == 0;
            }
        }

        [DllImport(PosixLibrary, EntryPoint = "flock", SetLastError = true)]
        private static extern int FlockUnix(int fd, int operation);

        [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "flock", SetLastError = true)]
        private static extern int FlockMac(int fd, int operation);

#if UNITY_IOS && !UNITY_EDITOR
        [DllImport(PosixLibrary, EntryPoint = "logos_ios_realpath", SetLastError = true)]
#else
        [DllImport(PosixLibrary, EntryPoint = "realpath", SetLastError = true)]
#endif
        private static extern IntPtr RealpathUnix(string path, IntPtr output);

        [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "realpath", SetLastError = true)]
        private static extern IntPtr RealpathMac(string path, IntPtr output);

        [DllImport(PosixLibrary, EntryPoint = "stat", SetLastError = true)]
        private static extern int StatUnix(string path, byte[] output);

        [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "stat", SetLastError = true)]
        private static extern int StatMac(string path, byte[] output);

        [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "stat$INODE64", SetLastError = true)]
        private static extern int StatMacInode64(string path, byte[] output);

        // 固定路径预先编码为以零结尾的 UTF-8；避免每次 stat 的字符串封送分配。
        [DllImport(PosixLibrary, EntryPoint = "stat", SetLastError = true)]
        private static extern int StatUnixEncoded(byte[] path, byte[] output);

        [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "stat", SetLastError = true)]
        private static extern int StatMacEncoded(byte[] path, byte[] output);

        [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "stat$INODE64", SetLastError = true)]
        private static extern int StatMacEncodedInode64(byte[] path, byte[] output);

        [DllImport(PosixLibrary, EntryPoint = "fstat", SetLastError = true)]
        private static extern int FstatUnix(int fd, byte[] output);

        [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "fstat", SetLastError = true)]
        private static extern int FstatMac(int fd, byte[] output);

        [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "fstat$INODE64", SetLastError = true)]
        private static extern int FstatMacInode64(int fd, byte[] output);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandleW(
            SafeFileHandle handle,
            System.Text.StringBuilder output,
            uint length,
            uint flags
        );

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(
            SafeFileHandle handle,
            out FileInfo info
        );

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(
            string path,
            uint access,
            uint share,
            IntPtr security,
            uint disposition,
            uint flags,
            IntPtr template
        );

        [StructLayout(LayoutKind.Sequential)]
        private struct FileInfo
        {
            public uint Attributes,
                CreationLow,
                CreationHigh,
                AccessLow,
                AccessHigh,
                WriteLow,
                WriteHigh,
                Volume,
                SizeHigh,
                SizeLow,
                Links,
                IndexHigh,
                IndexLow;
        }

        internal static bool TryLock(SafeHandle handle)
        {
            int result = Mac
                ? FlockMac(handle.DangerousGetHandle().ToInt32(), 6)
                : FlockUnix(handle.DangerousGetHandle().ToInt32(), 6);
            if (result == 0)
                return true;
            int error = Marshal.GetLastWin32Error();
            if (error == 11 || error == 35)
                return false;
            if (error == 38 || error == 45 || error == 95)
                throw new NotSupportedException("OS advisory lock unsupported.");
            throw new IOException("OS owner lock failed.");
        }

        internal static string Canonicalize(string path)
        {
            if (IsWindows)
            {
                using (var h = OpenPath(path, true))
                {
                    var b = new System.Text.StringBuilder(32768);
                    uint n = GetFinalPathNameByHandleW(h, b, (uint)b.Capacity, 0);
                    if (n == 0 || n >= b.Capacity)
                        throw new IOException("Canonical path unavailable.");
                    string p = b.ToString();
                    return p.StartsWith("\\\\?\\UNC\\", StringComparison.Ordinal)
                            ? "\\\\" + p.Substring(8)
                        : p.StartsWith("\\\\?\\", StringComparison.Ordinal) ? p.Substring(4)
                        : p;
                }
            }
            IntPtr buffer = Marshal.AllocHGlobal(4096);
            try
            {
                IntPtr p = Mac ? RealpathMac(path, buffer) : RealpathUnix(path, buffer);
                if (p == IntPtr.Zero)
                    throw new IOException("Canonical path unavailable.");
                return Marshal.PtrToStringAnsi(p);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static SafeFileHandle OpenPath(string path, bool directory)
        {
            var h = CreateFileW(
                path,
                0,
                7,
                IntPtr.Zero,
                3,
                directory ? 0x02000000u : 0u,
                IntPtr.Zero
            );
            if (h.IsInvalid)
            {
                h.Dispose();
                throw new IOException("Filesystem identity unavailable.");
            }
            return h;
        }

        internal static byte[] PathIdentity(string path, bool directory)
        {
            if (IsWindows)
            {
                using (var h = OpenPath(path, directory))
                    return HandleIdentity(h);
            }
            byte[] b = new byte[512];
            int r = MacInode64 ? StatMacInode64(path, b) : Mac ? StatMac(path, b) : StatUnix(path, b);
            if (r != 0)
                throw new IOException("Filesystem identity unavailable.");
            return Extract(b);
        }

        // 工作区由租约独占并在校验锁内使用；只复用内存，不缓存 stat 的结果。
        internal static bool MatchesPathIdentity(byte[] encodedPath, byte[] scratch, byte[] expected)
        {
            if (IntPtr.Size != 8)
                throw new NotSupportedException("Unverified native stat ABI.");
            int result = MacInode64 ? StatMacEncodedInode64(encodedPath, scratch)
                : Mac ? StatMacEncoded(encodedPath, scratch) : StatUnixEncoded(encodedPath, scratch);
            if (result != 0)
                throw new IOException("Filesystem identity unavailable.");
            int deviceBytes = DarwinAbi ? 4 : 8;
            int diff = 0;
            for (int i = 0; i < 16; i++)
            {
                byte actual = i < deviceBytes || i >= 8 ? scratch[i] : (byte)0;
                diff |= actual ^ expected[i];
            }
            return diff == 0;
        }

        internal static byte[] HandleIdentity(SafeHandle h)
        {
            if (IsWindows)
            {
                if (!GetFileInformationByHandle((SafeFileHandle)h, out var info))
                    throw new IOException("Filesystem identity unavailable.");
                var b = new byte[12];
                Buffer.BlockCopy(BitConverter.GetBytes(info.Volume), 0, b, 0, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(info.IndexHigh), 0, b, 4, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(info.IndexLow), 0, b, 8, 4);
                return b;
            }
            byte[] data = new byte[512];
            int fd = h.DangerousGetHandle().ToInt32();
            int r = MacInode64 ? FstatMacInode64(fd, data) : Mac ? FstatMac(fd, data) : FstatUnix(fd, data);
            if (r != 0)
                throw new IOException("Filesystem identity unavailable.");
            return Extract(data);
        }

        // Darwin 的 dev_t 为 32 位，Linux 为 64 位；已验证的 64 位 ABI 中 inode 从字节 8 开始。
        private static byte[] Extract(byte[] data)
        {
            if (IntPtr.Size != 8)
                throw new NotSupportedException("Unverified native stat ABI.");
            byte[] id = new byte[16];
            Buffer.BlockCopy(data, 0, id, 0, DarwinAbi ? 4 : 8);
            Buffer.BlockCopy(data, 8, id, 8, 8);
            return id;
        }
    }
}
