using System;
using System.Globalization;
using System.Threading;
namespace Logos
{
    public sealed class LogRecord
    {
        public DiagnosticRecordMetadata DiagnosticMetadata
        {
            get;
        }
        public const int MaxIdLength = 128;
        public const int MaxMessageLength = 8192;
        public const int MaxStackLength = 16384;
        public LogLevel Level
        {
            get;
        }
        public QuotaDayTag? QuotaDayTag
        {
            get;
        }
        public string SourceId
        {
            get;
        }
        public string EventTypeId
        {
            get;
        }
        private string recordId;
        private readonly string recordPrefix;
        // 公开读取仍返回稳定标识。流水线只保存会话前缀和序号，首次编码或访问时才格式化。
        public string RecordId
        {
            get
            {
                string value = Volatile.Read(ref recordId);
                if (value != null)
                    return value;
                value = recordPrefix + Sequence.ToString(CultureInfo.InvariantCulture);
                return Interlocked.CompareExchange(ref recordId, value, null) ?? value;
            }
        }
        public long Sequence
        {
            get;
        }
        // UTC 用于跨系统定位事件；另存的单调时间用于实例内的耗时与先后判断，二者的起点不同。
        public DateTime UtcTimestamp
        {
            get;
        }
        public long MonotonicTimestamp
        {
            get;
        }
        public string Message
        {
            get;
        }
        public string Stack
        {
            get;
        }
        public LogContext Context
        {
            get;
        }
        public bool MessageTruncated
        {
            get;
        }
        public bool StackTruncated
        {
            get;
        }
        public LogRecord(LogLevel level, string sourceId, string eventTypeId, string recordId, long sequence, DateTime utcTimestamp, long monotonicTimestamp, string message, string stack, LogContext context, bool messageTruncated = false, bool stackTruncated = false, QuotaDayTag? quotaDayTag = null, DiagnosticRecordMetadata diagnosticMetadata = null)
            : this(level, sourceId, eventTypeId, recordId, sequence, utcTimestamp, monotonicTimestamp, message, stack, context,
                messageTruncated, stackTruncated, quotaDayTag, false, diagnosticMetadata)
        {
            if (diagnosticMetadata != null && diagnosticMetadata.Kind != DiagnosticRecordKind.Event)
                throw new ArgumentException("Synthetic records require trusted construction.", nameof(diagnosticMetadata));
        }
        // 仅内部流水线使用延迟路径；不改变外部显式 ID 构造器的验证和异常时机。
        internal LogRecord(LogLevel level, string sourceId, string eventTypeId, string recordId, long sequence, DateTime utcTimestamp,
            long monotonicTimestamp, string message, string stack, LogContext context, bool messageTruncated, bool stackTruncated,
            QuotaDayTag? quotaDayTag, bool deferRecordId, DiagnosticRecordMetadata diagnosticMetadata = null)
        {
            if (level < LogLevel.Trace || level > LogLevel.Fatal)
                throw new ArgumentOutOfRangeException(nameof(level));
            if (sequence < 0)
                throw new ArgumentOutOfRangeException(nameof(sequence));
            if (monotonicTimestamp < 0)
                throw new ArgumentOutOfRangeException(nameof(monotonicTimestamp));
            if (utcTimestamp.Kind != DateTimeKind.Utc)
                throw new ArgumentException("UTC DateTime required.", nameof(utcTimestamp));
            Context = context ?? throw new ArgumentNullException(nameof(context));
            SourceId = LogContext.RequireId(sourceId, nameof(sourceId));
            string identity = LogContext.RequireId(recordId, nameof(recordId));
            if (deferRecordId)
            {
                // Int64 的非负序号最多 19 位，避免把输入验证延后到消费者线程。
                if (identity.Length > MaxIdLength - 19)
                    throw new ArgumentException("Record prefix exceeds model limit.", nameof(recordId));
                recordPrefix = identity;
            }
            else
                this.recordId = identity;
            EventTypeId = BoundedText(eventTypeId, MaxIdLength, nameof(eventTypeId));
            Message = BoundedText(message, MaxMessageLength, nameof(message));
            Stack = BoundedText(stack, MaxStackLength, nameof(stack));
            diagnosticMetadata?.ValidateContainer(level, Context, identity);
            DiagnosticMetadata = diagnosticMetadata;
            Level = level;
            QuotaDayTag = quotaDayTag;
            Sequence = sequence;
            UtcTimestamp = utcTimestamp;
            MonotonicTimestamp = monotonicTimestamp;
            MessageTruncated = messageTruncated;
            StackTruncated = stackTruncated;
        }

        private static string BoundedText(string value, int limit, string name)
        {
            if (value != null && value.Length > limit)
                throw new ArgumentException("Text exceeds model limit.", name);
            return value ?? string.Empty;
        }
    }
}
