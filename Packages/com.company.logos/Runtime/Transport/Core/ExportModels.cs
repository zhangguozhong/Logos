using System;
using System.Collections.Generic;
namespace Logos.Transport
{
    /// <summary>每个导出器的结果。提供书签时，Complete/NoMatch 仅描述恢复后的后缀；调用者汇总已持久化的整任务总量。</summary>
    public enum TransferOutcome
    {
        Progress, BatchReady, Complete, NoMatch, NotReady, Missing, Expired, Corrupt, KeyUnavailable, LeaseCancelled, Cancelled, Unauthorized, PartialBudget, Unavailable, WaitingCredit, IdentityUnverifiable
    }
    /// <summary>不可信恢复坐标；导出器使用前重新认证所有前置块。</summary>
    public sealed class ExportBookmark
    {
        private readonly byte[] headerHash;
        public long Day
        {
            get;
        }
        public long CapturedEnd
        {
            get;
        }
        public long BlockOffset
        {
            get;
        }
        public int NextRecordIndex
        {
            get;
        }
        public long CleanupGeneration
        {
            get;
        }
        // 同一块可以分批确认，恢复时既要知道源文件的捕获末端，也要知道块内下一条记录的索引。
        public ExportBookmark(long day, long capturedEnd, byte[] headerHash, long blockOffset, int nextRecordIndex, long cleanupGeneration = 0)
        {
            if (day < 0 || day > DateTime.MaxValue.Ticks / TimeSpan.TicksPerDay || capturedEnd < 512 || blockOffset < 512 || blockOffset > capturedEnd || nextRecordIndex < 0 || nextRecordIndex > 256 || (blockOffset == capturedEnd && nextRecordIndex != 0) || cleanupGeneration < 0 || headerHash == null || headerHash.Length != 32)
                throw new ArgumentException("Invalid bounded bookmark.");
            this.headerHash = (byte[])headerHash.Clone();
            Day = day;
            CapturedEnd = capturedEnd;
            BlockOffset = blockOffset;
            NextRecordIndex = nextRecordIndex;
            CleanupGeneration = cleanupGeneration;
        }
        public byte[] CopyHeaderHash() => (byte[])headerHash.Clone();
        internal bool HasHeader(byte[] hash)
        {
            if (hash == null || hash.Length != 32)
                return false;
            int difference = 0;
            for (int i = 0; i < 32; i++)
                difference |= headerHash[i] ^ hash[i];
            return difference == 0;
        }
    }
    public sealed class ExportBatch
    {
        public BatchCoverageManifest Coverage
        {
            get;
        }
        public UploadRequest Request
        {
            get;
        }
        public ExportBookmark NextBookmark
        {
            get;
        }
        public int RecordCount
        {
            get;
        }
        internal ExportBookmark BeforeBookmark
        {
            get;
        }
        internal ExportBatch(UploadRequest request, ExportBookmark nextBookmark, int recordCount, ExportBookmark beforeBookmark = null, BatchCoverageManifest coverage = null)
        {
            Request = request;
            NextBookmark = nextBookmark;
            RecordCount = recordCount;
            BeforeBookmark = beforeBookmark;
            Coverage = coverage;
        }
    }
    public sealed class VerifiedUploadEnvelope
    {
        /// <summary>v1 没有清单，返回 null；不能把缺失当作零覆盖。</summary>
        public BatchCoverageManifest Coverage
        {
            get;
        }
        public IReadOnlyList<LogRecord> Records
        {
            get;
        }
        public ExportBookmark NextBookmark
        {
            get;
        }
        public int BeforeRecordIndex
        {
            get;
        }
        public ulong SourceBlockSequence
        {
            get;
        }
        private readonly byte[] sourceEpoch;
        public byte[] CopySourceEpoch() => (byte[])sourceEpoch.Clone();
        internal VerifiedUploadEnvelope(LogRecord[] records, ExportBookmark next, int before, ulong sequence, byte[] epoch, BatchCoverageManifest coverage = null)
        {
            Records = Array.AsReadOnly(records);
            NextBookmark = next;
            BeforeRecordIndex = before;
            SourceBlockSequence = sequence;
            sourceEpoch = epoch;
            Coverage = coverage;
        }
    }
}
