using System;
using System.Text;
namespace Logos.Storage
{
    internal enum StorageOwnershipRole
    {
        Direct, FirstOfAggregate, SummaryOfAggregate, IncidentSynthetic
    }
    internal readonly struct StorageOwnershipToken
    {
        internal readonly StorageOwnershipRole Role;
        internal readonly int Bucket;
        internal readonly long Generation;
        internal bool IsSynthetic => Role == StorageOwnershipRole.SummaryOfAggregate || Role == StorageOwnershipRole.IncidentSynthetic;
        internal StorageOwnershipToken(StorageOwnershipRole role, int bucket, long generation)
        {
            Role = role;
            Bucket = bucket;
            Generation = generation;
        }
    }

    // 原输入只借用到编码完成；之后仅保留空正文描述符、已脱敏字节及有界身份。
    internal sealed class PreparedRecordSlot : IDisposable
    {
        internal readonly byte[] Bytes = new byte[StorageRuntimeLimits.MaxPlainBlockBytes - 4];
        internal readonly byte[] Signature = new byte[2048];
        private readonly char[] sampleChars = new char[256];
        private readonly Decoder sampleDecoder = Encoding.UTF8.GetDecoder();
        internal LogRecord Descriptor
        {
            get;
            private set;
        }
        internal int Length
        {
            get;
            private set;
        }
        internal int SignatureLength
        {
            get;
            private set;
        }
        internal string Sample
        {
            get;
            private set;
        }
        internal StorageOwnershipToken Token;
        internal bool Classified;
        private LogStorageOutcome terminal;
        internal bool HasOutcome => terminal.Sequence != 0;
        internal bool IsEmpty => Descriptor == null;
        internal int WorkspaceBytes => Bytes.Length + Signature.Length + sampleChars.Length * sizeof(char) + 32768;
        internal bool Prepare(LogRecord record, RecordCodec codec, bool synthetic, out RecordEncodeFailure failure)
        {
            if (!IsEmpty)
                throw new InvalidOperationException("Prepared slot is occupied.");
            bool ok = synthetic ? codec.TryEncodeSanitized(record, Bytes, 0, Bytes.Length, out int length, out failure)
                : codec.TryEncode(record, Bytes, 0, Bytes.Length, out length, out failure);
            if (ok && length + record.Context.BlockSnapshotBytes > Bytes.Length)
            {
                failure = RecordEncodeFailure.RecordTooLarge;
                ok = false;
            }
            if (!ok)
            {
                Array.Clear(Bytes, 0, Bytes.Length);
                return false;
            }
            Length = length;
            Descriptor = new LogRecord(record.Level, record.SourceId, record.EventTypeId, record.RecordId,
                record.Sequence, record.UtcTimestamp, record.MonotonicTimestamp, "", "", record.Context,
                record.MessageTruncated, record.StackTruncated, record.QuotaDayTag, false, record.DiagnosticMetadata);
            if (!synthetic)
                // 签名从已编码的脱敏数据中截取，避免在背压重试时再次接触输入原文或第二次调用脱敏器。
                BuildSignature();
            return true;
        }
        private void BuildSignature()
        {
            // 线格式布局已由编码器验证；跳过 ID、序号和时间，保留完整字段长度与字节。
            int at = 40;
            int source = at;
            Skip(ref at);
            Skip(ref at);
            int sourceEnd = at;
            Skip(ref at);
            int text = at;
            int messageLength = checked((int)Wire.U32(Bytes, at));
            Skip(ref at);
            Skip(ref at);
            int total = 18 + (sourceEnd - source) + Length - text;
            if (total > Signature.Length)
            {
                SignatureLength = 0;
                return;
            }
            sampleDecoder.Reset();
            sampleDecoder.Convert(Bytes, text + 4, messageLength, sampleChars, 0, sampleChars.Length, false, out _, out int used, out _);
            Sample = new string(sampleChars, 0, used);
            Array.Clear(sampleChars, 0, sampleChars.Length);
            Signature[0] = Bytes[4];
            Signature[1] = Bytes[5];
            Wire.Put64(Signature, 2, Descriptor.Context.IdentityContextVersion);
            Wire.Put64(Signature, 10, Descriptor.QuotaDayTag.Value.DayId);
            Buffer.BlockCopy(Bytes, source, Signature, 18, sourceEnd - source);
            Buffer.BlockCopy(Bytes, text, Signature, 18 + sourceEnd - source, Length - text);
            SignatureLength = total;
        }
        private void Skip(ref int at)
        {
            at += 4 + checked((int)Wire.U32(Bytes, at));
        }
        internal void Publish(ulong sequence, AdmissionReason reason)
        {
            if (!HasOutcome && !IsEmpty)
                terminal = new LogStorageOutcome(sequence, LogStorageOutcomeKind.Dropped, reason, Counts(Descriptor.Level, 1));
        }
        internal bool Peek(out LogStorageOutcome outcome)
        {
            outcome = terminal;
            return HasOutcome;
        }
        internal bool Ack(ulong sequence)
        {
            if (!HasOutcome || sequence != terminal.Sequence)
                return false;
            Clear();
            return true;
        }
        internal static LogLevelCounts Counts(LogLevel level, long count) => new LogLevelCounts(level == LogLevel.Trace ? count : 0,
            level == LogLevel.Debug ? count : 0, level == LogLevel.Info ? count : 0, level == LogLevel.Warn ? count : 0,
            level == LogLevel.Error ? count : 0, level == LogLevel.Fatal ? count : 0);
        internal void Clear()
        {
            Array.Clear(Bytes, 0, Bytes.Length);
            Array.Clear(Signature, 0, Signature.Length);
            Array.Clear(sampleChars, 0, sampleChars.Length);
            sampleDecoder.Reset();
            Descriptor = null;
            Sample = null;
            Length = SignatureLength = 0;
            Token = default;
            Classified = false;
            terminal = default;
        }
        public void Dispose() => Clear();
    }
}
