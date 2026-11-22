using System;
namespace Logos.Transport
{
    internal static class TransferValidation
    {
        internal static string Id(string value, string name, bool optional = false)
        {
            if (value == null && optional)
                return null;
            if (value == null || value.Length < 1 || value.Length > 128 || value[0] == ' ' || value[value.Length - 1] == ' ')
                throw new ArgumentException("Expected a 1..128 printable ASCII identifier.", name);
            for (int i = 0; i < value.Length; i++)
                if (value[i] < 32 || value[i] > 126)
                    throw new ArgumentException("Invalid identifier.", name);
            return value;
        }
        internal static string ContextId(string value, string name, bool allowEmpty, bool optional = true)
        {
            if (value == null && optional)
                return null;
            if (value == null || value.Length > LogContext.MaxIdLength || (!allowEmpty && string.IsNullOrWhiteSpace(value)) || (value.Length != 0 && string.IsNullOrWhiteSpace(value)))
                throw new ArgumentException("Invalid context identifier.", name);
            return value;
        }
        internal static void Utc(DateTime value, string name)
        {
            if (value.Kind != DateTimeKind.Utc)
                throw new ArgumentException("UTC required.", name);
        }
    }

    /// <summary>不可变记录谓词。事件 UTC 独立于源额度日期。</summary>
    public sealed class RetrievalScope
    {
        public DateTime StartUtc
        {
            get;
        }
        public DateTime EndUtc
        {
            get;
        }
        public byte LevelMask
        {
            get;
        }
        public long? StartSequence
        {
            get;
        }
        public long? EndSequence
        {
            get;
        }
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
        public long? IdentityVersion
        {
            get;
        }
        public string DiagnosticUserId { get; }
        // 兼容已编译的宿主，省略 UID 时保持原有筛选语义。
        public RetrievalScope(DateTime startUtc, DateTime endUtc, byte levelMask, long? startSequence, long? endSequence,
            string appSessionId, string unityInstanceId, string unityVisitId, long? identityVersion)
            : this(startUtc, endUtc, levelMask, startSequence, endSequence, appSessionId, unityInstanceId, unityVisitId, identityVersion, null) { }

