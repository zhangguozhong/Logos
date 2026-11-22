using System;
using System.Collections.Generic;
using System.Text;

namespace Logos.Storage
{
    /// <summary>工作线程独占的固定白名单编码器。不跨调用保留记录或借用批次。</summary>
    public sealed partial class RecordCodec : IDisposable
    {
        private readonly int maxMessage,
            maxStack,
            coreByteLimit;
        private readonly char[] sanitized;
        private readonly ILogTextSanitizer sanitizer;
        private bool disposed;
        private static readonly Encoding WriteUtf8 = new UTF8Encoding(false, false),
            ReadUtf8 = new UTF8Encoding(false, true);
        public int MaximumEncodedSingleRecordBytes
        {
            get;
        }
        public int WorkspaceBytes => sanitized.Length * sizeof(char);

        public RecordCodec(LoggingOptions options, ILogTextSanitizer sanitizer = null)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            options.Validate();
            maxMessage = options.MaxMessageChars;
            maxStack = options.MaxStackChars;
            coreByteLimit = options.EffectiveRecordByteLimit;
            int maxTextUnits = Math.Min(
                checked(maxMessage + maxStack),
                (coreByteLimit - LoggingOptions.FixedRecordBytes) / 2
            );
            // 元数据也占同一 UTF-16 预算，其 wire/cost 比不超过文本的 3/2，原最坏上界仍覆盖 trailer。
            MaximumEncodedSingleRecordBytes = checked(
                4 + 72 + 6 * LogRecord.MaxIdLength * 3 + 3 * maxTextUnits
            );
            if (MaximumEncodedSingleRecordBytes > 65536)
                throw new ArgumentException(
                    "UTF8 worst case of legal Core records must fit a 64 KiB block.",
                    nameof(options)
                );
            this.sanitizer = sanitizer ?? new CredentialTextSanitizer();
            sanitized = new char[Math.Max(maxMessage, maxStack)];
        }

        /// <summary>向调用者暂存区写入一条带长度前缀的记录；正文 schema 和数量前缀另占 4 字节。</summary>
        public bool TryEncode(
            LogRecord record,
            byte[] destination,
            int offset,
            int capacity,
            out int written,
            out RecordEncodeFailure failure
        )
        => TryEncodeCore(record, destination, offset, capacity, out written, out failure, false);

        internal bool TryEncodeSanitized(LogRecord record, byte[] destination, int offset, int capacity,
            out int written, out RecordEncodeFailure failure)
            => TryEncodeCore(record, destination, offset, capacity, out written, out failure, true);

        private bool TryEncodeCore(LogRecord record, byte[] destination, int offset, int capacity,
            out int written, out RecordEncodeFailure failure, bool alreadySanitized)
        {
            if (disposed)
                throw new ObjectDisposedException(nameof(RecordCodec));
            written = 0;
            failure = RecordEncodeFailure.InvalidRecord;
            if (record == null)
                return false;
            if (
                destination == null
                || offset < 0
                || capacity < 0
                || offset > destination.Length - capacity
            )
                throw new ArgumentException("Invalid destination slice.");
            if (
                record.Message.Length > maxMessage
                || record.Stack.Length > maxStack
                || RecordMemoryCost.Of(record) > coreByteLimit
            )
            {
                failure = RecordEncodeFailure.RecordTooLarge;
                return false;
            }
            int required =
                72
                + WriteUtf8.GetByteCount(record.SourceId)
                + WriteUtf8.GetByteCount(record.EventTypeId)
                + WriteUtf8.GetByteCount(record.RecordId)
                + ((historical || alreadySanitized) ? WriteUtf8.GetByteCount(record.Message) : 3 * record.Message.Length)
                + ((historical || alreadySanitized) ? WriteUtf8.GetByteCount(record.Stack) : 3 * record.Stack.Length)
                + WriteUtf8.GetByteCount(record.Context.AppSessionId)
                + WriteUtf8.GetByteCount(record.Context.UnityInstanceId)
                + WriteUtf8.GetByteCount(record.Context.UnityVisitId)
                + DiagnosticSize(record.DiagnosticMetadata);
            if (required > 65532 || required > capacity)
            {
                failure = RecordEncodeFailure.DestinationTooSmall;
                return false;
            }
            int at = offset;
            try
            {
                Wire.Put32(destination, at, 0);
                at += 4;
                destination[at++] = (byte)record.Level;
                destination[at++] = (byte)(
                    (record.MessageTruncated ? 1 : 0) | (record.StackTruncated ? 2 : 0)
                );
                Wire.Put16(destination, at, 0);
                at += 2;
                Wire.Put64(destination, at, record.Sequence);
                at += 8;
                Wire.Put64(destination, at, record.UtcTimestamp.Ticks);
                at += 8;
                Wire.Put64(destination, at, record.MonotonicTimestamp);
                at += 8;
                Wire.Put64(destination, at, record.Context.IdentityContextVersion);
                at += 8;
                WriteText(record.SourceId, destination, ref at);
                WriteText(record.EventTypeId, destination, ref at);
                WriteText(record.RecordId, destination, ref at);
                if (historical || alreadySanitized)
                {
                    WriteText(record.Message, destination, ref at);
                    WriteText(record.Stack, destination, ref at);
                }
                else if (
                    !WriteSanitized(record.Message, destination, ref at)
                    || !WriteSanitized(record.Stack, destination, ref at)
                )
                {
                    failure = RecordEncodeFailure.SanitizeFailed;
                    Array.Clear(destination, offset, at - offset);
                    return false;
                }
                WriteText(record.Context.AppSessionId, destination, ref at);
                WriteText(record.Context.UnityInstanceId, destination, ref at);
                WriteText(record.Context.UnityVisitId, destination, ref at);
                WriteDiagnostic(record.DiagnosticMetadata, destination, ref at);
                Wire.Put32(destination, offset, (uint)(at - offset - 4));
                written = at - offset;
                failure = RecordEncodeFailure.None;
                return true;
            }
            finally
            {
                Array.Clear(sanitized, 0, sanitized.Length);
            }
        }

