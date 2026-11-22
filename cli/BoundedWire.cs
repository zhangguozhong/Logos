using System.Text;

namespace Logos.Cli;

internal static class BoundedWire
{
    internal static readonly UTF8Encoding Utf8 = new(false, true);
    internal static void Text(BinaryWriter w, string value, int max, bool optional = false)
    {
        if (value == null)
        {
            if (!optional)
                throw new FormatException();
            w.Write(-1);
            return;
        }
        int length = Utf8.GetByteCount(value);
        if (length > max)
            throw new FormatException();
        w.Write(length);
        w.Write(Utf8.GetBytes(value));
    }
    internal static byte[] ReadFile(string path, int maximum)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length < 0 || file.Length > maximum)
            throw new FormatException();
        var bytes = new byte[(int)file.Length];
        file.ReadExactly(bytes);
        if (file.ReadByte() != -1)
            throw new FormatException();
        return bytes;
    }
}