        public RetrievalScope(DateTime startUtc, DateTime endUtc, byte levelMask = 63, long? startSequence = null, long? endSequence = null, string appSessionId = null, string unityInstanceId = null, string unityVisitId = null, long? identityVersion = null, string diagnosticUserId = null)
        {
            TransferValidation.Utc(startUtc, nameof(startUtc));
            TransferValidation.Utc(endUtc, nameof(endUtc));
            if (startUtc >= endUtc || levelMask == 0 || (levelMask & ~63) != 0 || startSequence < 0 || endSequence < 0 || (startSequence.HasValue && endSequence.HasValue && startSequence >= endSequence) || identityVersion < 0)
                throw new ArgumentException("Invalid scope interval, mask or identity.");
            AppSessionId = TransferValidation.ContextId(appSessionId, nameof(appSessionId), true);
            UnityInstanceId = TransferValidation.ContextId(unityInstanceId, nameof(unityInstanceId), false);
            UnityVisitId = TransferValidation.ContextId(unityVisitId, nameof(unityVisitId), true);
            StartUtc = startUtc;
            EndUtc = endUtc;
            LevelMask = levelMask;
            StartSequence = startSequence;
            EndSequence = endSequence;
            IdentityVersion = identityVersion;
            if (diagnosticUserId != null && !LogContext.IsValidDiagnosticUserId(diagnosticUserId))
                throw new ArgumentException("Invalid diagnostic user identifier.", nameof(diagnosticUserId));
            DiagnosticUserId = diagnosticUserId;
        }
        public bool Matches(LogRecord record) => record != null && MatchesIdentity(record.Level, record.UtcTimestamp, record.Sequence, record.Context);
        public bool MatchesIdentity(LogLevel level, DateTime utcTimestamp, long sequence, LogContext context) =>
            (DiagnosticUserId == null || DiagnosticUserId == context?.DiagnosticUserId) && MatchesCoordinates(level, utcTimestamp, sequence, context);
        internal bool MatchesCoordinates(LogLevel level, DateTime utcTimestamp, long sequence, LogContext context) => context != null && level >= LogLevel.Trace && level <= LogLevel.Fatal
            && (LevelMask & (1 << (int)level)) != 0 && utcTimestamp.Kind == DateTimeKind.Utc && utcTimestamp >= StartUtc && utcTimestamp < EndUtc
            && (!StartSequence.HasValue || sequence >= StartSequence.Value) && (!EndSequence.HasValue || sequence < EndSequence.Value)
            && (AppSessionId == null || AppSessionId == context.AppSessionId)
            && (UnityInstanceId == null || UnityInstanceId == context.UnityInstanceId)
            && (UnityVisitId == null || UnityVisitId == context.UnityVisitId)
            && (!IdentityVersion.HasValue || IdentityVersion.Value == context.IdentityContextVersion);
        // 每个引用独立检查，不能用合成记录的容器时间替代成员范围。
        // 诊断记录还携带其他记录的引用，其成员也可能跨越请求指定的时间或身份范围。
        public bool ContainsDiagnosticReferences(LogRecord record)
        {
            var metadata = record?.DiagnosticMetadata;
            if (metadata == null || metadata.Kind == DiagnosticRecordKind.Event)
                return record != null;
            for (int i = 0; i < metadata.Members.Count; i++)
                if (!MatchesReference(metadata.Members[i]))
                    return false;
            return (metadata.FirstCommitted == null || MatchesReference(metadata.FirstCommitted)) && (metadata.LatestReference == null || MatchesReference(metadata.LatestReference));
        }
        public bool MatchesReference(DiagnosticRecordReference reference) => reference != null && MatchesIdentity(reference.Level, reference.UtcTimestamp, reference.Sequence, reference.Context)
            && (!reference.IsResolved || MatchesIdentity(reference.Level, reference.TargetUtcTimestamp, reference.TargetSequence, reference.Context));

    }
    public sealed class ExportLimits
    {
        public long SourceBytes
        {
            get;
        }
        public long EncodedBytes
        {
            get;
        }
        public int Batches
        {
            get;
        }
        public ExportLimits(long sourceBytes, long encodedBytes, int batches)
        {
            if (sourceBytes <= 0 || encodedBytes <= 0 || batches <= 0)
                throw new ArgumentException("Finite positive export limits required.");
            SourceBytes = sourceBytes;
            EncodedBytes = encodedBytes;
            Batches = batches;
        }
    }
    public sealed class RetrievalRequest
    {
        public string TaskId
        {
            get;
        }
        public string DestinationId
        {
            get;
        }
        public string InstallationId
        {
            get;
        }
        public long FirstQuotaDay
        {
            get;
        }
        public int QuotaDays
        {
            get;
        }
        public RetrievalScope Scope
        {
            get;
        }
        public DateTime ExpiresUtc
        {
            get;
        }
        public ExportLimits Limits
        {
            get;
        }
        public RetrievalRequest(string taskId, string destinationId, string installationId, long firstQuotaDay, int quotaDays, RetrievalScope scope, DateTime expiresUtc, ExportLimits limits)
        {
            TaskId = TransferValidation.Id(taskId, nameof(taskId));
            DestinationId = TransferValidation.Id(destinationId, nameof(destinationId));
            InstallationId = TransferValidation.Id(installationId, nameof(installationId));
            if (quotaDays < 1 || quotaDays > 1024 || firstQuotaDay < 0 || firstQuotaDay > DateTime.MaxValue.Ticks / TimeSpan.TicksPerDay - quotaDays + 1)
                throw new ArgumentException("Invalid quota day range.");
            TransferValidation.Utc(expiresUtc, nameof(expiresUtc));
            FirstQuotaDay = firstQuotaDay;
            QuotaDays = quotaDays;
            Scope = scope ?? throw new ArgumentNullException(nameof(scope));
            ExpiresUtc = expiresUtc;
            Limits = limits ?? throw new ArgumentNullException(nameof(limits));
        }
    }
    public sealed class TransferAuthorizationDomain
    {
        internal RetrievalScope Scope
        {
            get;
        }
        public TransferAuthorizationDomain(string appSessionId, string unityInstanceId, long identityVersion, DateTime startUtc,
            DateTime endUtc, byte levelMask, long? startSequence, long? endSequence)
            : this(appSessionId, unityInstanceId, identityVersion, startUtc, endUtc, levelMask, startSequence, endSequence, null) { }