        // 先把文本交给脱敏器，再把允许写出的字符编码到目标区间；脱敏失败会使整条记录编码失败。
        private bool WriteSanitized(string input, byte[] destination, ref int at)
        {
            int n;
            try
            {
                if (
                    !sanitizer.TrySanitize(input, sanitized, input.Length, out n)
                    || n < 0
                    || n > input.Length
                )
                    return false;
            }
            catch (Exception)
            {
                return false;
            }
            int bytes = WriteUtf8.GetByteCount(sanitized, 0, n);
            Wire.Put32(destination, at, (uint)bytes);
            at += 4;
            at += WriteUtf8.GetBytes(sanitized, 0, n, destination, at);
            Array.Clear(sanitized, 0, n);
            return true;
        }

        private static void WriteText(string text, byte[] destination, ref int at)
        {
            int bytes = WriteUtf8.GetByteCount(text);
            Wire.Put32(destination, at, (uint)bytes);
            at += 4;
            at += WriteUtf8.GetBytes(text, 0, text.Length, destination, at);
        }

        internal static void WriteBlockContext(LogContext context, byte[] destination)
        {
            int at = 4;
            WriteText(context.DiagnosticUserId ?? "", destination, ref at);
            var device = context.Device;
            Wire.Put16(destination, at, (ushort)(device == null ? 0 : 1));
            at += 2;
            if (device == null)
                return;
            WriteText(device.Platform, destination, ref at);
            WriteText(device.Model, destination, ref at);
            WriteText(device.OperatingSystem, destination, ref at);
            WriteText(device.AppVersion, destination, ref at);
            WriteText(device.UnityVersion, destination, ref at);
        }

        // 仅供测试便利。生产路径将 TryEncode 结果直接追加到预分配的类别槽位。
        internal byte[] Encode(IReadOnlyList<LogRecord> records)
        {
            if (records == null || records.Count < 1 || records.Count > 256)
                throw new StorageFormatException();
            byte[] buffer = new byte[65536];
            try
            {
                Wire.Put16(buffer, 0, 1);
                Wire.Put16(buffer, 2, (ushort)records.Count);
                int at = 4;
                foreach (var record in records)
                {
                    if (record.DiagnosticMetadata != null)
                        Wire.Put16(buffer, 0, 2);
                    int n;
                    RecordEncodeFailure reason;
                    if (!TryEncode(record, buffer, at, buffer.Length - at, out n, out reason))
                    {
                        if (reason == RecordEncodeFailure.SanitizeFailed)
                            throw new SanitizeFailedException();
                        throw new StorageFormatException();
                    }
                    at += n;
                }
                return Wire.Slice(buffer, 0, at);
            }
            finally
            {
                Wire.Clear(buffer);
            }
        }

        public LogRecord[] Decode(byte[] body) => Decode(body, 0, body?.Length ?? 0);

