using System;
using System.Threading;
namespace Logos
{
    internal enum QueueTakeDecision
    {
        InvalidInput, Empty, ContentionRejected, Taken
    }
    // Reserved 只授权有界构造；只有 Committed 才表示队列接收。
    internal enum QueueDecision
    {
        InvalidInput, RecordTooLarge, Full, Closed, ContentionRejected, InvalidReservation, Reserved, Committed, NotReady, QuotaRejected, StaleQuotaDay
    }
    internal readonly struct QueueReservation
    {
        internal readonly BoundedLogQueue Owner;
        internal readonly long Generation;
        internal readonly ulong Lease;
        internal readonly int Slot;
        internal readonly QuotaDayTag? QuotaDayTag;
        internal QueueReservation(BoundedLogQueue owner, long generation, ulong lease, int slot, QuotaDayTag? quotaDayTag = null)
        {
            Owner = owner;
            Generation = generation;
            Lease = lease;
            Slot = slot;
            QuotaDayTag = quotaDayTag;
        }
    }
    internal readonly struct QueueSnapshot
    {
        private readonly int[] queuedByLevel;
        public int GetQueued(LogLevel level) => queuedByLevel[(int)level];
        public int NormalCount
        {
            get;
        }
        public int ErrorCount
        {
            get;
        }
        public int NormalBytes
        {
            get;
        }
        public int ErrorBytes
        {
            get;
        }
        public int ReservedCount
        {
            get;
        }
        public int QueuedCount
        {
            get;
        }
        public int PeakNormalCount
        {
            get;
        }
        public int PeakErrorCount
        {
            get;
        }
        public int PeakNormalBytes
        {
            get;
        }
        public int PeakErrorBytes
        {
            get;
        }
        public bool IsClosed
        {
            get;
        }
        internal QueueSnapshot(int normalCount, int errorCount, int normalBytes, int errorBytes, int reservedCount, int queuedCount,
            int peakNormalCount, int peakErrorCount, int peakNormalBytes, int peakErrorBytes, bool closed, int[] queuedByLevel)
        {
            this.queuedByLevel = queuedByLevel;
            NormalCount = normalCount;
            ErrorCount = errorCount;
            NormalBytes = normalBytes;
            ErrorBytes = errorBytes;
            ReservedCount = reservedCount;
            QueuedCount = queuedCount;
            PeakNormalCount = peakNormalCount;
            PeakErrorCount = peakErrorCount;
            PeakNormalBytes = peakNormalBytes;
            PeakErrorBytes = peakErrorBytes;
            IsClosed = closed;
        }
    }
    // 普通与错误分区各自限制数量和字节。生产者发布槽位；固定消费者移交后才归还槽位。
    internal sealed class BoundedLogQueue : ILogAdmissionController, IWorkerQueuedInput, IStartupAdmissionController
    {
        private readonly object consumerGate = new object();
        private readonly object gate = new object();
        private readonly long generation;
        private readonly int recordLimit, messageLimit, stackLimit, batchCount, batchBytes;
        private readonly Slot[] slots;
        private readonly Partition[] partitions;
        private int reservedCount, queuedCount, errorStreak;
        private readonly int[] queuedByLevel = new int[6];
        private ulong nextLease;
        private bool closed, exhausted;
        private sealed class QuotaSnapshot
        {
            internal readonly QuotaAdmissionGate Gate;
            internal readonly QuotaDayTag Tag;
            internal QuotaSnapshot(QuotaAdmissionGate gate, QuotaDayTag tag)
            {
                Gate = gate;
                Tag = tag;
            }
        }
        private QuotaSnapshot quota;
        private int pinned = -1;
        private LogContext lastNormalTransfer;
        private readonly long[] controlDrops = new long[6 * HealthCounters.ReasonCount];
        private bool startupEnabled;
        // null 表示首次恢复仍在进行。两个分区共用首个决议，后续恢复、跨日和清理均不能重新打开暂存。
        private QuotaSnapshot startupSettlement;
        private int startupQueued;
        private AdmissionReason startupNormalFailure, startupErrorFailure;
        internal void EnableQuotaGate(bool allowStartup = false)
        {
            lock (gate)
                if (quota == null)
                {
                    startupEnabled = allowStartup;
                    Volatile.Write(ref quota, new QuotaSnapshot(new QuotaAdmissionGate(0, QuotaAdmissionState.NotReady, AdmissionReason.NotReady, AdmissionReason.NotReady), default));
                }
        }
        public bool HasQueuedInput => Volatile.Read(ref queuedCount) != 0 || Volatile.Read(ref pinned) >= 0;
        internal bool IsDayTransition => Volatile.Read(ref quota)?.Gate.State == QuotaAdmissionState.DayTransition;
        internal AdmissionReason PrecheckAdmission(LogLevel level) => PrecheckAdmission(level, out _);
        internal AdmissionReason PrecheckAdmission(LogLevel level, out QuotaDayTag? tag, bool startupEligible = false)
        {
            QuotaSnapshot view = Volatile.Read(ref quota);
            if (startupEligible && startupEnabled && Volatile.Read(ref startupSettlement) == null)
            {
                tag = null;
                return AdmissionReason.Accepted;
            }
            tag = view?.Tag;
            return view == null ? AdmissionReason.Accepted : level >= LogLevel.Error ? view.Gate.ErrorDecision : view.Gate.NormalDecision;
        }
        public QuotaDayTag SetGate(QuotaAdmissionGate value)
            => SetStartupGate(value, AdmissionReason.StorageFault);

