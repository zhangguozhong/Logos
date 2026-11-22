using System.Security.Cryptography;
using System.Text.Json;
using Logos.Storage;

namespace Logos.Cli;

internal sealed class ExportKeys : IStorageKeyProvider, IDisposable
{
    private readonly Dictionary<string, KeyMaterial> keys = new(StringComparer.Ordinal);
    internal string MissingId
    {
        get; private set;
    }

    internal static ExportKeys Load(string path)
    {
        var result = new ExportKeys();
        var bytes = BoundedWire.ReadFile(path, 1024 * 1024);
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            RejectDuplicateFields(doc.RootElement);
            var entries = doc.RootElement.GetProperty("exportKeys");
            if (entries.GetArrayLength() > 128)
                throw new FormatException();
            foreach (var entry in entries.EnumerateArray())
            {
                string id = entry.GetProperty("id").GetString();
                if (id == null || result.keys.ContainsKey(id))
                    throw new FormatException();
                var enc = entry.GetProperty("encryptionKeyBase64").GetBytesFromBase64();
                try
                {
                    var mac = entry.GetProperty("macKeyBase64").GetBytesFromBase64();
                    try
                    {
                        result.keys.Add(id, new KeyMaterial(id, enc, mac));
                    }
                    finally { CryptographicOperations.ZeroMemory(mac); }
                }
                finally { CryptographicOperations.ZeroMemory(enc); }
            }
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    // JsonElement.GetProperty 会选择最后一个同名字段，读取密钥前先排除歧义。
    private static void RejectDuplicateFields(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new FormatException();
                RejectDuplicateFields(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                RejectDuplicateFields(item);
        }
    }

    public KeyAcquireResult TryAcquireCurrent() => KeyAcquireResult.Unavailable();

    public KeyAcquireResult TryAcquire(string id)
    {
        if (!keys.TryGetValue(id, out var material))
        {
            MissingId = id;
            return KeyAcquireResult.Unavailable();
        }
        var enc = material.CopyEncryptionKey();
        var mac = material.CopyAuthenticationKey();
        try
        {
            return KeyAcquireResult.Acquired(new KeyMaterial(id, enc, mac));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(enc);
            CryptographicOperations.ZeroMemory(mac);
        }
    }

    public void Dispose()
    {
        foreach (var material in keys.Values)
            material.Dispose();
        keys.Clear();
    }
}