        public LogRecord[] Decode(byte[] body, int offset, int length)
        {
            if (disposed)
                throw new ObjectDisposedException(nameof(RecordCodec));
            if (
                body == null
                || offset < 0
                || length < 79
                || length > 65536
                || offset > body.Length - length
            )
                throw new StorageFormatException();
            try
            {
                int at = offset,
                    end = offset + length;
                int schema = Wire.U16(body, at);
                if (schema < 1 || schema > 3)
                    throw new StorageFormatException();
                int count = Wire.U16(body, at + 2);
                at += 4;
                if (count < 1 || count > 256 || count > (length - 4) / 75)
                    throw new StorageFormatException();
                string user = null;
                LogDeviceSnapshot device = null;
                if (schema == 3)
                {
                    user = ReadText(body, ref at, 128, end);
                    if (user.Length == 0)
                        user = null;
                    if (end - at < 2)
                        throw new StorageFormatException();
                    int present = Wire.U16(body, at);
                    at += 2;
                    if (present > 1 || (present == 0 && user == null))
                        throw new StorageFormatException();
                    if (present == 1)
                        device = new LogDeviceSnapshot(ReadDeviceField(body, ref at, end), ReadDeviceField(body, ref at, end),
                            ReadDeviceField(body, ref at, end), ReadDeviceField(body, ref at, end), ReadDeviceField(body, ref at, end));
                }
                var records = new LogRecord[count];
                for (int i = 0; i < count; i++)
                {
                    if (end - at < 4)
                        throw new StorageFormatException();
                    uint recordLength = Wire.U32(body, at);
                    at += 4;
                    if (recordLength < 71 || recordLength > end - at)
                        throw new StorageFormatException();
                    int recordEnd = at + (int)recordLength;
                    byte level = body[at++],
                        flags = body[at++];
                    if (level > 5 || flags > 3 || Wire.U16(body, at) != 0)
                        throw new StorageFormatException();
                    at += 2;
                    long sequence = Wire.I64(body, at);
                    at += 8;
                    long utc = Wire.I64(body, at);
                    at += 8;
                    long mono = Wire.I64(body, at);
                    at += 8;
                    long version = Wire.I64(body, at);
                    at += 8;
                    string source = ReadText(body, ref at, 128, recordEnd),
                        ev = ReadText(body, ref at, 128, recordEnd),
                        id = ReadText(body, ref at, 128, recordEnd),
                        message = ReadText(body, ref at, LogRecord.MaxMessageLength, recordEnd),
                        stack = ReadText(body, ref at, LogRecord.MaxStackLength, recordEnd),
                        app = ReadText(body, ref at, 128, recordEnd),
                        instance = ReadText(body, ref at, 128, recordEnd),
                        visit = ReadText(body, ref at, 128, recordEnd);
                    DiagnosticRecordMetadata diagnostic = schema >= 2 && at < recordEnd ? ReadDiagnostic(body, ref at, recordEnd, user, device) : null;
                    if (at != recordEnd)
                        throw new StorageFormatException();
                    records[i] = new LogRecord(
                        (LogLevel)level,
                        source,
                        ev,
                        id,
                        sequence,
                        new DateTime(utc, DateTimeKind.Utc),
                        mono,
                        message,
                        stack,
                        new LogContext(app, instance, visit, version, user, device),
                        (flags & 1) != 0,
                        (flags & 2) != 0,
                        null, false, diagnostic
                    );
                }
                if (at != end)
                    throw new StorageFormatException();
                return records;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is OverflowException)
            {
                throw new StorageFormatException();
            }
        }

        private static string ReadDeviceField(byte[] body, ref int at, int end)
        {
            string field = ReadText(body, ref at, LogDeviceSnapshot.MaxFieldLength, end);
            if (string.IsNullOrWhiteSpace(field))
                throw new StorageFormatException();
            return field;
        }

        private static string ReadText(byte[] body, ref int at, int maxChars, int end)
        {
            if (end - at < 4)
                throw new StorageFormatException();
            uint size = Wire.U32(body, at);
            at += 4;
            if (size > (uint)(maxChars * 3) || size > end - at)
                throw new StorageFormatException();
            string value = ReadUtf8.GetString(body, at, (int)size);
            at += (int)size;
            if (value.Length > maxChars)
                throw new StorageFormatException();
            return value;
        }

        public void Dispose()
        {
            if (disposed)
                return;
            Array.Clear(sanitized, 0, sanitized.Length);
            disposed = true;
        }
    }
}