        public QuotaDayTag SetStartupGate(QuotaAdmissionGate value, AdmissionReason disposition)
        {
            bool settle = startupEnabled && disposition != AdmissionReason.Recovering && Volatile.Read(ref startupSettlement) == null;
            var current = Volatile.Read(ref quota);
            if (!settle && current != null && current.Gate.Equals(value))
                return current.Tag;
            lock (consumerGate)
            lock (gate)
            {
                if (!settle && quota != null && quota.Gate.Equals(value))
                    return quota.Tag;
                ulong previous = quota?.Tag.Generation ?? 0;
                if (previous == ulong.MaxValue)
                {
                    closed = exhausted = true;
                    return quota.Tag;
                }
                lastNormalTransfer = null;
                var tag = new QuotaDayTag(value.DayId, previous + 1);
                var update = new QuotaSnapshot(value, tag);
                if (settle && startupSettlement == null)
                {
                    startupNormalFailure = disposition == AdmissionReason.Accepted ? value.NormalDecision : disposition;
                    startupErrorFailure = disposition == AdmissionReason.Accepted ? value.ErrorDecision : disposition;
                    Volatile.Write(ref startupSettlement, update);
                }
                else if (startupSettlement != null)
                {
                    // 只冻结受影响的分区。普通额度耗尽不应淘汰已暂存的 Error；关闭过的配额绝不能在重开时复活。
                    bool changedDay = startupSettlement.Tag.DayId != value.DayId;
                    if (startupNormalFailure == AdmissionReason.Accepted)
                        startupNormalFailure = changedDay ? AdmissionReason.DayRolloverDropped : value.NormalDecision;
                    if (startupErrorFailure == AdmissionReason.Accepted)
                        startupErrorFailure = changedDay ? AdmissionReason.DayRolloverDropped : value.ErrorDecision;
                }
                Volatile.Write(ref quota, update);
                return tag;
            }
        }
        internal long GetControlDropped(LogLevel level, AdmissionReason reason) => Interlocked.Read(ref controlDrops[(int)level * HealthCounters.ReasonCount + (int)reason]);
        public QueueDayDropResult DropOneQueuedDay(long dayId, LogClassMask classes, AdmissionReason reason)
        {
            if ((uint)reason >= HealthCounters.ReasonCount || reason == AdmissionReason.Accepted)
                throw new ArgumentOutOfRangeException(nameof(reason));
            if (!TryEnterRemoval())
                return QueueDayDropResult.Contended;
            try
            {
                if (pinned != -1)
                    return QueueDayDropResult.LeaseBusy;
                for (int part = 0; part < 2; part++)
                {
                    if ((classes & (part == 0 ? LogClassMask.Normal : LogClassMask.ErrorFatal)) == 0)
                        continue;
                    int index = partitions[part].Head;
                    if (index == -1 || (slots[index].Record.QuotaDayTag ?? startupSettlement?.Tag)?.DayId != dayId)
                        continue;
                    ref Partition p = ref partitions[part];
                    ref Slot slot = ref slots[index];
                    p.Published.Pop();
                    queuedCount--;
                    queuedByLevel[(int)slot.Record.Level]--;
                    Interlocked.Increment(ref controlDrops[(int)slot.Record.Level * HealthCounters.ReasonCount + (int)reason]);
                    errorStreak = part == 1 ? Math.Min(ErrorWeight, errorStreak + 1) : 0;
                    ReleaseSlot(index);
                    return QueueDayDropResult.Dropped;
                }
                return QueueDayDropResult.NoMatchingHead;
            }
            finally
            {
                ExitRemoval();
            }
        }
        // 清理、快照和门禁变更先取消费锁再取发布锁；旧身份摘要仅尝试反向取锁，失败即追加队尾。
        private bool TryEnterRemoval()
        {
            if (!Monitor.TryEnter(consumerGate))
                return false;
            if (Monitor.TryEnter(gate))
                return true;
            Monitor.Exit(consumerGate);
            return false;
        }
        private void ExitRemoval()
        {
            Monitor.Exit(gate);
            Monitor.Exit(consumerGate);
        }
        private const int ErrorWeight = 3;
        private enum SlotState : byte
        {
            Free, Reserved, Queued
        }
        private struct Slot
        {
            public SlotState State;
            public int Next, Bytes, Partition;
            public ulong Lease;
            public LogRecord Record;
        }
        // 两端各自串行。先写入索引再发布写游标；读端归还读游标后槽位才可覆盖。
        private sealed class IndexRing
        {
            private readonly int[] indices;
            private int read, write;

