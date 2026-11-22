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
    // 固定元数据，数量和字节各有两个独立分区。持有门锁时不构造记录或文本，也不调用通知回调。
    internal sealed class BoundedLogQueue : ILogAdmissionController, IWorkerQueuedInput
    {
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
        internal void EnableQuotaGate()
        {
            lock (gate)
                if (quota == null)
                    Volatile.Write(ref quota, new QuotaSnapshot(new QuotaAdmissionGate(0, QuotaAdmissionState.NotReady, AdmissionReason.NotReady, AdmissionReason.NotReady), default));
        }
        public bool HasQueuedInput => Volatile.Read(ref queuedCount) != 0 || Volatile.Read(ref pinned) >= 0;
        internal bool IsDayTransition => Volatile.Read(ref quota)?.Gate.State == QuotaAdmissionState.DayTransition;
        internal AdmissionReason PrecheckAdmission(LogLevel level) => PrecheckAdmission(level, out _);
        internal AdmissionReason PrecheckAdmission(LogLevel level, out QuotaDayTag? tag)
        {
            QuotaSnapshot view = Volatile.Read(ref quota);
            tag = view?.Tag;
            return view == null ? AdmissionReason.Accepted : level >= LogLevel.Error ? view.Gate.ErrorDecision : view.Gate.NormalDecision;
        }
        public QuotaDayTag SetGate(QuotaAdmissionGate value)
        {
            lock (gate)
            {
                if (quota != null && quota.Gate.Equals(value))
                    return quota.Tag;
                ulong previous = quota?.Tag.Generation ?? 0;
                if (previous == ulong.MaxValue)
                {
                    closed = exhausted = true;
                    return quota.Tag;
                }
                lastNormalTransfer = null;
                var tag = new QuotaDayTag(value.DayId, previous + 1);
                Volatile.Write(ref quota, new QuotaSnapshot(value, tag));
                return tag;
            }
        }
        internal long GetControlDropped(LogLevel level, AdmissionReason reason) => Interlocked.Read(ref controlDrops[(int)level * HealthCounters.ReasonCount + (int)reason]);
        public QueueDayDropResult DropOneQueuedDay(long dayId, LogClassMask classes, AdmissionReason reason)
        {
            if ((uint)reason >= HealthCounters.ReasonCount || reason == AdmissionReason.Accepted)
                throw new ArgumentOutOfRangeException(nameof(reason));
            if (!Monitor.TryEnter(gate))
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
                    if (index == -1 || slots[index].Record.QuotaDayTag?.DayId != dayId)
                        continue;
                    ref Partition p = ref partitions[part];
                    ref Slot slot = ref slots[index];
                    p.Head = slot.Next;
                    if (p.Head == -1)
                        p.Tail = -1;
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
                Monitor.Exit(gate);
            }
        }
        private const int ErrorWeight = 3;
        private enum SlotState : byte
        {
            Free, Reserved, Queued, Pinned
        }
        private struct Slot
        {
            public SlotState State;
            public int Next, Bytes, Partition;
            public ulong Lease;
            public LogRecord Record;
        }
        private struct Partition
        {
            public int Free, Head, Tail, Count, Bytes, PeakCount, PeakBytes;
            public readonly int ByteCapacity;
            public Partition(int start, int bytes)
            {
                Free = start;
                Head = Tail = -1;
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
            partitions = new[] { new Partition(0, normalBytes), new Partition(normalSlots, frozen.ErrorQueueBytes) };
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
        internal QueueDecision TryReserve(LogLevel level, int estimatedBytes, out QueueReservation reservation, out AdmissionReason quotaReason, bool terminalSummary = false)
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
                AdmissionReason admission = PrecheckAdmission(level);
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
                if (p.Free == -1 || estimatedBytes > p.ByteCapacity - p.Bytes)
                    return QueueDecision.Full;
                int index = p.Free;
                ref Slot slot = ref slots[index];
                p.Free = slot.Next;
                slot.State = SlotState.Reserved;
                slot.Next = -1;
                slot.Bytes = estimatedBytes;
                slot.Lease = ++nextLease;
                p.Count++;
                p.Bytes += estimatedBytes;
                reservedCount++;
                p.PeakCount = Math.Max(p.PeakCount, p.Count);
                p.PeakBytes = Math.Max(p.PeakBytes, p.Bytes);
                reservation = new QueueReservation(this, generation, slot.Lease, index, quota?.Tag);
                return QueueDecision.Reserved;
            }
            finally
            {
                Monitor.Exit(gate);
            }
        }
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
                if (quota != null && (!reservation.QuotaDayTag.HasValue || !reservation.QuotaDayTag.Value.Equals(quota.Tag) || !record.QuotaDayTag.HasValue || !record.QuotaDayTag.Value.Equals(reservation.QuotaDayTag.Value)))
                    return QueueDecision.StaleQuotaDay;
                quotaReason = PrecheckAdmission(record.Level);
                if (quotaReason != AdmissionReason.Accepted)
                    return QueueDecision.QuotaRejected;
                ref Slot slot = ref slots[reservation.Slot];
                if ((record.Level >= LogLevel.Error ? 1 : 0) != slot.Partition || actualBytes > slot.Bytes)
                    return QueueDecision.InvalidInput;
                ref Partition p = ref partitions[slot.Partition];
                // 预留按上限收费，成功提交时改为记录实际估算值。差额立即归还分区，槽位仍保留到消费结束。
                p.Bytes -= slot.Bytes - actualBytes;
                slot.Bytes = actualBytes;
                slot.Record = record;
                slot.State = SlotState.Queued;
                // 只有已提交槽位进入 FIFO。更早的预留仍可继续复制，不阻塞当前队首。
                if (anchorSlot < 0 || !TryLinkRetiringTail(ref p, reservation.Slot, anchorSlot, anchorSequence))
                {
                    if (p.Tail == -1)
                        p.Head = reservation.Slot;
                    else
                        slots[p.Tail].Next = reservation.Slot;
                    p.Tail = reservation.Slot;
                }
                reservedCount--;
                queuedCount++;
                queuedByLevel[(int)record.Level]++;
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
            if (partition.Head == -1 || pinned >= 0 || quota == null || slots[index].Partition != 0)
                return false;
            var context = slots[index].Record.Context;
            int previous = -1, next = partition.Head;
            bool anchorQueued = slots[anchorSlot].State == SlotState.Queued && slots[anchorSlot].Record.Sequence == anchorSequence;
            for (int i = 0; i < 8 && next != -1 && ReferenceEquals(slots[next].Record.Context, context); i++)
            {
                if (next == anchorSlot) anchorQueued = false;
                previous = next;
                next = slots[next].Next;
            }
            if (anchorQueued || next == -1 || slots[next].Record.Context.IdentityContextVersion <= context.IdentityContextVersion
                || (previous == -1 && !ReferenceEquals(lastNormalTransfer, context)))
                return false;
            slots[index].Next = next;
            if (previous == -1) partition.Head = index;
            else slots[previous].Next = index;
            return true;
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
            if (!Monitor.TryEnter(gate))
            {
                decision = QueueTakeDecision.ContentionRejected;
                return false;
            }
            try
            {
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
                    p.Head = slot.Next;
                    if (p.Head == -1)
                        p.Tail = -1;
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
                Monitor.Exit(gate);
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
            internal HeadLease(BoundedLogQueue owner, int index, ulong lease, LogRecord record)
            {
                Owner = owner;
                Index = index;
                Lease = lease;
                Record = record;
            }
        }
        // 缓冲后端一次借用一个已提交队首。借用期间槽位仍占队列容量，直到 ResolvePin 确认移走或原位归还。
        internal bool TryPinHead(LogClassMask classes, out HeadLease lease, out QueueTakeDecision decision, bool holdNewNormal = false)
        {
            lease = default;
            decision = QueueTakeDecision.Empty;
            if (!Monitor.TryEnter(gate))
            {
                decision = QueueTakeDecision.ContentionRejected;
                return false;
            }
            try
            {
                if (pinned != -1)
                    return false;
                bool normal = (classes & LogClassMask.Normal) != 0 && partitions[0].Head != -1;
                // 仅暂缓跨登录版本的普通队首。保持原顺序，错误分区和同身份摘要仍可移交。
                if (normal && holdNewNormal && lastNormalTransfer != null)
                {
                    ref Partition pending = ref partitions[0];
                    if (pending.Free != -1 && pending.ByteCapacity - pending.Bytes >= recordLimit
                        && slots[pending.Head].Record.Context.IdentityContextVersion > lastNormalTransfer.IdentityContextVersion)
                        normal = false;
                }
                bool error = (classes & LogClassMask.ErrorFatal) != 0 && partitions[1].Head != -1;
                if (!normal && !error)
                    return false;
                int part = error && (!normal || errorStreak < ErrorWeight) ? 1 : 0;
                ref Partition p = ref partitions[part];
                int index = p.Head;
                ref Slot slot = ref slots[index];
                p.Head = slot.Next;
                if (p.Head == -1)
                    p.Tail = -1;
                slot.Next = -1;
                slot.State = SlotState.Pinned;
                pinned = index;
                queuedCount--;
                queuedByLevel[(int)slot.Record.Level]--;
                lease = new HeadLease(this, index, slot.Lease, slot.Record);
                decision = QueueTakeDecision.Taken;
                return true;
            }
            finally
            {
                Monitor.Exit(gate);
            }
        }
        internal bool ResolvePin(HeadLease lease, bool remove, bool transferred = false)
        {
            lock (gate)
            {
                if (!ReferenceEquals(lease.Owner, this) || pinned == -1 || pinned != lease.Index || slots[pinned].Lease != lease.Lease)
                    return false;
                int index = pinned;
                pinned = -1;
                ref Slot slot = ref slots[index];
                if (remove)
                {
                    if (slot.Partition == 0 && transferred) lastNormalTransfer = slot.Record.Context;
                    errorStreak = slot.Partition == 1 ? Math.Min(ErrorWeight, errorStreak + 1) : 0;
                    ReleaseSlot(index);
                }
                else
                {
                    ref Partition p = ref partitions[slot.Partition];
                    slot.State = SlotState.Queued;
                    slot.Next = p.Head;
                    p.Head = index;
                    if (p.Tail == -1)
                        p.Tail = index;
                    queuedCount++;
                    queuedByLevel[(int)slot.Record.Level]++;
                }
                return true;
            }
        }
        private void ReleaseSlot(int index)
        {
            ref Slot slot = ref slots[index];
            ref Partition p = ref partitions[slot.Partition];
            p.Count--;
            p.Bytes -= slot.Bytes;
            slot.Record = null;
            slot.State = SlotState.Free;
            slot.Bytes = 0;
            slot.Next = p.Free;
            p.Free = index;
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
            lock (gate)
            {
                return new QueueSnapshot(partitions[0].Count, partitions[1].Count, partitions[0].Bytes, partitions[1].Bytes,
                    reservedCount, queuedCount, partitions[0].PeakCount, partitions[1].PeakCount, partitions[0].PeakBytes, partitions[1].PeakBytes, closed, (int[])queuedByLevel.Clone());
            }
        }
    }
}
