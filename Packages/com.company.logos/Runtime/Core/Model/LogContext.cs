using System;
using System.Text;

namespace Logos
{
    // 每条记录持有创建时的上下文。更新用户或访问会构造新的快照，等待写入的旧记录仍能保留原来的归属。
    public sealed class LogContext
    {
        public const int MaxIdLength = 128;
        public string AppSessionId
        {
            get;
        }
        public string UnityInstanceId
        {
            get;
        }
        public string UnityVisitId
        {
            get;
        }
        public long IdentityContextVersion
        {
            get;
        }
        public static bool IsValidDiagnosticUserId(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > MaxIdLength) return false;
            foreach (char c in value)
                if (!(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '-' || c == '_' || c == '.' || c == ':'))
                    return false;
            return true;
        }

        internal int BlockSnapshotBytes { get; }
        public string DiagnosticUserId { get; }
        public LogDeviceSnapshot Device { get; }

        // 保留旧宿主程序集引用的四参数构造函数。
        public LogContext(string appSessionId, string unityInstanceId, string unityVisitId, long identityContextVersion)
            : this(appSessionId, unityInstanceId, unityVisitId, identityContextVersion, null, null) { }

        public LogContext(string appSessionId, string unityInstanceId, string unityVisitId, long identityContextVersion, string diagnosticUserId = null, LogDeviceSnapshot device = null)
        {
            AppSessionId = OptionalId(appSessionId, nameof(appSessionId));
            UnityInstanceId = RequireId(unityInstanceId, nameof(unityInstanceId));
            UnityVisitId = OptionalId(unityVisitId, nameof(unityVisitId));
            if (identityContextVersion < 0)
                throw new ArgumentOutOfRangeException(nameof(identityContextVersion));
            IdentityContextVersion = identityContextVersion;
            if (diagnosticUserId != null && !IsValidDiagnosticUserId(diagnosticUserId))
                throw new ArgumentException("Invalid diagnostic user token.", nameof(diagnosticUserId));
            DiagnosticUserId = diagnosticUserId;
            BlockSnapshotBytes = diagnosticUserId == null && device == null ? 0
                : 6 + (diagnosticUserId?.Length ?? 0) + (device?.WireBytes ?? 0);
            Device = device;
        }

        private static string OptionalId(string value, string name)
        {
            return string.IsNullOrEmpty(value) ? string.Empty : RequireId(value, name);
        }

        internal static string RequireId(string value, string name)
        {
            if (value == null || value.Length > MaxIdLength || string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("ID must contain 1 to 128 UTF-16 code units and not be whitespace.", name);
            return value;
        }
    }

    public sealed class LogDeviceSnapshot : IEquatable<LogDeviceSnapshot>
    {
        public const int MaxFieldLength = 128;
        public string Platform { get; }
        public string Model { get; }
        public string OperatingSystem { get; }
        public string AppVersion { get; }
        public string UnityVersion { get; }
        internal int WireBytes { get; }
        private readonly int hash;

        public LogDeviceSnapshot(string platform, string model, string operatingSystem, string appVersion, string unityVersion)
        {
            Platform = Bound(platform);
            Model = Bound(model);
            OperatingSystem = Bound(operatingSystem);
            AppVersion = Bound(appVersion);
            UnityVersion = Bound(unityVersion);
            WireBytes = 20 + Encoding.UTF8.GetByteCount(Platform) + Encoding.UTF8.GetByteCount(Model)
                + Encoding.UTF8.GetByteCount(OperatingSystem) + Encoding.UTF8.GetByteCount(AppVersion) + Encoding.UTF8.GetByteCount(UnityVersion);
            unchecked
            {
                hash = (((Platform.GetHashCode() * 31 + Model.GetHashCode()) * 31 + OperatingSystem.GetHashCode()) * 31
                    + AppVersion.GetHashCode()) * 31 + UnityVersion.GetHashCode();
            }
        }

        private static string Bound(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "unknown";
            int length = Math.Min(MaxFieldLength, value.Length);
            if (length < value.Length && char.IsHighSurrogate(value[length - 1]) && char.IsLowSurrogate(value[length]))
                length--;
            string bounded = value.Substring(0, length);
            return string.IsNullOrWhiteSpace(bounded) ? "unknown" : bounded;
        }

        public override int GetHashCode() => hash;
        public override bool Equals(object value) => Equals(value as LogDeviceSnapshot);
        public bool Equals(LogDeviceSnapshot other) => ReferenceEquals(this, other) || (other != null && hash == other.hash
            && Platform == other.Platform && Model == other.Model && OperatingSystem == other.OperatingSystem
            && AppVersion == other.AppVersion && UnityVersion == other.UnityVersion);
    }
}
