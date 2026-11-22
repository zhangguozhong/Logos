using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using Logos.Storage;

namespace Logos.Transport
{
    // 每个日志目录仅借出一条路径。后台任务全部结束后才释放所有者，配额申请由调度线程执行。
    internal sealed class FileUploadSpool
    {
        readonly JournalSession journal;
        volatile bool needsRecovery = true;
        int reserved;
        string published;
        internal bool NeedsRecovery => needsRecovery;
        internal FileUploadSpool(JournalSession journal)
        {
            this.journal = journal ?? throw new ArgumentNullException(nameof(journal));
        }

        internal bool TryReserve(int length)
        {
            if (needsRecovery || length <= 0 || length > 131072 || Volatile.Read(ref reserved) != 0
                || !journal.TryReserveUploadFile(length))
                return false;
            Volatile.Write(ref reserved, length);
            return true;
        }

        internal bool Recover()
        {
            if (Volatile.Read(ref reserved) != 0)
                throw new InvalidOperationException("Upload file is still borrowed.");
            needsRecovery = true;
            if (!journal.CleanUploadPage(false))
                return false;
            needsRecovery = false;
            return true;
        }

        internal void CloseRecovery() => journal.CloseUploadCleanup(false);

        internal string Prepare(UploadRequest request)
        {
            string current = null;
            try
            {
                if (request == null || needsRecovery || published != null || request.Body.Length != Volatile.Read(ref reserved))
                    throw new InvalidOperationException("Upload reservation unavailable.");
                using (var workspace = new UploadWorkspace(journal.GetUploadFileOwner(), true))
                {
                    string stem = Guid.NewGuid().ToString("N");
                    string partial = stem + ".part";
                    string ready = stem + ".body";
                    using (var output = workspace.OpenFile(partial, true))
                    {
                        current = partial;
                        request.Body.WriteTo(output);
                    }
                    using (var input = workspace.OpenFile(partial, false))
                        VerifyBody(input, request);
                    workspace.Publish(partial, ready);
                    current = ready;
                    workspace.Verify();
                    published = Path.Combine(workspace.PathName, ready);
                    return published;
                }
            }
            catch
            {
                // 创建或关闭失败仍可能遗留文件。保留计费，直到有界恢复确认目录已清空。
                needsRecovery = true;
                if (current != null)
                {
                    using (var workspace = new UploadWorkspace(journal.GetUploadFileOwner(), false))
                        workspace.DeleteFile(current);
                }
                throw;
            }
            finally
            {
                if (needsRecovery)
                    Interlocked.Exchange(ref reserved, 0);
            }
        }

        static void VerifyBody(Stream input, UploadRequest request)
        {
            using (var hash = SHA256.Create())
            {
                var bytes = new byte[4096];
                int remaining = request.Body.Length;
                while (remaining > 0)
                {
                    int read = input.Read(bytes, 0, Math.Min(bytes.Length, remaining));
                    if (read == 0)
                        throw new IOException("Incomplete upload file.");
                    hash.TransformBlock(bytes, 0, read, bytes, 0);
                    remaining -= read;
                }
                if (input.ReadByte() >= 0)
                    throw new IOException("Oversized upload file.");
                hash.TransformFinalBlock(bytes, 0, 0);
                string digest = BitConverter.ToString(hash.Hash).Replace("-", "");
                if (!string.Equals(digest, request.BodySha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Upload file digest mismatch.");
            }
        }

        internal void Delete(string path)
        {
            if (path == null || !string.Equals(path, published, StringComparison.Ordinal))
                throw new InvalidOperationException("Path is not borrowed by this operation.");
            try
            {
                using (var workspace = new UploadWorkspace(journal.GetUploadFileOwner(), false))
                    workspace.DeleteFile(Path.GetFileName(path));
                journal.ClearUploadFileCharge();
            }
            catch
            {
                needsRecovery = true;
                throw;
            }
            finally
            {
                published = null;
                Interlocked.Exchange(ref reserved, 0);
            }
        }

        internal void ReleaseReservation()
        {
            if (published != null)
                throw new InvalidOperationException("Upload file is still borrowed.");
            if (Interlocked.Exchange(ref reserved, 0) != 0)
                journal.ClearUploadFileCharge();
        }
    }
}
