using System;
using System.IO;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Logos.Storage
{
    // POSIX 创建、改名和删除都相对于固定目录描述符。Windows 目录句柄禁止删除共享，且直接打开重解析点。
    internal sealed class UploadWorkspace : IDisposable
    {
        readonly IDirectoryLease owner;
        SafeHandle root, folder;
        IEnumerator<string> orphanEntries;
        long retainedBytes;
        bool retainedEntries, incompleteMeasurement, unsafeEntry;
        internal string PathName { get; }
        internal bool Exists => folder != null;

        internal static bool SupportsAbi(bool windows, bool posix, int pointerSize, Architecture architecture) =>
            windows || (posix && pointerSize == 8 && (architecture == Architecture.X64 || architecture == Architecture.Arm64));

        internal static bool IsSupported => SupportsAbi(NativeLock.IsWindows, NativeLock.IsSupported,
            IntPtr.Size, RuntimeInformation.ProcessArchitecture);

        internal UploadWorkspace(IDirectoryLease owner, bool create)
        {
            // 在调用 openat/mkdirat 或解释 dirent/stat 之前拒绝未经验证的 ABI。
            if (!IsSupported)
                throw new IOException("Private upload workspace is unsupported on this ABI.");
            this.owner = owner;
            PathName = Path.Combine(owner.CanonicalPath, DirectoryQuota.UploadDirectoryName);
            try
            {
                root = PinDirectory(owner.CanonicalPath, null);
                Verify();
                if (NativeLock.IsWindows)
                {
                    if (create)
                        Directory.CreateDirectory(PathName);
                    folder = PinDirectory(PathName, root);
                }
                else
                {
                    if (create && MkdirAt(Fd(root), DirectoryQuota.UploadDirectoryName, 448) != 0
                        && Marshal.GetLastWin32Error() != 17)
                        throw new IOException("Cannot create private upload directory.");
                    folder = PinDirectory(DirectoryQuota.UploadDirectoryName, root);
                    if (create && folder != null && Fchmod(Fd(folder), 448) != 0)
                        throw new IOException("Cannot secure private upload directory.");
                }
                Verify();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        // 每次至多移除八个属于 SDK 的扁平条目。残留只参加清理，绝不作为上传输入。
        internal bool CleanOrphanPage(DirectoryQuota quota)
        {
            Verify();
            if (Exists)
            {
                if (orphanEntries == null)
                    orphanEntries = Names().GetEnumerator();
                int inspected = 0;
                while (inspected < 8)
                {
                    if (!orphanEntries.MoveNext())
                        break;
                    inspected++;
                    string name = orphanEntries.Current;
                    bool regular, singleLink;
                    long length;
                    try
                    {
                        regular = InspectEntry(name, out length, out singleLink);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        Verify();
                        retainedEntries = incompleteMeasurement = quota.UploadUsageUnknown = true;
                        continue;
                    }
                    if (regular && singleLink && IsOwnedName(name))
                    {
                        try
                        {
                            DeleteFile(name);
                            quota.SetUploadCharge(Math.Max(retainedBytes, quota.UploadBytes - length));
                            continue;
                        }
                        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                        {
                            // 目录身份变化时立即停止。单个文件无法删除时保留计费并继续检查兄弟条目。
                            Verify();
                        }
                    }
                    if (!regular)
                        unsafeEntry = quota.UploadUsageUnknown = true;
                    retainedEntries = true;
                    retainedBytes = checked(retainedBytes + length);
                    quota.SetUploadCharge(Math.Max(retainedBytes, quota.UploadBytes));
                }
                // 游标停在本页最后一项，下一次从它的后继继续。
                if (inspected == 8)
                    return false;
                Verify();
                if (retainedEntries)
                {
                    if (!incompleteMeasurement)
                        quota.SetUploadCharge(retainedBytes);
                    quota.UploadUsageUnknown = unsafeEntry || incompleteMeasurement;
                    throw new IOException("Upload workspace contains retained entries.");
                }
                using (var remaining = Names().GetEnumerator())
                    if (remaining.MoveNext())
                    {
                        orphanEntries.Dispose();
                        orphanEntries = null;
                        return false;
                    }
            }
            quota.ClearUploadCharge();
            return true;
        }

        bool InspectEntry(string name, out long length, out bool singleLink)
        {
            if (!NativeLock.IsWindows)
                return GetUnixFileInfo(Fd(folder), name, out length, out singleLink);
            using (var handle = CreateFileW(Path.Combine(PathName, name), 0x80, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero))
            {
                if (handle.IsInvalid || !GetFileInformationByHandleEx(handle, 9, out var tag, 8))
                    throw new IOException("Cannot inspect upload entry.");
                if ((tag.Attributes & 0x450) != 0)
                {
                    singleLink = false;
                    length = 0;
                    return false;
                }
                if (!GetStandardInfo(handle, 1, out var standard, (uint)Marshal.SizeOf<StandardInfo>()))
                    throw new IOException("Cannot inspect upload file.");
                singleLink = standard.Links == 1;
                length = standard.Length;
                return true;
            }
        }

        internal static IEnumerable<long> Inventory(IDirectoryLease owner)
        {
            using (var workspace = new UploadWorkspace(owner, false))
            {
                if (!workspace.Exists)
                    yield break;
                foreach (string name in workspace.Names())
                {
                    long length;
                    try
                    {
                        if (!workspace.InspectEntry(name, out length, out _))
                            length = -1;
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        workspace.Verify();
                        length = -1;
                    }
                    // 负值表示不能证明容量，例如未知子目录；继续清点剩余同级文件。
                    yield return length;
                }
            }
        }

        IEnumerable<string> Names()
        {
            if (NativeLock.IsWindows)
            {
                foreach (string path in Directory.EnumerateFileSystemEntries(PathName))
                    yield return Path.GetFileName(path);
                yield break;
            }
            // 打开独立的遍历游标；即使路径名称被替换，枚举也不会越过已固定的目录。
            int fd = OpenAt(Fd(folder), ".", CloseOnExec | (Darwin ? 0x100000 : 0x10000), 0);
            if (fd < 0)
                throw new IOException("Cannot open upload inventory.");
            var stream = FdOpenDir(fd);
            if (stream == IntPtr.Zero)
            {
                CloseFd(fd);
                throw new IOException("Cannot open upload inventory.");
            }
            IntPtr entry = IntPtr.Zero;
            try
            {
                entry = Marshal.AllocHGlobal(2048);
                while (true)
                {
                    if (ReadDir(stream, entry, out var result) != 0)
                        throw new IOException("Cannot read upload inventory.");
                    if (result == IntPtr.Zero)
                        yield break;
                    string name = Marshal.PtrToStringAnsi(IntPtr.Add(result, Darwin ? 21 : 19));
                    if (name != "." && name != "..")
                        yield return name;
                }
            }
            finally
            {
                if (entry != IntPtr.Zero)
                    Marshal.FreeHGlobal(entry);
                CloseDir(stream);
            }
        }

        static bool IsOwnedName(string name)
        {
            if (name.Length != 37 || !(name.EndsWith(".part", StringComparison.Ordinal)
                || name.EndsWith(".body", StringComparison.Ordinal)))
                return false;
            for (int i = 0; i < 32; i++)
                if (!(name[i] >= '0' && name[i] <= '9') && !(name[i] >= 'a' && name[i] <= 'f'))
                    return false;
            return true;
        }

        internal void Verify()
        {
            if (!owner.ValidateIdentity() || !Matches(root, owner.CanonicalPath)
                || (folder != null && !Matches(folder, PathName)))
                throw new IOException("Upload directory identity changed.");
        }

        static bool Matches(SafeHandle handle, string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                return false;
            var a = NativeLock.HandleIdentity(handle);
            var b = NativeLock.PathIdentity(path, true);
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i])
                    return false;
            return true;
        }

        internal Stream OpenFile(string name, bool write)
        {
            Verify();
            if (NativeLock.IsWindows)
            {
                string path = Path.Combine(PathName, name);
                if (write)
                    return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                // 先检查实际句柄上的重解析属性，验证后才允许读取。
                var handle = CreateFileW(path, 0x80000000, 1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
                try
                {
                    if (handle.IsInvalid || !OrdinaryWindowsHandle(handle, false))
                        throw new IOException("Upload file is unavailable.");
                    return new FileStream(handle, FileAccess.Read);
                }
                catch { handle.Dispose(); throw; }
            }
            // 读取端也拒绝特殊文件；非阻塞打开避免命名管道使停机永久等待。
            int flags = NoFollow | CloseOnExec | (write ? (Darwin ? 0x0a01 : 0xc1) : (Darwin ? 4 : 0x800));
            int fd = OpenAt(Fd(folder), name, flags, 384);
            if (fd < 0)
                throw new IOException("Cannot open private upload file.");
            try
            {
                if (write ? Fchmod(fd, 384) != 0 : !GetUnixFileInfo(fd, null, out _))
                    throw new IOException("Cannot secure private upload file.");
                return new UnixStream(fd, write);
            }
            catch
            {
                CloseFd(fd);
                throw;
            }
        }

        internal void Publish(string partial, string ready)
        {
            Verify();
            if (NativeLock.IsWindows)
            {
                string from = Path.Combine(PathName, partial);
                SetWindowsReadOnly(from, true);
                File.Move(from, Path.Combine(PathName, ready));
            }
            else if (RenameAt(Fd(folder), partial, Fd(folder), ready) != 0)
                throw new IOException("Cannot publish private upload file.");
        }

        internal void DeleteFile(string name)
        {
            Verify();
            string path = Path.Combine(PathName, name);
            if (!Exists)
                throw new IOException("Upload workspace no longer exists.");
            if (NativeLock.IsWindows)
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                    throw new IOException("Unrecognized upload directory entry.");
                if ((attributes & FileAttributes.ReadOnly) != 0)
                    SetWindowsReadOnly(path, false);
                File.Delete(path);
            }
            else if (!GetUnixFileInfo(Fd(folder), name, out _) || UnlinkAt(Fd(folder), name, 0) != 0)
                throw new IOException("Cannot remove upload file.");
        }

        public void Dispose()
        {
            orphanEntries?.Dispose();
            orphanEntries = null;
            folder?.Dispose();
            root?.Dispose();
        }

        static int Fd(SafeHandle handle) => handle.DangerousGetHandle().ToInt32();
        static SafeHandle PinDirectory(string path, SafeHandle parent)
        {
            if (NativeLock.IsWindows)
            {
                var handle = CreateFileW(path, 0, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                if (!handle.IsInvalid && OrdinaryWindowsHandle(handle, true))
                    return handle;
                int error = Marshal.GetLastWin32Error();
                bool missing = handle.IsInvalid && (error == 2 || error == 3);
                handle.Dispose();
                if (missing && parent != null)
                    return null;
                throw new IOException("Cannot pin private upload directory.");
            }
            int flags = NoFollow | CloseOnExec | (Darwin ? 0x100000 : 0x10000);
            int fd = OpenAt(parent == null ? (Darwin ? -2 : -100) : Fd(parent), path, flags, 0);
            if (fd < 0)
            {
                if (parent != null && Marshal.GetLastWin32Error() == 2)
                    return null;
                throw new IOException("Cannot pin private upload directory.");
            }
            return new DirectoryHandle(fd);
        }

        sealed class DirectoryHandle : SafeHandle
        {
            internal DirectoryHandle(int fd) : base(new IntPtr(-1), true) { SetHandle(new IntPtr(fd)); }
            public override bool IsInvalid => handle.ToInt64() < 0;
            protected override bool ReleaseHandle() => CloseFd(handle.ToInt32()) == 0;
        }

        // 只通过不跟随重解析点的固定句柄更改属性，拒绝多硬链接文件。
        static void SetWindowsReadOnly(string path, bool value)
        {
            using (var handle = CreateFileW(path, 0x180, 1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero))
            {
                if (handle.IsInvalid || !OrdinaryWindowsHandle(handle, false)
                    || !GetStandardInfo(handle, 1, out var standard, (uint)Marshal.SizeOf<StandardInfo>())
                    || standard.Links != 1
                    || !GetBasicInfo(handle, 0, out var basic, (uint)Marshal.SizeOf<BasicInfo>()))
                    throw new IOException("Cannot verify private upload file.");
                basic.Attributes = value ? (basic.Attributes & ~0x80u) | 1u : basic.Attributes & ~1u;
                if (basic.Attributes == 0)
                    basic.Attributes = 0x80;
                if (!SetBasicInfo(handle, 0, ref basic, (uint)Marshal.SizeOf<BasicInfo>()))
                    throw new IOException("Cannot update private upload file attributes.");
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct StandardInfo { internal long Allocated, Length; internal uint Links; internal byte DeletePending, Directory; }
        [StructLayout(LayoutKind.Sequential)]
        struct BasicInfo { internal long Created, Accessed, Written, Changed; internal uint Attributes; }
        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
        static extern bool GetStandardInfo(SafeFileHandle handle, int type, out StandardInfo info, uint size);
        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
        static extern bool GetBasicInfo(SafeFileHandle handle, int type, out BasicInfo info, uint size);
        [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
        static extern bool SetBasicInfo(SafeFileHandle handle, int type, ref BasicInfo info, uint size);

        static bool OrdinaryWindowsHandle(SafeFileHandle handle, bool directory)
        {
            if (!GetFileInformationByHandleEx(handle, 9, out var tag, 8))
                return false;
            return (tag.Attributes & 0x450) == (directory ? 0x10u : 0u);
        }
        [StructLayout(LayoutKind.Sequential)]
        struct AttributeTag { internal uint Attributes, Tag; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int type, out AttributeTag info, int length);
#if UNITY_IOS && !UNITY_EDITOR
        const string Lib = "__Internal";
        static bool Darwin => true;
#elif UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
        const string Lib = "/usr/lib/libSystem.B.dylib";
        static bool Darwin => true;
#else
        const string Lib = "libc";
        static bool Darwin => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
#endif
        static int NoFollow => Darwin ? 0x100 : 0x20000;
        static int CloseOnExec => Darwin ? 0x1000000 : 0x80000;
        static bool GetUnixFileInfo(int descriptor, string name, out long length) =>
            GetUnixFileInfo(descriptor, name, out length, out _);

        static bool GetUnixFileInfo(int descriptor, string name, out long length, out bool singleLink)
        {
            // Darwin、Linux x64 和 Linux arm64 的 64 位 stat 布局。
            int modeOffset;
            if (Darwin)
                modeOffset = 4;
            else if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
                modeOffset = 24;
            else if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
                modeOffset = 16;
            else
                throw new NotSupportedException("Unverified upload file stat ABI.");
            byte[] data = new byte[512];
            int result = name == null ? Fstat(descriptor, data)
                : FstatAt(descriptor, name, data, Darwin ? 0x20 : 0x100);
            if (result != 0)
                throw new IOException("Cannot inspect private upload file.");
            bool regular = (BitConverter.ToUInt16(data, modeOffset) & 0xf000) == 0x8000;
            singleLink = Darwin ? BitConverter.ToUInt16(data, 6) == 1
                : modeOffset == 24 ? BitConverter.ToUInt64(data, 16) == 1 : BitConverter.ToUInt32(data, 20) == 1;
            length = regular ? BitConverter.ToInt64(data, Darwin ? 96 : 48) : 0;
            return regular;
        }
        [DllImport(Lib, EntryPoint = "fdopendir", SetLastError = true)]
        static extern IntPtr FdOpenDir(int fd);
        [DllImport(Lib, EntryPoint = "readdir_r", SetLastError = true)]
        static extern int ReadDir(IntPtr directory, IntPtr entry, out IntPtr result);
        [DllImport(Lib, EntryPoint = "closedir", SetLastError = true)]
        static extern int CloseDir(IntPtr directory);
        [DllImport(Lib, EntryPoint = "fstat", SetLastError = true)]
        static extern int Fstat(int fd, byte[] output);
        [DllImport(Lib, EntryPoint = "fstatat", SetLastError = true)]
        static extern int FstatAt(int directory, string name, byte[] output, int flags);
        // Apple arm64 可变参数从栈读取，其他已支持的 ABI 从第四参数读取。
        // 同时提供这两处的 mode，确保 IL2CPP 对原生 openat 只生成一种签名。
        static int OpenAt(int directory, string name, int flags, int mode) =>
            OpenAtNative(directory, name, flags, mode, 0, 0, 0, 0, mode);
        [DllImport(Lib, EntryPoint = "openat", SetLastError = true)]
        static extern int OpenAtNative(int directory, string name, int flags,
            int registerMode, long x4, long x5, long x6, long x7, int stackMode);
        [DllImport(Lib, EntryPoint = "mkdirat", SetLastError = true)]
        static extern int MkdirAt(int directory, string name, int mode);
        [DllImport(Lib, EntryPoint = "renameat", SetLastError = true)]
        static extern int RenameAt(int fromDir, string from, int toDir, string to);
        [DllImport(Lib, EntryPoint = "unlinkat", SetLastError = true)]
        static extern int UnlinkAt(int directory, string name, int flags);
        [DllImport(Lib, EntryPoint = "fchmod", SetLastError = true)]
        static extern int Fchmod(int fd, int mode);
        [DllImport(Lib, EntryPoint = "close")]
        static extern int CloseFd(int fd);
        [DllImport(Lib, EntryPoint = "write", SetLastError = true)]
        static extern IntPtr WriteFd(int fd, IntPtr buffer, UIntPtr count);
        [DllImport(Lib, EntryPoint = "read", SetLastError = true)]
        static extern IntPtr ReadFd(int fd, IntPtr buffer, UIntPtr count);

        sealed class UnixStream : Stream
        {
            int fd;
            readonly bool writable;
            internal UnixStream(int fd, bool writable) { this.fd = fd; this.writable = writable; }
            public override int Read(byte[] buffer, int offset, int count) => Transfer(buffer, offset, count, false);
            public override void Write(byte[] buffer, int offset, int count)
            {
                while (count > 0)
                {
                    int written = Transfer(buffer, offset, count, true);
                    if (written == 0)
                        throw new IOException("Upload write made no progress.");
                    offset += written;
                    count -= written;
                }
            }
            int Transfer(byte[] buffer, int offset, int count, bool writing)
            {
                if (fd < 0 || writing != writable || buffer == null || offset < 0 || count < 0 || offset > buffer.Length - count)
                    throw new IOException("Invalid upload stream access.");
                if (count == 0)
                    return 0;
                var pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                try
                {
                    var address = IntPtr.Add(pin.AddrOfPinnedObject(), offset);
                    long n = (writing ? WriteFd(fd, address, (UIntPtr)(uint)count) : ReadFd(fd, address, (UIntPtr)(uint)count)).ToInt64();
                    if (n < 0 || n > count)
                        throw new IOException("Upload file I/O failed.");
                    return (int)n;
                }
                finally { pin.Free(); }
            }
            protected override void Dispose(bool disposing)
            {
                int owned = fd;
                fd = -1;
                if (owned >= 0)
                {
                    bool sealedFile = !writable || Fchmod(owned, 256) == 0;
                    if (CloseFd(owned) != 0 || !sealedFile)
                        throw new IOException("Cannot finish private upload file.");
                }
                base.Dispose(disposing);
            }
            public override bool CanSeek => false;
            public override bool CanRead => !writable;
            public override bool CanWrite => writable;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() {}
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