            public IndexRing(int capacity)
            {
                indices = new int[checked(capacity + 1)];
            }

            public int Capacity => indices.Length - 1;
            public int Head
            {
                get
                {
                    int current = read;
                    return current == Volatile.Read(ref write) ? -1 : indices[current];
                }
            }

            public void Push(int index)
            {
                int current = write;
                int next = current + 1 == indices.Length ? 0 : current + 1;
                if (next == Volatile.Read(ref read))
                    throw new InvalidOperationException("Queue ring exceeded its slot capacity.");
                indices[current] = index;
                Volatile.Write(ref write, next);
            }

            // 以下两项只在同时持有两侧锁时调用。沿队首最多移动八个索引，
            // 利用该预留记录自有的空位，保持普通记录和跨身份摘要的既有顺序。
            public int Peek(int offset)
            {
                int count = write >= read ? write - read : indices.Length - read + write;
                return offset < count ? indices[(int)(((long)read + offset) % indices.Length)] : -1;
            }

            public void InsertBefore(int offset, int index)
            {
                int first = read == 0 ? indices.Length - 1 : read - 1;
                int dest = first;
                for (int i = 0; i < offset; i++)
                {
                    int src = dest + 1 == indices.Length ? 0 : dest + 1;
                    indices[dest] = indices[src];
                    dest = src;
                }
                indices[dest] = index;
                Volatile.Write(ref read, first);
            }

            public void Pop()
            {
                int current = read;
                Volatile.Write(ref read, current + 1 == indices.Length ? 0 : current + 1);
            }
        }

