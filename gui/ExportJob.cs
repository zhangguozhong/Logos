using System.Diagnostics;
using System.Reflection;

namespace Logos.Decoder;

internal static class ExportJob
{
    // 逐级解析符号链接，防止通过另一个目录名将输出写回输入或密钥文件。
    internal static string CanonicalPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        var current = root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileSystemInfo item = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (item.LinkTarget != null)
                current = item.ResolveLinkTarget(true)!.FullName;
        }
        return current;
    }

    internal static async Task<int> Run(string[] files, string keys, string target, string format, CancellationToken token)
    {
        var destination = CanonicalPath(target);
        if (files.Append(keys).Any(p => string.Equals(CanonicalPath(p), destination, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("保存位置与输入批次或密钥文件相同，请选择其他文件名。");

        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        foreach (var arg in new[] { "--decode-worker", "decode", "--keys", keys, "--format", format, "--" }.Concat(files))
            start.ArgumentList.Add(arg);

        // 同目录暂存，校验失败、取消或写盘失败都不会截断用户已有的导出。
        var temporary = Path.Combine(Path.GetDirectoryName(target)!, ".logos-" + Guid.NewGuid().ToString("N") + ".tmp");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Options = FileOptions.Asynchronous };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        using var worker = new Process { StartInfo = start };
        using var writer = new FileStream(temporary, options);
        try
        {
            token.ThrowIfCancellationRequested();
            worker.Start();
            try
            {
                using var cancel = token.Register(() => Stop(worker));
                var error = worker.StandardError.ReadToEndAsync();
                var buffer = new byte[32768];
                int count = 0, read;
                bool lineStart = true;
                while ((read = await worker.StandardOutput.BaseStream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    // 文本格式的堆栈行以空格开头，应计入上一条记录。
                    for (int i = 0; i < read; i++)
                    {
                        if (lineStart && buffer[i] != (byte)' ') count++;
                        lineStart = buffer[i] == (byte)'\n';
                    }
                    await writer.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                }
                await worker.WaitForExitAsync().ConfigureAwait(false);
                var reason = (await error.ConfigureAwait(false)).Trim();
                token.ThrowIfCancellationRequested();
                if (worker.ExitCode != 0)
                    throw new IOException(reason.Length == 0 ? $"解码进程退出（{worker.ExitCode}）。" : reason);
                await writer.FlushAsync(token).ConfigureAwait(false);
                writer.Close();
                token.ThrowIfCancellationRequested();
                File.Move(temporary, target, true);
                return count;
            }
            finally
            {
                Stop(worker);
                await worker.WaitForExitAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            writer.Close();
            File.Delete(temporary);
        }
    }

    private static void Stop(Process worker)
    {
        try { if (!worker.HasExited) worker.Kill(); }
        catch (InvalidOperationException) { } // 进程可能刚好自行退出。
    }
}
