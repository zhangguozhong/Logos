using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
namespace Logos.Transport
{
    internal static class AutomaticAuthorizationBinding
    {
        internal static string Hash(TransferAuthorizationSnapshot snapshot)
        {
            var utf8 = new UTF8Encoding(false, true);
            var domains = new byte[snapshot.DomainCount][];
            for (int i = 0; i < domains.Length; i++)
                using (var stream = new MemoryStream())
                using (var writer = new BinaryWriter(stream, utf8, true))
                {
                    var s = snapshot.DomainScope(i);
                    Text(writer, s.AppSessionId, utf8);
                    Text(writer, s.UnityInstanceId, utf8);
                    Text(writer, s.UnityVisitId, utf8);
                    Optional(writer, s.IdentityVersion);
                    writer.Write(s.StartUtc.Ticks);
                    writer.Write(s.EndUtc.Ticks);
                    writer.Write(s.LevelMask);
                    Optional(writer, s.StartSequence);
                    Optional(writer, s.EndSequence);
                    if (s.DiagnosticUserId != null)
                        Text(writer, s.DiagnosticUserId, utf8);
                    writer.Flush();
                    domains[i] = stream.ToArray();
                }
            // 按域的编码字节排序，让调用方调整授权域的排列顺序时仍得到相同的绑定值。
            Array.Sort(domains, Compare);
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, utf8, true))
            using (var sha = SHA256.Create())
            {
                Text(writer, "Logos.AutoAuthorization/v1", utf8);
                Text(writer, snapshot.InstallationId, utf8);
                Text(writer, snapshot.DestinationId, utf8);
                writer.Write(snapshot.Revision);
                writer.Write(domains.Length);
                foreach (var domain in domains)
                {
                    writer.Write(domain.Length);
                    writer.Write(domain);
                }
                writer.Flush();
                var digest = sha.ComputeHash(stream.ToArray());
                return BitConverter.ToString(digest).Replace("-", "").ToLowerInvariant();
            }
        }
        private static void Optional(BinaryWriter writer, long? value)
        {
            writer.Write(value.HasValue);
            if (value.HasValue)
                writer.Write(value.Value);
        }
        private static void Text(BinaryWriter writer, string text, Encoding encoding)
        {
            writer.Write(text != null);
            if (text == null)
                return;
            byte[] bytes = encoding.GetBytes(text);
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }
        private static int Compare(byte[] a, byte[] b)
        {
            int n = Math.Min(a.Length, b.Length);
            for (int i = 0; i < n; i++)
                if (a[i] != b[i])
                    return a[i].CompareTo(b[i]);
            return a.Length.CompareTo(b.Length);
        }
        internal static string TaskId(string binding, long cleanup, ulong generation) => "auto-" + binding + "-" + cleanup.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" + generation.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
