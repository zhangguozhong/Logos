using System;

namespace Logos.Storage
{
    // 账本为暂存记录保留序号、级别及所有权令牌；合成记录可占据块内位置，但不能凭空增加一个原始输入的结果。
    /// <summary>一个类别的固定所有权账本，仅含数值数据，由工作线程独占。</summary>
    internal sealed class StorageOutcomeLedger : IDisposable
    {
        private struct Entry
        {
            internal long RecordSequence;
            internal int EncodedBytes;
            internal LogLevel Level;
            internal StorageOwnershipToken Token;
        }

        private readonly Entry[] entries = new Entry[StorageRuntimeLimits.MaxRecordsPerBlock];
        private int count;
        private long trace, debug, info, warn, error, fatal;
        private ulong lastSequence;
        private LogStorageOutcome pending;
        private bool hasPending, disposed;
        internal int Count => count;
        internal int DirectCount
        {
            get;
            private set;
        }
        internal long SequenceAt(int index) => entries[index].RecordSequence;
        internal StorageOwnershipToken TokenAt(int index) => entries[index].Token;
        internal bool CanAdd => !disposed && !hasPending && count < entries.Length && lastSequence != ulong.MaxValue;
        internal int WorkspaceBytes => StorageRuntimeLimits.MaxRecordsPerBlock * 40;
        // 六个计数器、结果、游标和标记的保守有界预算，不含 CLR 对象头或填充。
        internal int ControlPayloadBytes => 160;

        internal bool TryAdd(long recordSequence, LogLevel level, int encodedBytes, StorageOwnershipToken token = default)
        {
            Check();
            if (!CanAdd || recordSequence < 0 || level < LogLevel.Trace || level > LogLevel.Fatal
                || encodedBytes < 1 || encodedBytes > StorageRuntimeLimits.MaxPlainBlockBytes - 4)
                return false;
            entries[count++] = new Entry { RecordSequence = recordSequence, Level = level, EncodedBytes = encodedBytes, Token = token };
            if (token.IsSynthetic)
                return true;
            DirectCount++;
            switch (level)
            {
                case LogLevel.Trace:
                    trace++;
                    break;
                case LogLevel.Debug:
                    debug++;
                    break;
                case LogLevel.Info:
                    info++;
                    break;
                case LogLevel.Warn:
                    warn++;
                    break;
                case LogLevel.Error:
                    error++;
                    break;
                case LogLevel.Fatal:
                    fatal++;
                    break;
            }
            return true;
        }

        /// <summary>协调器提供共享的严格递增序列，并在接收新所有权前预留剩余的两个序列值。</summary>
        internal bool TryPublish(ulong sequence, LogStorageOutcomeKind kind, AdmissionReason reason)
        {
            Check();
            if (hasPending || DirectCount == 0 || sequence == 0 || sequence <= lastSequence
                || kind < LogStorageOutcomeKind.Committed || kind > LogStorageOutcomeKind.Dropped
                || (uint)reason > (uint)AdmissionReason.Cleared
                || (kind == LogStorageOutcomeKind.Dropped && reason == AdmissionReason.Accepted)
                || (kind == LogStorageOutcomeKind.Committed && reason != AdmissionReason.Accepted))
                return false;
            pending = new LogStorageOutcome(sequence, kind, reason, new LogLevelCounts(trace, debug, info, warn, error, fatal));
            lastSequence = sequence;
            hasPending = true;
            return true;
        }

        internal bool TryPeek(out LogStorageOutcome outcome)
        {
            Check();
            outcome = pending;
            return hasPending;
        }

        internal bool Ack(ulong sequence)
        {
            Check();
            if (!hasPending || pending.Sequence != sequence)
                return false;
            Clear();
            return true;
        }

        internal void Clear()
        {
            Array.Clear(entries, 0, entries.Length);
            count = 0;
            DirectCount = 0;
            trace = debug = info = warn = error = fatal = 0;
            pending = default;
            hasPending = false;
            // 槽位复用时保留 lastSequence，包括序列耗尽状态。
        }

        private void Check()
        {
            if (disposed)
                throw new ObjectDisposedException(nameof(StorageOutcomeLedger));
        }

        public void Dispose()
        {
            if (disposed)
                return;
            Clear();
            lastSequence = 0;
            disposed = true;
        }
    }
}
