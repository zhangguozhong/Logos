using System;
namespace Logos.Transport
{
    public enum TransferControlAdmission
    {
        Queued, Busy, WrongThread, StaleSession, Stopped, Clearing, Invalid, SequenceExhausted
    }
    /// <summary>仅表示工作线程最终准入。AcceptedInMemory 和 ReleaseSaveRequested 不是磁盘持久化证明。</summary>
    public enum TransferControlStatus
    {
        AcceptedInMemory, Existing, Busy, Conflict, Unauthorized, NotReady, Invalid, ReleaseSaveRequested, ReleaseRejected, StaleTaskHandle, StoppedBeforeExecution, ClearedBeforeExecution
    }
    public readonly struct TransferControlTicket
    {
        public long SessionGeneration
        {
            get;
        }
        public ulong Sequence
        {
            get;
        }
        internal int Slot
        {
            get;
        }
        internal TransferControlTicket(long generation, ulong sequence, int slot)
        {
            SessionGeneration = generation;
            Sequence = sequence;
            Slot = slot;
        }
    }
    public readonly struct TransferControlResult
    {
        public TransferControlTicket Ticket
        {
            get;
        }
        public TransferControlStatus Status
        {
            get;
        }
        internal TransferControlResult(TransferControlTicket ticket, TransferControlStatus status)
        {
            Ticket = ticket;
            Status = status;
        }
    }
    internal readonly struct TransferControlCommand
    {
        internal TransferControlTicket Ticket
        {
            get;
        }
        internal RetrievalRequest Request
        {
            get;
        }
        internal TransferTaskHandle Handle
        {
            get;
        }
        internal bool Release
        {
            get;
        }
        internal TransferControlCommand(TransferControlTicket ticket, RetrievalRequest request, TransferTaskHandle handle, bool release)
        {
            Ticket = ticket;
            Request = request;
            Handle = handle;
            Release = release;
        }
    }
    // 结果在被消费前保留槽位。持有此锁时不执行回调或 I/O。
    internal sealed class TransferControlMailbox
    {
        private struct Slot
        {
            internal byte State;
            internal ulong Sequence;
            internal RetrievalRequest Request;
            internal TransferTaskHandle Handle;
            internal bool Release;
            internal TransferControlStatus Result;
        }
        private readonly object gate = new object();
        private readonly Slot[] slots = new Slot[8];
        private readonly long generation;
        private ulong sequence;
        private bool stopped, clearing;
        internal TransferControlMailbox(long generation, ulong initialSequence = 0)
        {
            if (generation <= 0)
                throw new ArgumentOutOfRangeException(nameof(generation));
            this.generation = generation;
            sequence = initialSequence;
        }
        internal bool HasQueued
        {
            get
            {
                lock (gate)
                {
                    for (int i = 0; i < 8; i++)
                        if (slots[i].State == 1)
                            return true;
                    return false;
                }
            }
        }
        internal bool TryQueue(RetrievalRequest request, TransferTaskHandle handle, bool release, out TransferControlTicket ticket, out TransferControlAdmission admission)
        {
            ticket = default;
            lock (gate)
            {
                if (stopped)
                {
                    admission = TransferControlAdmission.Stopped;
                    return false;
                }
                if (clearing)
                {
                    admission = TransferControlAdmission.Clearing;
                    return false;
                }
                if (sequence == ulong.MaxValue)
                {
                    admission = TransferControlAdmission.SequenceExhausted;
                    return false;
                }
                for (int i = 0; i < 8; i++)
                    if (slots[i].State == 0)
                    {
                        ticket = new TransferControlTicket(generation, ++sequence, i);
                        slots[i] = new Slot { State = 1, Sequence = sequence, Request = request, Handle = handle, Release = release };
                        admission = TransferControlAdmission.Queued;
                        return true;
                    }
                admission = TransferControlAdmission.Busy;
                return false;
            }
        }
        internal bool TryBegin(out TransferControlCommand command)
        {
            lock (gate)
            {
                int oldest = -1;
                for (int i = 0; i < 8; i++)
                    if (slots[i].State == 1 && (oldest < 0 || slots[i].Sequence < slots[oldest].Sequence))
                        oldest = i;
                if (oldest < 0)
                {
                    command = default;
                    return false;
                }
                ref var row = ref slots[oldest];
                row.State = 2;
                command = new TransferControlCommand(new TransferControlTicket(generation, row.Sequence, oldest), row.Request, row.Handle, row.Release);
                row.Request = null;
                row.Handle = default;
                return true;
            }
        }
        internal void Complete(TransferControlTicket ticket, TransferControlStatus status)
        {
            lock (gate)
            {
                if (!Matches(ticket) || slots[ticket.Slot].State != 2)
                    return;
                slots[ticket.Slot].Result = status;
                slots[ticket.Slot].State = 3;
            }
        }
        // 调用方取走最终结果后才能复用槽位，排队和执行期间票据始终占用容量。
        internal bool TryTake(TransferControlTicket ticket, out TransferControlResult result)
        {
            lock (gate)
            {
                result = default;
                if (!Matches(ticket) || slots[ticket.Slot].State != 3)
                    return false;
                result = new TransferControlResult(ticket, slots[ticket.Slot].Result);
                slots[ticket.Slot] = default;
                return true;
            }
        }
        internal void CancelQueued(TransferControlStatus status, bool stop)
        {
            lock (gate)
            {
                stopped |= stop;
                clearing |= !stop;
                for (int i = 0; i < 8; i++)
                    if (slots[i].State == 1)
                    {
                        slots[i].Request = null;
                        slots[i].Handle = default;
                        slots[i].Result = status;
                        slots[i].State = 3;
                    }
            }
        }
        internal void ResumeAfterClear()
        {
            lock (gate)
                clearing = false;
        }
        private bool Matches(TransferControlTicket ticket) => ticket.SessionGeneration == generation && ticket.Sequence != 0 && ticket.Slot >= 0 && ticket.Slot < 8 && slots[ticket.Slot].Sequence == ticket.Sequence;
    }
}