        private struct Partition
        {
            public readonly IndexRing Published, Returned;
            public int Head => Published.Head;
            public int Free, Count, Bytes, PeakCount, PeakBytes;
            public readonly int ByteCapacity;
            public Partition(int start, int count, int bytes)
            {
                Free = start;
                Published = new IndexRing(count);
                Returned = new IndexRing(count);
                Count = Bytes = PeakCount = PeakBytes = 0;
                ByteCapacity = bytes;
            }
        }
        public BoundedLogQueue(LoggingOptions options, long generation = 1, bool reserveIncident = true)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            if (generation < 1)
                throw new ArgumentOutOfRangeException(nameof(generation));
            LoggingOptions frozen = options.Freeze();
            this.generation = generation;
            recordLimit = frozen.EffectiveRecordByteLimit;
            messageLimit = frozen.MaxMessageChars;
            stackLimit = frozen.MaxStackChars;
            batchCount = frozen.BatchCount;
            batchBytes = frozen.BatchBytes;
            int normalSlots = frozen.NormalQueueCount - (reserveIncident && frozen.EffectiveIncidentInfoContext ? LoggingOptions.IncidentReservedSlots : 0);
            int normalBytes = frozen.NormalQueueBytes - (reserveIncident && frozen.EffectiveIncidentInfoContext ? LoggingOptions.IncidentReservedBytes : 0);
            slots = new Slot[normalSlots + frozen.ErrorQueueCount];
            partitions = new[] { new Partition(0, normalSlots, normalBytes), new Partition(normalSlots, frozen.ErrorQueueCount, frozen.ErrorQueueBytes) };
            for (int i = 0; i < slots.Length; i++)
            {
                int partition = i < normalSlots ? 0 : 1;
                int end = partition == 0 ? normalSlots : slots.Length;
                slots[i].Partition = partition;
                slots[i].Next = i + 1 < end ? i + 1 : -1;
            }
        }
        // 生产者调用快速失败：不自旋、不等待容量或 I/O。冻结的输入是受信任的内部估算值。
        public QueueDecision TryReserve(LogLevel level, int estimatedBytes, out QueueReservation reservation)
            => TryReserve(level, estimatedBytes, out reservation, out _);
        internal QueueDecision TryReserve(LogLevel level, int estimatedBytes, out QueueReservation reservation, out AdmissionReason quotaReason, bool terminalSummary = false, bool keepGate = false, bool startupEligible = false)
        {
            reservation = default;
            quotaReason = AdmissionReason.Accepted;
            if (level < LogLevel.Trace || level > LogLevel.Fatal || estimatedBytes < LoggingOptions.FixedRecordBytes)
                return QueueDecision.InvalidInput;
            if (estimatedBytes > recordLimit)
                return QueueDecision.RecordTooLarge;
            if (!Monitor.TryEnter(gate))
                return QueueDecision.ContentionRejected;
            try
            {
                if (closed && (!terminalSummary || exhausted))
                    return QueueDecision.Closed;
                AdmissionReason admission = PrecheckAdmission(level, out var reservedTag, startupEligible);
                quotaReason = admission;
                if (admission != AdmissionReason.Accepted)
                    return admission == AdmissionReason.NotReady ? QueueDecision.NotReady : QueueDecision.QuotaRejected;
                // 即使经历极长生命周期，也不能让租约编号回绕而产生 ABA 问题。
                if (nextLease == ulong.MaxValue)
                {
                    closed = exhausted = true;
                    return QueueDecision.Closed;
                }
                int partition = level >= LogLevel.Error ? 1 : 0;
                ref Partition p = ref partitions[partition];
                int index = p.Free >= 0 ? p.Free : p.Returned.Head;
                if (index < 0 || estimatedBytes > p.ByteCapacity - Volatile.Read(ref p.Bytes))
                    return QueueDecision.Full;
                ref Slot slot = ref slots[index];
                if (p.Free >= 0)
                    p.Free = slot.Next;
                else
                    p.Returned.Pop();
                slot.State = SlotState.Reserved;
                slot.Next = -1;
                slot.Bytes = estimatedBytes;
                slot.Lease = ++nextLease;
                int count = Interlocked.Increment(ref p.Count);
                int bytes = Interlocked.Add(ref p.Bytes, estimatedBytes);
                reservedCount++;
                p.PeakCount = Math.Max(p.PeakCount, count);
                p.PeakBytes = Math.Max(p.PeakBytes, bytes);
                reservation = new QueueReservation(this, generation, slot.Lease, index, reservedTag);
                return QueueDecision.Reserved;
            }
            finally
            {
                if (!keepGate || reservation.Owner == null) Monitor.Exit(gate);
            }
        }
        internal void EndPriorityPublication() => Monitor.Exit(gate);
        // 任何失败都保留预留。调用者必须在 finally 中取消，包括构造或提交失败时。
        // 提交成功即转移记录所有权；调用者不得修改保留的缓冲区（LogRecord 不可变）。
        public QueueDecision TryCommit(QueueReservation reservation, LogRecord record) => TryCommit(reservation, record, out _);
        internal QueueDecision TryCommit(QueueReservation reservation, LogRecord record, out AdmissionReason quotaReason, bool terminalSummary = false, int anchorSlot = -1, long anchorSequence = 0)
        {
            quotaReason = AdmissionReason.Accepted;
            if (record == null)
                return QueueDecision.InvalidInput;
            if (record.Message.Length > messageLimit || record.Stack.Length > stackLimit)
                return QueueDecision.RecordTooLarge;
            int actualBytes = RecordMemoryCost.Of(record);
            if (actualBytes > recordLimit)
                return QueueDecision.RecordTooLarge;
            if (!Monitor.TryEnter(gate))
                return QueueDecision.ContentionRejected;
            try
            {
                if (closed && (!terminalSummary || exhausted))
                    return QueueDecision.Closed;
                if (!IsLiveReservation(reservation))
                    return QueueDecision.InvalidReservation;
                // 预留后生产者会离开锁复制文本。回来时要同时核对队列当前配额、预留标签和记录标签，跨日或门状态变更都可能使提交失效。
                if (quota != null && !Nullable.Equals(record.QuotaDayTag, reservation.QuotaDayTag))
                    return QueueDecision.StaleQuotaDay;
                if (quota != null && !reservation.QuotaDayTag.HasValue && startupEnabled)
                    quotaReason = StartupRecordDecision(record.Level);
                else
                {
                    if (quota != null && !Nullable.Equals(reservation.QuotaDayTag, quota.Tag))
                        return QueueDecision.StaleQuotaDay;
                    quotaReason = PrecheckAdmission(record.Level);
                }
                if (quotaReason != AdmissionReason.Accepted)
                    return QueueDecision.QuotaRejected;
                ref Slot slot = ref slots[reservation.Slot];
                if ((record.Level >= LogLevel.Error ? 1 : 0) != slot.Partition || actualBytes > slot.Bytes)
                    return QueueDecision.InvalidInput;
                ref Partition p = ref partitions[slot.Partition];
                // 预留按上限收费，成功提交时改为记录实际估算值。差额立即归还分区，槽位仍保留到消费结束。
                Interlocked.Add(ref p.Bytes, actualBytes - slot.Bytes);
                slot.Bytes = actualBytes;
                slot.Record = record;
                if (quota != null && !record.QuotaDayTag.HasValue && startupEnabled)
                    Interlocked.Increment(ref startupQueued);
                slot.State = SlotState.Queued;
                // 只有已提交槽位进入 FIFO。更早的预留仍可继续复制，不阻塞当前队首。
                reservedCount--;
                Interlocked.Increment(ref queuedCount);
                Interlocked.Increment(ref queuedByLevel[(int)record.Level]);
                if (anchorSlot < 0 || !TryLinkRetiringTail(ref p, reservation.Slot, anchorSlot, anchorSequence))
                    p.Published.Push(reservation.Slot);
                return QueueDecision.Committed;
            }
            finally
            {
                Monitor.Exit(gate);
            }
        }
        // 固定 worker 提交的旧身份摘要可接在同一身份的队首段之后，原始记录相互之间仍按 FIFO。
        // 每次最多查看八个槽；已借队首、找不到身份边界或可能越过自身首条时，正常追加到队尾。
        private bool TryLinkRetiringTail(ref Partition partition, int index, int anchorSlot, long anchorSequence)
        {
            if (quota == null || slots[index].Partition != 0 || !Monitor.TryEnter(consumerGate))
                return false;
            try
            {
                if (pinned >= 0) return false;
                var ring = partition.Published;
                var context = slots[index].Record.Context;
                bool anchorQueued = slots[anchorSlot].State == SlotState.Queued && slots[anchorSlot].Record.Sequence == anchorSequence;
                int offset = 0;
                int next = ring.Peek(offset);
                while (offset < 8 && next >= 0 && ReferenceEquals(slots[next].Record.Context, context))
                {
                    if (next == anchorSlot) anchorQueued = false;
                    next = ring.Peek(++offset);
                }
                if (anchorQueued || next < 0 || slots[next].Record.Context.IdentityContextVersion <= context.IdentityContextVersion
                    || (offset == 0 && !ReferenceEquals(lastNormalTransfer, context)))
                    return false;
                ring.InsertBefore(offset, index);
                return true;
            }
            finally { Monitor.Exit(consumerGate); }
        }