        public TransferAuthorizationDomain(string appSessionId, string unityInstanceId, long identityVersion, DateTime startUtc, DateTime endUtc, byte levelMask = 63, long? startSequence = null, long? endSequence = null, string diagnosticUserId = null)
        {
            TransferValidation.ContextId(appSessionId, nameof(appSessionId), true, false);
            TransferValidation.ContextId(unityInstanceId, nameof(unityInstanceId), false, false);
            Scope = new RetrievalScope(startUtc, endUtc, levelMask, startSequence, endSequence, appSessionId, unityInstanceId, null, identityVersion, diagnosticUserId);
        }
    }
    public sealed class TransferAuthorizationSnapshot
    {
        private readonly TransferAuthorizationDomain[] domains;
        public long Revision
        {
            get;
        }
        public DateTime TrustedUtc
        {
            get;
        }
        public DateTime ExpiresUtc
        {
            get;
        }
        public string InstallationId
        {
            get;
        }
        public string DestinationId
        {
            get;
        }
        public int DomainCount => domains.Length;
        internal RetrievalScope DomainScope(int index) => domains[index].Scope;
        public TransferAuthorizationSnapshot(long revision, DateTime trustedUtc, DateTime expiresUtc, string installationId, string destinationId, TransferAuthorizationDomain[] domains)
        {
            if (revision < 0 || domains == null || domains.Length > 8)
                throw new ArgumentException("At most eight authorization domains required.");
            TransferValidation.Utc(trustedUtc, nameof(trustedUtc));
            TransferValidation.Utc(expiresUtc, nameof(expiresUtc));
            if (expiresUtc <= trustedUtc)
                throw new ArgumentException("Authorization already expired.");
            InstallationId = TransferValidation.Id(installationId, nameof(installationId));
            DestinationId = TransferValidation.Id(destinationId, nameof(destinationId));
            for (int i = 0; i < domains.Length; i++)
                if (domains[i] == null)
                    throw new ArgumentException("Null domain.");
            this.domains = (TransferAuthorizationDomain[])domains.Clone();
            Revision = revision;
            TrustedUtc = trustedUtc;
            ExpiresUtc = expiresUtc;
        }
        internal bool Matches(LogRecord record, string requiredUser = null)
        {
            for (int i = 0; i < domains.Length; i++)
                if ((requiredUser == null || requiredUser == domains[i].Scope.DiagnosticUserId) && domains[i].Scope.Matches(record))
                    return true;
            return false;
        }
        internal bool MatchesReference(DiagnosticRecordReference reference, string requiredUser = null)
        {
            bool original = false, target = !reference.IsResolved;
            for (int i = 0; i < domains.Length; i++)
            {
                if (requiredUser != null && requiredUser != domains[i].Scope.DiagnosticUserId)
                    continue;
                original |= domains[i].Scope.MatchesIdentity(reference.Level, reference.UtcTimestamp, reference.Sequence, reference.Context);
                target |= reference.IsResolved && domains[i].Scope.MatchesIdentity(reference.Level, reference.TargetUtcTimestamp, reference.TargetSequence, reference.Context);
            }
            return original && target;
        }
        internal bool ContainsDiagnosticReferences(LogRecord record, string requiredUser = null)
        {
            var metadata = record?.DiagnosticMetadata;
            if (metadata == null || metadata.Kind == DiagnosticRecordKind.Event)
                return record != null;
            for (int i = 0; i < metadata.Members.Count; i++)
                if (!MatchesReference(metadata.Members[i], requiredUser))
                    return false;
            return (metadata.FirstCommitted == null || MatchesReference(metadata.FirstCommitted, requiredUser)) && (metadata.LatestReference == null || MatchesReference(metadata.LatestReference, requiredUser));
        }
        internal bool Allows(RetrievalRequest request, long revision, DateTime utc) => utc.Kind == DateTimeKind.Utc && utc >= TrustedUtc && utc < ExpiresUtc && utc < request.ExpiresUtc && revision == Revision && request.InstallationId == InstallationId && request.DestinationId == DestinationId && HasUserDomain(request.Scope.DiagnosticUserId);
        private bool HasUserDomain(string diagnosticUserId)
        {
            if (diagnosticUserId == null)
                return true;
            for (int i = 0; i < domains.Length; i++)
                if (domains[i].Scope.DiagnosticUserId == diagnosticUserId)
                    return true;
            return false;
        }
    }
}
