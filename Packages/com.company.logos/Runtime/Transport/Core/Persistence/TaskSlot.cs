using System;
namespace Logos.Transport
{
    public enum TransferTaskState : byte
    {
        Empty, Queued, Exporting, Pending, Retry, Complete, Canceled, Partial, Blocked, Failed
    }
    public enum TransferTerminalReason : byte
    {
        None, Complete, NoMatch, NotReady, Missing, Expired, Corrupt, KeyUnavailable, LeaseCancelled, Cancelled, Unauthorized, PartialSourceBudget, PartialEncodedBudget, PartialBatchBudget, DailyUploadBudget, RetryExhausted, TooLarge, Protocol, Cleared, DestinationChanged, ReauthorizationRequired, SequenceExhausted, IdentityUnverifiable
    }
    // 同一槽位可反复分配，Generation 用于区分占用过该位置的不同任务。
    internal sealed class TaskSlot
    {
        internal RetrievalRequest Request
        {
            get;
        }
        internal bool IdentityCoverageGap { get; }
        internal ulong Generation
        {
            get;
        }
        internal long Revision
        {
            get;
        }
        internal long CleanupGeneration
        {
            get;
        }
        internal TransferTaskState State
        {
            get;
        }
        internal bool AutoError
        {
            get;
        }
        internal int MaxRecords
        {
            get;
        }
        internal int GeneratedBatches
        {
            get;
        }
        // 表示已发出的读取授权，而非实际读取量；不可退款。
        internal long SourceUsed
        {
            get;
        }
        internal long EncodedUsed
        {
            get;
        }
        internal ExportBookmark Bookmark
        {
            get;
        }
        internal int Attempts
        {
            get;
        }
        internal int Splits
        {
            get;
        }
        internal TransferTerminalReason Reason
        {
            get;
        }
        internal long RetryUtc
        {
            get;
        }
        internal static void ValidateContext(string value)
        {
            if (value == null)
                return;
            try
            {
                if (value.Length > 128 || new System.Text.UTF8Encoding(false, true).GetByteCount(value) > 384)
                    throw new ArgumentException("Context too long.");
            }
            catch (System.Text.EncoderFallbackException)
            {
                throw new ArgumentException("Strict UTF8 required.");
            }
        }

        internal TaskSlot(RetrievalRequest request, ulong generation, long revision, long cleanupGeneration, TransferTaskState state = TransferTaskState.Queued, bool autoError = false, int maxRecords = 256, int generatedBatches = 0, long sourceUsed = 0, long encodedUsed = 0, ExportBookmark bookmark = null, int attempts = 0, int splits = 0, TransferTerminalReason reason = TransferTerminalReason.None, long retryUtc = 0, bool identityCoverageGap = false)
        {
            if (request == null || generation == 0 || revision < 0 || cleanupGeneration < 0 || state < TransferTaskState.Queued || state > TransferTaskState.Failed || maxRecords < 1 || maxRecords > 256 || generatedBatches < 0 || generatedBatches > request.Limits.Batches || sourceUsed < 0 || sourceUsed > request.Limits.SourceBytes || encodedUsed < 0 || encodedUsed > request.Limits.EncodedBytes || attempts < 0 || attempts > 5 || splits < 0 || splits > 8 || reason > TransferTerminalReason.IdentityUnverifiable || ((identityCoverageGap || reason == TransferTerminalReason.IdentityUnverifiable) && request.Scope.DiagnosticUserId == null) || retryUtc < 0 || retryUtc > DateTime.MaxValue.Ticks)
                throw new ArgumentException("Invalid bounded task slot.");
            ValidateContext(request.Scope.AppSessionId);
            ValidateContext(request.Scope.UnityInstanceId);
            ValidateContext(request.Scope.UnityVisitId);
            ValidateContext(request.Scope.DiagnosticUserId);
            if (bookmark != null && (bookmark.CleanupGeneration != cleanupGeneration || bookmark.Day < request.FirstQuotaDay || bookmark.Day >= request.FirstQuotaDay + request.QuotaDays))
                throw new ArgumentException("Bookmark outside task.");
            Request = request;
            IdentityCoverageGap = identityCoverageGap;
            Generation = generation;
            Revision = revision;
            CleanupGeneration = cleanupGeneration;
            State = state;
            AutoError = autoError;
            MaxRecords = maxRecords;
            GeneratedBatches = generatedBatches;
            SourceUsed = sourceUsed;
            EncodedUsed = encodedUsed;
            Bookmark = bookmark;
            Attempts = attempts;
            Splits = splits;
            Reason = reason;
            RetryUtc = retryUtc;
        }
    }
}