        // 可靠回滚有意使用短时阻塞的元数据锁，并不保证绝对无等待。
        // 它不等待消费者、I/O 或用户工作；拒绝取消会永久泄漏预留容量。
        public bool CancelReservation(QueueReservation reservation)
        {
            lock (gate)
            {
                if (!IsLiveReservation(reservation))
                    return false;
                reservedCount--;
                ReleaseSlot(reservation.Slot);
                return true;
            }
        }
        private bool IsLiveReservation(QueueReservation reservation)
        {
            if (!ReferenceEquals(reservation.Owner, this) || reservation.Generation != generation ||
                reservation.Slot < 0 || reservation.Slot >= slots.Length || reservation.Lease == 0)
                return false;
            ref Slot slot = ref slots[reservation.Slot];
            return slot.State == SlotState.Reserved && slot.Lease == reservation.Lease;
        }
        // 单消费者 API：调用者持有空的预分配数组，并须先清理或完成上一批次。
        // 成功后立即归还队列容量；调用者或工作线程必须另行预算由此产生的处理中批次。
        // 仅返回 bool 的重载供轮询调用者使用。工作线程必须使用返回决策的重载：发生争用时，
        // 在有界延迟后重试，不要等待新的提交信号（唤醒信号可能已经被消耗）。
        public bool TryTakeBatch(LogRecord[] destination, out int count, out int bytes)
            => TryTakeBatch(destination, out count, out bytes, out _);
        public bool TryTakeBatch(LogRecord[] destination, out int count, out int bytes, out QueueTakeDecision decision)
        {
            count = bytes = 0;
            decision = QueueTakeDecision.InvalidInput;
            if (destination == null || destination.Length == 0)
                return false;
            if (!TryEnterRemoval())
            {
                decision = QueueTakeDecision.ContentionRejected;
                return false;
            }
            try
            {
                if (pinned != -1)
                {
                    decision = QueueTakeDecision.ContentionRejected;
                    return false;
                }
                int limit = Math.Min(batchCount, destination.Length);
                while (count < limit)
                {
                    int partition = partitions[1].Head != -1 && (partitions[0].Head == -1 || errorStreak < ErrorWeight) ? 1 : 0;
                    int index = partitions[partition].Head;
                    if (index == -1 || slots[index].Bytes > batchBytes - bytes)
                    {
                        partition = 1 - partition;
                        index = partitions[partition].Head;
                        if (index == -1 || slots[index].Bytes > batchBytes - bytes)
                            break;
                    }
                    ref Partition p = ref partitions[partition];
                    ref Slot slot = ref slots[index];
                    p.Published.Pop();
                    destination[count++] = slot.Record;
                    bytes += slot.Bytes;
                    queuedCount--;
                    queuedByLevel[(int)slot.Record.Level]--;
                    errorStreak = partition == 1 ? Math.Min(ErrorWeight, errorStreak + 1) : 0;
                    ReleaseSlot(index);
                }
                decision = count > 0 ? QueueTakeDecision.Taken : QueueTakeDecision.Empty;
                return count > 0;
            }
            finally
            {
                ExitRemoval();
            }
        }
        internal readonly struct HeadLease
        {
            internal readonly BoundedLogQueue Owner;
            internal readonly int Index;
            internal readonly ulong Lease;
            internal LogRecord Record
            {
                get;
            }
            internal readonly AdmissionReason StartupFailure;
            internal HeadLease(BoundedLogQueue owner, int index, ulong lease, LogRecord record, AdmissionReason startupFailure = AdmissionReason.Accepted)
            {
                Owner = owner;
                Index = index;
                Lease = lease;
                Record = record;
                StartupFailure = startupFailure;
            }
        }
        // 缓冲后端一次借用一个已提交队首。借用期间槽位仍占队列容量，直到 ResolvePin 确认移走或原位归还。
        internal bool TryPinHead(LogClassMask classes, out HeadLease lease, out QueueTakeDecision decision, bool holdNewNormal = false, bool resolveStartup = false)
        {
            lease = default;
            decision = QueueTakeDecision.Empty;
            if (!Monitor.TryEnter(consumerGate))
            {
                decision = QueueTakeDecision.ContentionRejected;
                return false;
            }
            try
            {
                if (pinned != -1)
                    return false;
                int error = partitions[1].Head;
                int normal = partitions[0].Head;
                if (normal >= 0 && holdNewNormal && lastNormalTransfer != null)
                {
                    ref Partition pending = ref partitions[0];
                    if (Volatile.Read(ref pending.Count) < pending.Published.Capacity
                        && pending.ByteCapacity - Volatile.Read(ref pending.Bytes) >= recordLimit
                        && slots[normal].Record.Context.IdentityContextVersion > lastNormalTransfer.IdentityContextVersion)
                        normal = -1;
                }
                AdmissionReason normalFailure = AdmissionReason.Accepted, errorFailure = AdmissionReason.Accepted;
                bool checkStartup = resolveStartup && Volatile.Read(ref startupQueued) != 0;
                if (checkStartup)
                {
                    CheckStartupHead(normal, LogClassMask.Normal, ref classes, out normalFailure);
                    CheckStartupHead(error, LogClassMask.ErrorFatal, ref classes, out errorFailure);
                }
                if ((classes & LogClassMask.Normal) == 0)
                    normal = -1;
                if ((classes & LogClassMask.ErrorFatal) == 0)
                    error = -1;
                int index = error >= 0 && (normal < 0 || errorStreak < ErrorWeight) ? error : normal;
                if (index < 0)
                    return false;
                // 借出的索引留在环首，生产者可以同时追加队尾；回滚无需重新插入记录。
                ref Slot slot = ref slots[index];
                AdmissionReason failure = index == error ? errorFailure : normalFailure;
                // 临时借用副本绑定首日，回滚仍保留原队首；下次借用必须重新检查清理和日切。
                var record = slot.Record;
                if (checkStartup && !record.QuotaDayTag.HasValue && failure == AdmissionReason.Accepted)
                    record = record.WithStartupDay(startupSettlement.Tag);
                pinned = index;
                lease = new HeadLease(this, index, slot.Lease, record, failure);
                decision = QueueTakeDecision.Taken;
                return true;
            }
            finally
            {
                Monitor.Exit(consumerGate);
            }
        }
        // 决议只绑定首次生效的配额代际。过期项目由 worker 按原始等级回收，无须扫描或复制整条队列。
        private AdmissionReason StartupRecordDecision(LogLevel level)
        {
            if (startupSettlement == null) return AdmissionReason.Accepted;
            return level >= LogLevel.Error ? startupErrorFailure : startupNormalFailure;
        }

