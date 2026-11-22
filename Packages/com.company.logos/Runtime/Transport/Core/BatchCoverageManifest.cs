using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Logos.Storage;
namespace Logos.Transport
{
    public enum CoverageKnowledge
    {
        Unknown = 0
    }
    /// <summary>仅描述本批已授权记录的证据；不是全任务去重数量或连续覆盖率。</summary>
    public sealed class BatchCoverageManifest
    {
        internal const int WireBytes = 256, Offset = 544;
        private readonly byte[] scopeDigest;
        public int PhysicalRecords
        {
            get;
        }
        public int DirectRecords
        {
            get;
        }
        public int SummaryRecords
        {
            get;
        }
        public int RepresentedInputs
        {
            get;
        }
        public int IncidentRecords
        {
            get;
        }
        public int DirectErrorFatalRecords
        {
            get;
        }
        public int DirectCriticalEvents
        {
            get;
        }
        public int ResolvedIncidentReferences
        {
            get;
        }
        public int UnresolvedIncidentReferences
        {
            get;
        }
        public DateTime? FirstSelectedUtc
        {
            get;
        }
        public DateTime? LastSelectedUtc
        {
            get;
        }
        public DateTime? FirstRepresentedUtc
        {
            get;
        }
        public DateTime? LastRepresentedUtc
        {
            get;
        }
        public bool ContinuousCoverage => false;
        public CoverageKnowledge CheckpointStatus => CoverageKnowledge.Unknown;
        public CoverageKnowledge CompletionStatus => CoverageKnowledge.Unknown;
        public CoverageKnowledge ScopeGapStatus => CoverageKnowledge.Unknown;
        // 正文可能经过脱敏；没有受信的版本解析契约时不能从任意文本推断版本。
        public string AppVersion => null;
        public string UnityVersion => null;
        public byte[] CopyScopeDigest() => (byte[])scopeDigest.Clone();
        internal BatchCoverageManifest(CoverageAccumulator a, RetrievalScope scope)
        {
            PhysicalRecords = a.Physical;
            DirectRecords = a.Direct;
            SummaryRecords = a.Summary;
            RepresentedInputs = a.Represented;
            IncidentRecords = a.Incident;
            DirectErrorFatalRecords = a.ErrorFatal;
            DirectCriticalEvents = a.Critical;
            ResolvedIncidentReferences = a.Resolved;
            UnresolvedIncidentReferences = a.Unresolved;
            FirstSelectedUtc = a.First;
            LastSelectedUtc = a.Last;
            FirstRepresentedUtc = a.MemberFirst;
            LastRepresentedUtc = a.MemberLast;
            scopeDigest = DigestScope(scope);
        }
        internal void Write(byte[] b, int o)
        {
            Array.Clear(b, o, WireBytes);
            Wire.Put16(b, o, 1);
            Wire.Put16(b, o + 2, WireBytes);
            Wire.Put32(b, o + 4, (uint)((FirstSelectedUtc.HasValue ? 1 : 0) | (FirstRepresentedUtc.HasValue ? 2 : 0)));
            int[] counts = { PhysicalRecords, DirectRecords, SummaryRecords, RepresentedInputs, IncidentRecords, DirectErrorFatalRecords, DirectCriticalEvents, ResolvedIncidentReferences, UnresolvedIncidentReferences };
            for (int i = 0; i < counts.Length; i++)
                Wire.Put32(b, o + 8 + i * 4, (uint)counts[i]);
            Wire.Put64(b, o + 48, FirstSelectedUtc?.Ticks ?? 0);
            Wire.Put64(b, o + 56, LastSelectedUtc?.Ticks ?? 0);
            Wire.Put64(b, o + 64, FirstRepresentedUtc?.Ticks ?? 0);
            Wire.Put64(b, o + 72, LastRepresentedUtc?.Ticks ?? 0);
            Buffer.BlockCopy(scopeDigest, 0, b, o + 80, 32);
            // 44..47、112..255 为保留字段；0 明确表示 unknown / 非连续，而非缺口为零。
        }
        internal void ValidateBytes(byte[] body)
        {
            var expected = new byte[WireBytes];
            Write(expected, 0);
            for (int i = 0; i < WireBytes; i++)
                if (expected[i] != body[Offset + i])
                    throw new Logos.Storage.StorageFormatException();
        }
        // 摘要绑定筛选条件，即使两个请求恰好选出相同记录，也要按各自的范围验证。
        private static byte[] DigestScope(RetrievalScope s)
        {
            using (var stream = new MemoryStream(1280))
            using (var w = new BinaryWriter(stream, Encoding.UTF8, true))
            using (var sha = SHA256.Create())
            {
                w.Write(Encoding.ASCII.GetBytes("Logos.Transport/CoverageScope/v1\0"));
                w.Write(s.StartUtc.Ticks);
                w.Write(s.EndUtc.Ticks);
                w.Write(s.LevelMask);
                Optional(w, s.StartSequence);
                Optional(w, s.EndSequence);
                Text(w, s.AppSessionId);
                Text(w, s.UnityInstanceId);
                Text(w, s.UnityVisitId);
                Optional(w, s.IdentityVersion);
                if (s.DiagnosticUserId != null)
                    Text(w, s.DiagnosticUserId);
                w.Flush();
                return sha.ComputeHash(stream.ToArray());
            }
        }
        private static void Optional(BinaryWriter w, long? value)
        {
            w.Write(value.HasValue);
            if (value.HasValue)
                w.Write(value.Value);
        }
        private static void Text(BinaryWriter w, string value)
        {
            if (value == null)
            {
                w.Write(-1);
                return;
            }
            var bytes = Encoding.UTF8.GetBytes(value);
            w.Write(bytes.Length);
            w.Write(bytes);
        }
    }
    // 固定计数器；不缓存记录、正文或被过滤的身份。
    internal struct CoverageAccumulator
    {
        internal int Physical, Direct, Summary, Represented, Incident, ErrorFatal, Critical, Resolved, Unresolved;
        internal DateTime? First, Last, MemberFirst, MemberLast;
        internal void Observe(LogRecord record)
        {
            Physical++;
            Range(record.UtcTimestamp, ref First, ref Last);
            var m = record.DiagnosticMetadata;
            if (m == null || m.Kind == DiagnosticRecordKind.Event)
            {
                Direct++;
                if (record.Level == LogLevel.Error || record.Level == LogLevel.Fatal)
                    ErrorFatal++;
                if (DiagnosticRecordMetadata.IsCriticalEvent(record.EventTypeId))
                    Critical++;
            }
            else if (m.Kind == DiagnosticRecordKind.RepeatSummary)
            {
                Summary++;
                Represented += m.Members.Count;
                foreach (var member in m.Members)
                    Range(member.UtcTimestamp, ref MemberFirst, ref MemberLast);
            }
            else if (m.Kind == DiagnosticRecordKind.IncidentReference)
            {
                Incident++;
                foreach (var member in m.Members)
                {
                    if (member.IsResolved)
                        Resolved++;
                    else
                        Unresolved++;
                }
            }
            else
                throw new Logos.Storage.StorageFormatException();
        }
        private static void Range(DateTime utc, ref DateTime? first, ref DateTime? last)
        {
            if (!first.HasValue || utc < first.Value)
                first = utc;
            if (!last.HasValue || utc > last.Value)
                last = utc;
        }
        internal BatchCoverageManifest Finish(RetrievalScope scope) => new BatchCoverageManifest(this, scope);
    }
}
