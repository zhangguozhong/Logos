using System;
using System.IO;
using System.Security.Cryptography;

namespace Logos.Storage
{
    public sealed partial class StorageBatchProcessor
    {
        private const long MappedReplayTicks = TimeSpan.TicksPerHour;
        private const long MappedRetryTicks = 100 * TimeSpan.TicksPerMillisecond;
        private const int MappedMaxRecoveryRetries = 3;
        private readonly bool mappedRequested;
        private MappedSealedBuffer mappedBuffer;
        private string mappedControlKeyId;
        private bool mappedInitialized, mappedNormal, mappedError;
        private int mappedRecoverySlot, mappedRecoveryRetries;
        private bool mappedRecoverErrorFirst;
        private long mappedNextRecovery;
        private long mappedBlocksPublished, mappedBytesPublished, mappedBlocksRecovered, mappedBlocksRejected;

        // 恢复和初始化均在目录所有者的 worker 上执行；每个 tick 最多处理一个槽。
        private bool StepMappedStartup(WorkerControlContext context)
        {
            if (!mappedRequested)
                return false;
            if (mappedBuffer != null && mappedControlKeyId != journal.MappedControlKeyId)
            {
                StopMapping();
                mappedInitialized = false;
                mappedRecoverySlot = 0;
            }
            if (mappedInitialized && mappedRecoverySlot == 2)
                return false;
            if (journal.DirectoryInventoryInProgress)
            {
                journal.StepDirectoryInventory(JournalStepContext.FromWorker(context));
                return true;
            }
            try
            {
                if (!mappedInitialized)
                {
                    mappedInitialized = true;
                    mappedBuffer = journal.OpenMappedSealedBuffer();
                    if (mappedBuffer == null)
                        mappedRecoverySlot = 2;
                    else
                    {
                        mappedControlKeyId = journal.MappedControlKeyId;
                        mappedRecoverErrorFirst = mappedBuffer.RecoverErrorFirst();
                    }
                    return true;
                }

                if (context.NowTicks < mappedNextRecovery && context.Phase != WorkerControlPhase.Stopping)
                    return true;
                bool errorClass = mappedRecoverySlot == 0 ? mappedRecoverErrorFirst : !mappedRecoverErrorFirst;
                var result = JournalAppendResult.NotReady;
                bool trusted = CanUseMappedTime();
                var state = trusted
                    ? mappedBuffer.Read(errorClass, journal.CurrentHeader, journal.CleanupGeneration, observation.UtcNow.Ticks,
                        (wire, offset) => journal.RecoverMappedAppend(wire, offset, mappedBuffer, errorClass, rate, context.NowTicks),
                        out result)
                    : MappedSealedBuffer.State.Stale;

                if (state == MappedSealedBuffer.State.Ready &&
                    (result == JournalAppendResult.IoFailed || result == JournalAppendResult.DirectoryLimit ||
                     result == JournalAppendResult.Uncertain || result == JournalAppendResult.NotReady) &&
                    context.Phase != WorkerControlPhase.Stopping && mappedRecoveryRetries < MappedMaxRecoveryRetries)
                {
                    mappedRecoveryRetries++;
                    mappedNextRecovery = Add(context.NowTicks, MappedRetryTicks);
                    return true;
                }

                if (state != MappedSealedBuffer.State.Empty && mappedBuffer.HasSlot(errorClass))
                {
                    bool committed = result == JournalAppendResult.Committed;
                    mappedBuffer.Retire(errorClass, durable: !committed);
                    if (committed) mappedBlocksRecovered++;
                    else mappedBlocksRejected++;
                }
                mappedRecoverySlot++;
                mappedRecoveryRetries = 0;
                mappedNextRecovery = 0;
                return true;
            }
            catch (Exception ex) when (IsMappedFailure(ex))
            {
                StopMapping();
                return true;
            }
        }

        private bool CanUseMappedTime() => journal.CurrentHeader != null
            && journal.TimeDecision.IsConfirmed && !journal.TimeDecision.RequiresConfirmation
            && observation.UtcNow.Ticks / TimeSpan.TicksPerDay == journal.CurrentHeader.DayId;

        private void PublishMapped(BoundedBlockStaging staging, bool isError)
        {
            if (mappedBuffer == null || !CanUseMappedTime()) return;
            long utc = observation.UtcNow.Ticks;
            try
            {
                mappedBuffer.Publish(isError, staging.SealedBytes, journal.CurrentHeader,
                    journal.CleanupGeneration, utc, Add(utc, MappedReplayTicks), staging.SealedLease.SealedDigest);
                mappedBlocksPublished++;
                mappedBytesPublished += staging.SealedLength;
                if (isError) mappedError = true;
                else mappedNormal = true;
            }
            catch (Exception ex) when (IsMappedFailure(ex))
            {
                // 不能确认候选槽的状态时，停止使用映射。已绑定的槽仅可按日文件偏移核对或重放。
                StopMapping();
            }
        }

        private JournalAppendResult AppendMappedOrMemory(BoundedBlockStaging staging, bool isError)
        {
            if (mappedBuffer == null || !(isError ? mappedError : mappedNormal))
                return journal.TryAppend(staging.SealedLease);
            try
            {
                if (CanUseMappedTime())
                    return mappedBuffer.AppendLive(isError, journal, staging.SealedLease);
                if (!RetireMapped(staging, false))
                    return JournalAppendResult.Uncertain;
                return journal.TryAppend(staging.SealedLease);
            }
            catch (Exception ex) when (IsMappedFailure(ex))
            {
                StopMapping();
                return JournalAppendResult.Uncertain;
            }
        }

        private bool RetireMapped(BoundedBlockStaging staging, bool committed, bool uncertain = false)
        {
            bool isError = ReferenceEquals(staging, error);
            if (mappedBuffer == null || !(isError ? mappedError : mappedNormal))
                return true;
            if (uncertain)
            {
                // 保留已绑定偏移的密文，交给重启后的日文件恢复重新判定。
                StopMapping();
                return true;
            }
            try
            {
                mappedBuffer.Retire(isError, durable: !committed);
                if (isError) mappedError = false;
                else mappedNormal = false;
                return true;
            }
            catch (Exception ex) when (IsMappedFailure(ex))
            {
                // 未确认退休时，不把候选报告成确定丢弃，也不重新追加同一记忆块。
                StopMapping();
                return committed;
            }
        }

        private void StopMapping()
        {
            var buffer = mappedBuffer;
            mappedBuffer = null;
            mappedControlKeyId = null;
            mappedInitialized = true;
            mappedRecoverySlot = 2;
            mappedRecoveryRetries = 0;
            mappedNextRecovery = 0;
            mappedNormal = mappedError = false;
            buffer?.Dispose();
        }

        private static bool IsMappedFailure(Exception ex) => ex is IOException
            || ex is UnauthorizedAccessException || ex is NotSupportedException || ex is CryptographicException
            || ex is InvalidDataException || ex is DllNotFoundException || ex is EntryPointNotFoundException || ex is TypeLoadException;
    }
}