        private void CheckStartupHead(int index, LogClassMask group, ref LogClassMask classes, out AdmissionReason failure)
        {
            failure = AdmissionReason.Accepted;
            if (index < 0 || slots[index].Record.QuotaDayTag.HasValue) return;
            if (startupSettlement == null)
                classes &= ~group;
            else
            {
                failure = StartupRecordDecision(slots[index].Record.Level);
                if (failure != AdmissionReason.Accepted)
                    classes |= group;
            }
        }
        internal bool ResolvePin(HeadLease lease, bool remove, bool transferred = false)
        {
            lock (consumerGate)
            {
                if (!ReferenceEquals(lease.Owner, this) || pinned < 0 || pinned != lease.Index || slots[pinned].Lease != lease.Lease)
                    return false;
                if (remove)
                {
                    ref Slot slot = ref slots[pinned];
                    ref Partition part = ref partitions[slot.Partition];
                    if (slot.Partition == 0 && transferred) lastNormalTransfer = slot.Record.Context;
                    part.Published.Pop();
                    Interlocked.Decrement(ref queuedCount);
                    Interlocked.Decrement(ref queuedByLevel[(int)slot.Record.Level]);
                    errorStreak = slot.Partition == 1 ? Math.Min(ErrorWeight, errorStreak + 1) : 0;
                    ResetSlot(ref part, ref slot);
                    part.Returned.Push(pinned);
                }
                // 回滚只解除借用；该记录始终位于分区头部。
                pinned = -1;
                return true;
            }
        }
        private void ReleaseSlot(int index)
        {
            ref Slot slot = ref slots[index];
            ref Partition p = ref partitions[slot.Partition];
            ResetSlot(ref p, ref slot);
            slot.Next = p.Free;
            p.Free = index;
        }
        private void ResetSlot(ref Partition part, ref Slot slot)
        {
            if (startupEnabled && slot.Record != null && !slot.Record.QuotaDayTag.HasValue)
                Interlocked.Decrement(ref startupQueued);
            Interlocked.Decrement(ref part.Count);
            Interlocked.Add(ref part.Bytes, -slot.Bytes);
            slot.Bytes = 0;
            slot.Record = null;
            slot.State = SlotState.Free;
        }
        // Close 与提交在同一元数据门锁下线性化，并保留未归还租约以便安全取消。
        // 已提交的记录仍可排空。T4 的发信号和等待操作必须放在该门锁之外。
        public void Close()
        {
            lock (gate)
                closed = true;
        }
        // 观察接口不是快速失败的生产者 API；应在延迟敏感回调之外使用。
        public QueueSnapshot Snapshot()
        {
            lock (consumerGate)
            lock (gate)
            {
                var levels = (int[])queuedByLevel.Clone();
                if (pinned >= 0)
                    levels[(int)slots[pinned].Record.Level]--;
                return new QueueSnapshot(partitions[0].Count, partitions[1].Count, partitions[0].Bytes, partitions[1].Bytes,
                    reservedCount, queuedCount - (pinned >= 0 ? 1 : 0), partitions[0].PeakCount, partitions[1].PeakCount, partitions[0].PeakBytes, partitions[1].PeakBytes, closed, levels);
            }
        }
    }
}
