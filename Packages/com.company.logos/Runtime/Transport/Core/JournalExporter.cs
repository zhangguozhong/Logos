using System;
using System.IO;
using System.Security.Cryptography;
using Logos.Storage;
namespace Logos.Transport
{
    internal enum ExportBudgetFailure
    {
        None, Source, Encoded, Batches
    }
    /// <summary>仅由所有者工作线程使用的流式导出器。每步打开一个头、读取一个块或检查最多 16 条记录；不写源数据。</summary>
    public sealed class JournalExporter : IDisposable
    {
        private readonly JournalSession journal;
        private readonly RetrievalRequest request;
        private readonly TransferAccessController access;
        private readonly IStorageKeyProvider exportKeys;
        private readonly long revision, generation;
        private readonly int maxRecords;
        private readonly RecordCodec records = RecordCodec.CreateHistoricalReencoder();
        private readonly BlockCodec codec;
        private readonly byte[] plain = new byte[65536];
        private long day, blockOffset, sourceLimit, encodedLimit;
        private int batchLimit;
        private CommittedBlockCatalog catalog;
        private IJournalSourceReadAuthorization readAuthorization;
        internal void SetReadAuthorization(IJournalSourceReadAuthorization value)
        {
            readAuthorization = value;
            if (catalog != null)
                catalog.ReadAuthorization = value;
        }
        private DecodedBlock source;
        private ExportBookmark resume, authenticatedProgress, authenticatedSnapshot;
        internal ExportBookmark AuthenticatedSnapshot => authenticatedSnapshot;
        private bool trustedResume;
        internal void ApplyResume(TransferStateStore.AuthenticatedResume value)
        {
            if (catalog != null || SourceBytesRead != 0 || !value.IsCurrent(journal, request, revision, generation))
                throw new InvalidOperationException("Stale resume capability.");
            resume = value.Bookmark;
            day = resume.Day;
            trustedResume = true;
        }
        internal bool TryGetAuthenticatedProgress(out ExportBookmark value)
        {
            value = pending == null && selected == 0 ? authenticatedProgress : null;
            return value != null;
        }
        private int index, before, selected, plainLength;
        private KeyMaterial outputKey;
        private VerifiedFileHeader outputHeader;
        private BlockMetadata metadata;
        private CoverageAccumulator coverage;
        private ExportBatch pending;
        private TransferOutcome? terminal;
        internal bool IdentityCoverageGap { get; private set; }
        private bool anyMatch, disposed, closeFailed;
        internal ExportBudgetFailure BudgetFailure
        {
            get;
            private set;
        }
        public long SourceBytesRead
        {
            get;
            private set;
        }
        public long EncodedBytes
        {
            get;
            private set;
        }
        public int BatchCount
        {
            get;
            private set;
        }
        public JournalExporter(JournalSession journal, RetrievalRequest request, TransferAccessController access, long revision, IStorageKeyProvider exportKeys, ExportBookmark bookmark = null, int maxRecordsPerBatch = 256)
        {
            this.journal = journal ?? throw new ArgumentNullException(nameof(journal));
            this.request = request ?? throw new ArgumentNullException(nameof(request));
            this.access = access ?? throw new ArgumentNullException(nameof(access));
            this.exportKeys = exportKeys ?? throw new ArgumentNullException(nameof(exportKeys));
            if (revision < 0 || maxRecordsPerBatch < 1 || maxRecordsPerBatch > 256)
                throw new ArgumentException("Invalid revision or record limit.");
            this.revision = revision;
            generation = journal.CleanupGeneration;
            maxRecords = maxRecordsPerBatch;
            day = request.FirstQuotaDay;
            resume = bookmark;
            if (bookmark != null)
            {
                if (bookmark.Day < day || bookmark.Day >= day + request.QuotaDays || bookmark.CleanupGeneration != generation)
                    throw new ArgumentException("Bookmark is outside request/generation.");
                day = bookmark.Day;
            }
            sourceLimit = request.Limits.SourceBytes;
            encodedLimit = request.Limits.EncodedBytes;
            batchLimit = request.Limits.Batches;
            codec = new BlockCodec(records);
        }
        internal void RestrictRemainingBudgets(long source, long encoded, int batches)
        {
            if (catalog != null || SourceBytesRead != 0 || source < 0 || encoded < 0 || batches < 0 || source > request.Limits.SourceBytes || encoded > request.Limits.EncodedBytes || batches > request.Limits.Batches)
                throw new ArgumentException("Invalid remaining export budgets.");
            sourceLimit = source;
            encodedLimit = encoded;
            batchLimit = batches;
        }
        /// <summary>仅从此导出器的起点或给定书签推进。Complete/NoMatch 描述剩余捕获范围，不包括先前任务尝试。</summary>
        /// <remarks>NoMatch 表示此导出器扫描后缀期间未输出批次。持久化的整任务总量由调用者负责。</remarks>
        public TransferOutcome Step(JournalStepContext context, DateTime utc, out ExportBatch batch)
        {
            if (disposed)
                throw new ObjectDisposedException(nameof(JournalExporter));
            batch = null;
            if (context.Cancellation.IsCancellationRequested)
                return Finish(TransferOutcome.Cancelled);
            if (context.ShouldYield || context.StopRequested)
                return TransferOutcome.Progress;
            if (terminal.HasValue)
                return terminal.Value;
            if (journal.CleanupGeneration != generation)
                return Finish(TransferOutcome.LeaseCancelled);
            if (!access.IsAuthorized(request, revision, generation, utc))
                return Finish(TransferOutcome.Unauthorized);
            if (journal.Status != JournalStatus.Ready && catalog == null)
                return TransferOutcome.NotReady;
            try
            {
                if (catalog != null)
                    catalog.ValidateLease();
                if (pending != null)
                {
                    batch = pending;
                    return TransferOutcome.BatchReady;
                }
                if (catalog == null)
                    return OpenDay();
                if (source == null)
                    return ReadBlock();
                if (index == source.Records.Length && selected == 0)
                {
                    authenticatedProgress = new ExportBookmark(day, catalog.ConfirmedEnd, catalog.Header.Hash, catalog.Position, 0, generation);
                    source = null;
                    return TransferOutcome.Progress;
                }
                // 每步最多检查 16 条源记录，游标跨步保留；只有同时通过任务范围和授权的记录才进入输出。
                for (int work = 0; work < 16 && index < source.Records.Length && selected < maxRecords; work++)
                {
                    if (context.ShouldYield || context.StopRequested)
                        return TransferOutcome.Progress;
                    var record = source.Records[index];
                    if (request.Scope.DiagnosticUserId != null && record.Context.Device == null && record.Context.DiagnosticUserId == null
                        && access.HasUnverifiableIdentity(request, revision, generation, utc, record))
                        IdentityCoverageGap = true;
                    if (access.Matches(request, revision, generation, utc, record))
                    {
                        if (selected == 0)
                            BeginOutput(record);
                        if (!records.TryReencodeAuthenticated(source, index, plain, plainLength, plain.Length - plainLength, out int written, out var failure))
                            return Finish(TransferOutcome.Corrupt);
                        if (selected != 0 && !codec.TryIncludeRecord(metadata, record))
                            return Finish(TransferOutcome.Corrupt);
                        if (record.DiagnosticMetadata != null)
                            Wire.Put16(plain, 0, 2);
                        coverage.Observe(record);
                        selected++;
                        plainLength += written;
                    }
                    index++;
                }
                if (selected == 0)
                    return TransferOutcome.Progress;
                if (index < source.Records.Length && selected < maxRecords)
                    return TransferOutcome.Progress;
                if (context.ShouldYield || context.StopRequested)
                    return TransferOutcome.Progress;
                if (!access.IsAuthorized(request, revision, generation, utc))
                    return Finish(TransferOutcome.Unauthorized);
                if (BatchCount >= batchLimit)
                    return FinishBudget(ExportBudgetFailure.Batches);
                Wire.Put16(plain, 0, (ushort)(Wire.U16(plain, 0) == 2 ? 2 : 1));
                Wire.Put16(plain, 2, (ushort)selected);
                var result = UploadEnvelope.Seal(request, revision, generation, catalog, source, blockOffset, before, index, outputHeader, outputKey, codec, metadata, plain, plainLength, coverage.Finish(request.Scope));
                ResetOutput();
                if (result.Request.Body.Length > encodedLimit - EncodedBytes)
                    return FinishBudget(ExportBudgetFailure.Encoded);
                // 已封装待处理正文不跨网络等待固定源租约。
                CloseCatalog();
                source = null;
                EncodedBytes += result.Request.Body.Length;
                BatchCount++;
                anyMatch = true;
                pending = batch = result;
                return TransferOutcome.BatchReady;
            }
            catch (JournalSourceCreditException)
            {
                return TransferOutcome.WaitingCredit;
            }
            catch (JournalBlockedException ex)
            {
                return Finish(ex.Reason == JournalBlockReason.KeyUnavailable ? TransferOutcome.KeyUnavailable : TransferOutcome.Unavailable);
            }
            catch (JournalSnapshotBoundaryException)
            {
                return Finish(TransferOutcome.Unavailable);
            }
            catch (StorageAuthenticationException)
            {
                return Finish(TransferOutcome.Corrupt);
            }
            catch (CryptographicException)
            {
                return Finish(TransferOutcome.KeyUnavailable);
            }
            catch (StorageFormatException)
            {
                return Finish(TransferOutcome.Corrupt);
            }
            catch (FileNotFoundException)
            {
                return Finish(TransferOutcome.Missing);
            }
            catch (IOException) when (!closeFailed)
            {
                return Finish(TransferOutcome.Unavailable);
            }
            catch (InvalidOperationException)
            {
                return Finish(TransferOutcome.LeaseCancelled);
            }
        }
        // 按配额日打开已提交区间，恢复书签时还要检查原文件身份及可读取边界。
        private TransferOutcome OpenDay()
        {
            if (day >= request.FirstQuotaDay + request.QuotaDays)
                return Finish(IdentityCoverageGap ? TransferOutcome.IdentityUnverifiable : anyMatch ? TransferOutcome.Complete : TransferOutcome.NoMatch);
            var info = journal.GetTransferDayInfo(day);
            if (info == JournalTransferDayInfo.Missing)
                return Finish(TransferOutcome.Missing);
            if (info == JournalTransferDayInfo.Expired)
                return Finish(TransferOutcome.Expired);
            if (info == JournalTransferDayInfo.Empty)
            {
                if (resume != null)
                    return Finish(TransferOutcome.Unavailable);
                day++;
                return TransferOutcome.Progress;
            }
            if (sourceLimit - SourceBytesRead < 512)
                return FinishBudget(ExportBudgetFailure.Source);
            try
            {
                catalog = journal.OpenCatalogSnapshot(day, resume?.CapturedEnd, readAuthorization);
                SourceBytesRead += 512;
            }
            catch (StorageFormatException) when (resume != null)
            {
                return Finish(TransferOutcome.Unavailable);
            }
            if (catalog.CleanupGeneration != generation || (resume != null && !resume.HasHeader(catalog.Header.Hash)))
                return Finish(TransferOutcome.Unavailable);
            if (trustedResume && resume != null)
                catalog.ResumeAuthenticatedBoundary(resume.BlockOffset);
            authenticatedSnapshot = new ExportBookmark(day, catalog.ConfirmedEnd, catalog.Header.Hash, 512, 0, generation);
            return TransferOutcome.Progress;
        }
        private TransferOutcome ReadBlock()
        {
            if (catalog.Position == catalog.ConfirmedEnd)
            {
                if (resume != null && (resume.BlockOffset != catalog.Position || resume.NextRecordIndex != 0))
                    return Finish(TransferOutcome.Unavailable);
                CloseCatalog();
                day++;
                resume = null;
                return TransferOutcome.Progress;
            }
            blockOffset = catalog.Position;
            bool found = false, exhausted = false;
            int read = 0;
            try
            {
                found = catalog.TryReadNext(sourceLimit - SourceBytesRead, out source, out read, out exhausted);
            }
            finally
            {
                SourceBytesRead += read;
            }
            if (exhausted)
                return FinishBudget(ExportBudgetFailure.Source);
            if (!found)
                return Finish(TransferOutcome.Corrupt);
            index = 0;
            if (resume != null)
            {
                if (blockOffset < resume.BlockOffset)
                {
                    if (catalog.Position > resume.BlockOffset)
                        return Finish(TransferOutcome.Unavailable);
                    source = null;
                    return TransferOutcome.Progress;
                }
                if (blockOffset != resume.BlockOffset || resume.NextRecordIndex > source.Records.Length)
                    return Finish(TransferOutcome.Unavailable);
                index = resume.NextRecordIndex;
                resume = null;
            }
            before = index;
            // Only authenticated block payloads can establish a user binding. Legacy
            // blocks still need record-level checks to report gaps within the grant.
            var binding = source.Records[0].Context;
            if (request.Scope.DiagnosticUserId != null && binding.DiagnosticUserId != request.Scope.DiagnosticUserId
                && (binding.DiagnosticUserId != null || binding.Device != null))
                index = source.Records.Length;
            return TransferOutcome.Progress;
        }
        private void BeginOutput(LogRecord record)
        {
            outputKey = JournalIo.Acquire(exportKeys, null);
            codec.BeginIndependentContainer();
            byte[] directoryId = new byte[16];
            using (var random = RandomNumberGenerator.Create())
                random.GetBytes(directoryId);
            outputHeader = FileHeaderCodec.Create(day, 262144, 131072, 1, directoryId, outputKey);
            metadata = codec.CreateBlockMetadata(outputHeader, outputKey, record);
            plainLength = 4;
        }
        private void ResetOutput()
        {
            Wire.Clear(plain);
            outputKey?.Dispose();
            outputKey = null;
            outputHeader = null;
            metadata = null;
            selected = 0;
            coverage = default(CoverageAccumulator);
            plainLength = 0;
        }
        /// <summary>只有收到匹配回执后，调用者才持久化此批次的下一书签。仅尝试发送不会推进书签。</summary>
        public bool Acknowledge(ExportBatch batch, UploadResult result)
        {
            if (disposed)
                throw new ObjectDisposedException(nameof(JournalExporter));
            // 确认时还要求是本导出器交出的那个批次对象，以免迟到的回执推进另一批的游标。
            if (pending == null || !ReferenceEquals(batch, pending) || !pending.Request.IsAcknowledged(result))
                return false;
            resume = pending.NextBookmark;
            day = resume.Day;
            trustedResume = true;
            authenticatedProgress = resume;
            pending = null;
            index = before = 0;
            return true;
        }
        private void CloseCatalog()
        {
            try
            {
                catalog?.Dispose();
                catalog = null;
                closeFailed = false;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                closeFailed = true;
                throw;
            }
        }
        private TransferOutcome FinishBudget(ExportBudgetFailure failure)
        {
            BudgetFailure = failure;
            return Finish(TransferOutcome.PartialBudget);
        }
        private TransferOutcome Finish(TransferOutcome outcome)
        {
            CloseCatalog();
            source = null;
            ResetOutput();
            pending = null;
            terminal = outcome;
            return outcome;
        }
        public void Dispose()
        {
            if (disposed)
                return;
            CloseCatalog();
            source = null;
            pending = null;
            ResetOutput();
            codec.Dispose();
            records.Dispose();
            disposed = true;
        }
    }
}
