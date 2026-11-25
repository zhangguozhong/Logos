using System.Buffers.Binary;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Logos.Storage;
using Logos.Transport;

namespace Logos.Cli;

internal static class Program
{
    private const string Usage = "logos-cli decode --keys <密钥文件.json> [--format text|jsonl] [--] <批次文件>...";
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    internal static int Main(string[] args)
    {
        if (args is ["--help"] or ["-h"] or ["decode", "--help"])
        {
            Console.WriteLine(Usage);
            return 0;
        }
        string file = null;
        try
        {
            var options = Parse(args);
            file = options.Keys;
            using var keys = ExportKeys.Load(file);
            var timeline = new Dictionary<(string Installation, string Id), (LogRecord Record, byte[] Digest)>();
            foreach (string path in options.Files)
            {
                file = path;
                var bytes = BoundedWire.ReadFile(path, UploadEnvelope.MaximumBodyBytes);
                if (bytes.Length < 16 && "LOGOSX01"u8.StartsWith(bytes.AsSpan(0, Math.Min(8, bytes.Length))))
                    throw new EndOfStreamException();
                if (bytes.Length < 16 || !bytes.AsSpan(0, 8).SequenceEqual("LOGOSX01"u8))
                    throw new StorageFormatException();
                uint declared = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12));
                if (declared > bytes.Length && declared <= UploadEnvelope.MaximumBodyBytes)
                    throw new EndOfStreamException();

                VerifiedUploadEnvelope decoded;
                string installation;
                try
                {
                    decoded = UploadEnvelope.ReadArchive(new UploadBody(bytes, true), keys, out installation);
                }
                catch (Exception) when (keys.MissingId != null)
                {
                    return Fail(3, path, "找不到导出密钥 ID " + Quote(keys.MissingId));
                }
                foreach (var record in decoded.Records)
                {
                    var id = (installation, record.RecordId);
                    var digest = RecordDigest.Compute(record);
                    if (timeline.TryGetValue(id, out var old))
                    {
                        if (!digest.AsSpan().SequenceEqual(old.Digest))
                            return Fail(6, path, "同一安装的记录 ID 内容冲突 " + Quote(record.RecordId));
                    }
                    else
                        timeline.Add(id, (record, digest));
                }
            }

            // 所有文件通过验证后才输出。相同 UTC 时间采用稳定的会话与序号顺序。
            foreach (var entry in timeline.OrderBy(x => x.Value.Record.UtcTimestamp)
                .ThenBy(x => x.Key.Installation, StringComparer.Ordinal)
                .ThenBy(x => x.Value.Record.Context.AppSessionId, StringComparer.Ordinal)
                .ThenBy(x => x.Value.Record.Context.UnityInstanceId, StringComparer.Ordinal)
                .ThenBy(x => x.Value.Record.Sequence)
                .ThenBy(x => x.Value.Record.Context.UnityVisitId, StringComparer.Ordinal)
                .ThenBy(x => x.Key.Id, StringComparer.Ordinal))
                Print(entry.Key.Installation, entry.Value.Record, options.Format);
            return 0;
        }
        catch (EndOfStreamException) { return Fail(4, file, "文件截断或声明长度超过现有数据"); }
        catch (StorageAuthenticationException) { return Fail(5, file, "认证失败，请检查导出密钥与文件完整性"); }
        catch (StorageFormatException) { return Fail(5, file, "批次格式、版本或内容校验失败"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Fail(2, file, "无法读写文件或输出");
        }
        catch (Exception e) when (e is JsonException or FormatException or KeyNotFoundException or ArgumentException or InvalidOperationException)
        {
            return Fail(2, file, "参数、密钥文件或输入长度无效。用法：" + Usage);
        }
    }

    private static (string Keys, string Format, List<string> Files) Parse(string[] args)
    {
        if (args.Length == 0 || args[0] != "decode")
            throw new FormatException();
        string keys = null, format = null;
        bool pathsOnly = false;
        var files = new List<string>();
        for (int i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            if (!pathsOnly && arg == "--")
                pathsOnly = true;
            else if (!pathsOnly && arg == "--keys" && keys == null && i + 1 < args.Length)
                keys = args[++i];
            else if (!pathsOnly && arg == "--format" && format == null && i + 1 < args.Length)
                format = args[++i];
            else if (!pathsOnly && arg.StartsWith('-'))
                throw new FormatException();
            else
                files.Add(arg);
        }
        if (string.IsNullOrEmpty(keys) || files.Count is < 1 or > 512 || format is not (null or "jsonl" or "text"))
            throw new FormatException();
        return (keys, format ?? "text", files);
    }

    private static int Fail(int code, string path, string message)
    {
        Console.Error.WriteLine($"{Quote(path)} {message}");
        return code;
    }
    private static string Quote(string value) => JsonSerializer.Serialize(value, Json);

    private static void Print(string installation, LogRecord r, string format)
    {
        var c = r.Context;
        if (format == "jsonl")
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                utc = r.UtcTimestamp.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                level = r.Level.ToString(),
                installation,
                c.AppSessionId,
                c.UnityInstanceId,
                c.UnityVisitId,
                c.IdentityContextVersion,
                c.DiagnosticUserId,
                c.Device,
                r.Sequence,
                r.MonotonicTimestamp,
                r.RecordId,
                r.SourceId,
                r.EventTypeId,
                r.Message,
                r.Stack,
                r.MessageTruncated,
                r.StackTruncated,
                r.DiagnosticMetadata
            }, Json));
            return;
        }
        Console.WriteLine($"{r.UtcTimestamp:O} {r.Level} install={Quote(installation)} session={Quote(c.AppSessionId)} instance={Quote(c.UnityInstanceId)} visit={Quote(c.UnityVisitId)} uid={Quote(c.DiagnosticUserId)} seq={r.Sequence} id={Quote(r.RecordId)} event={Quote(r.EventTypeId)} {Quote(r.Message)}");
        if (!string.IsNullOrEmpty(r.Stack))
            Console.WriteLine("    stack=" + Quote(r.Stack));
    }
}
