using System;
using System.IO;
namespace Logos.Storage
{
    public sealed partial class JournalSession
    {
        // 仅作观察：不得授权过期、发布候选日期或绕过旧日期排空屏障。
        internal bool DirectoryInventoryInProgress => Status == JournalStatus.Ready && directoryQuota.InventoryInProgress;
        internal void StepDirectoryInventory(JournalStepContext context)
        {
            CheckThread();
            if (!DirectoryInventoryInProgress || context.ShouldYield)
                return;
            VerifyOwner();
            directoryQuota.Step(context);
        }
        internal bool CanAllocateMappedBuffer(int bytes)
        {
            CheckThread();
            VerifyOwner();
            return Status == JournalStatus.Ready
                && directoryQuota.CanAllocateMappedBuffer(bytes, EffectiveOptions);
        }

        internal void RefreshMappedBufferUsage()
        {
            CheckThread();
            VerifyOwner();
            directoryQuota.RestartScan();
        }

        internal string MappedControlKeyId => controlKey?.KeyId;

        internal long NextMappedAppendOffset => current.Exists ? current.CommittedEnd : StorageRuntimeLimits.FileHeaderBytes;

        internal MappedSealedBuffer OpenMappedSealedBuffer()
        {
            CheckThread();
            VerifyOwner();
            string path = Path.Combine(directory, MappedSealedBuffer.FileName);
            bool exists = files.Exists(path);
            if (exists && !files.GetEntryInfo(path).IsRegularFile)
                throw new IOException("Invalid sealed map path.");
            if (!exists && !CanAllocateMappedBuffer(MappedSealedBuffer.FileBytes))
                return null;
            try { return new MappedSealedBuffer(path, !exists, controlKey); }
            finally
            {
                if (!exists)
                    RefreshMappedBufferUsage();
            }
        }

        internal JournalAppendResult RecoverMappedAppend(ReadOnlySpan<byte> wire, long previousOffset,
            MappedSealedBuffer mapped, bool error, WriteRateLimiter rate, long now)
        {
            CheckThread();
            VerifyOwner();
            if (Status != JournalStatus.Ready || IsCurrentClearTarget || current.Tombstone)
                return JournalAppendResult.NotReady;
            long position = NextMappedAppendOffset;
            if (previousOffset >= 0 && previousOffset < position)
            {
                if (previousOffset < StorageRuntimeLimits.FileHeaderBytes || previousOffset > position - wire.Length)
                    return JournalAppendResult.InvalidBlock;
                try
                {
                    using (var reader = files.Open(DayPath(current.DayId), FileMode.Open, FileAccess.Read))
                    {
                        reader.Position = previousOffset;
                        JournalIo.ReadExactly(reader, scratch, 0, wire.Length);
                        return wire.SequenceEqual(scratch.AsSpan(0, wire.Length))
                            ? JournalAppendResult.Committed : JournalAppendResult.InvalidBlock;
                    }
                }
                catch (IOException) { return JournalAppendResult.IoFailed; }
                catch (UnauthorizedAccessException) { return JournalAppendResult.IoFailed; }
                finally { Array.Clear(scratch, 0, wire.Length); }
            }
            if (previousOffset > position)
                return JournalAppendResult.InvalidBlock;
            var preflight = PreflightOwnedAppend(error, wire.Length, current.DayId, WriteGeneration);
            if (preflight != JournalAppendResult.Committed)
                return preflight;
            if (!rate.TryAcquire(error, wire.Length, now, out _))
                return JournalAppendResult.NotReady;
            if (previousOffset < 0)
                mapped.BindAppend(error, position);
            return TryAppendMapped(wire, current.DayId);
        }

        // 仅作内部容量预检。不会写入或认证任意输入。
        // 公共 TryAppend 仍须认证完整线格式，并验证每条解码记录。
        internal JournalAppendResult PreflightOwnedAppend(bool errorClass, int length, long day, long generation)
        {
            CheckThread();
            if (Status != JournalStatus.Ready || quota == null)
                return JournalAppendResult.NotReady;
            if (day != current.DayId || generation != WriteGeneration)
                return JournalAppendResult.StaleDay;
            if (IsCurrentClearTarget || current.Tombstone || !quota.CanFit(errorClass, length, !current.Exists))
                return JournalAppendResult.QuotaClosed;
            if (directoryQuota.MetadataBytes > StorageOptions.MetadataBudgetBytes)
                return JournalAppendResult.MetadataLimit;
            var request = new JournalAdmission(errorClass, length, !current.Exists, EffectiveOptions, day, generation);
            if (!directoryQuota.CanAppend(request) || (admission != null && !admission.CanAppend(request)))
                return JournalAppendResult.DirectoryLimit;
            return JournalAppendResult.Committed;
        }
        // 纯只读派生门槛：恢复后的认证总用量同样参与，不创建可被重启重置的日额度。
        internal bool CanAppendDiagnostic(bool errorClass, int wireBytes, long day, long generation)
        {
            CheckThread();
            if (wireBytes <= 0 || Status != JournalStatus.Ready || quota == null || current == null
                || day != current.DayId || generation != WriteGeneration || IsCurrentClearTarget || current.Tombstone)
                return false;
            if (!errorClass)
                return true;
            // floor(limit / 10) 等价于 limit - ceil(90% * limit)，且乘法不溢出。
            long headroom = EffectiveOptions.ErrorFatalReservedBytesPerDay / 10;
            return quota.ErrorBytes <= headroom && wireBytes <= headroom - quota.ErrorBytes;
        }
        private void OpenStateStore(System.IO.FileMode mode, bool create)
        {
            if (openingStateStream != null)
                throw new System.IO.IOException("Startup state handle still belongs to the session.");
            // 即使构造失败且其自身关闭也可能失败，仍保留所有权。
            openingStateStream = files.Open(StatePath, mode, System.IO.FileAccess.ReadWrite);
            store = new JournalStateStore(openingStateStream, keys, VerifyOwner, create);
            openingStateStream = null;
        }
        private void RetryStartup()
        {
            if (openingStateStream != null)
            {
                openingStateStream.Dispose();
                if (!JournalIo.IsClosed(openingStateStream))
                    throw new System.IO.IOException("Startup state handle remains open.");
                openingStateStream = null;
                return;
            }
            if (entries != null)
            {
                entries.Dispose();
                entries = null;
                return;
            }
            if (store != null)
            {
                // 实际关闭前失败会保留此引用和目录所有者。
                store.Dispose();
                if (!store.ResourcesClosed)
                    throw new System.IO.IOException("Startup state handle remains open.");
                store = null;
                return;
            }
            phase = 0;
            BlockReason = JournalBlockReason.None;
        }
        internal void RequestCapacityProbe()
        {
            CheckThread();
            if (Status != JournalStatus.Ready)
                return;
            directoryQuota.RestartScan();
            phase = 6;
            Status = JournalStatus.Recovering;
        }
        public int ForceSyncCalls
        {
            get;
            private set;
        }
        public int ForceSyncSucceeded
        {
            get;
            private set;
        }
        public int ForceSyncUnsupported
        {
            get;
            private set;
        }
        // 停止流程按单元推进文件同步，并分别记录平台成功或不支持的结果，以便健康快照反映实际持久化能力。
        internal bool ForceSyncUnit(ref int phase, JournalStepContext context)
        {
            CheckThread();
            if (context.ShouldYield)
                return false;
            while (phase < 2)
            {
                var stream = phase++ == 0 ? writer : store?.OwnedStream;
                if (stream == null)
                    continue;
                if ((files is StorageIoMeasurements measured && !measured.SupportsDurability) || !(files is IJournalDurabilityFileSystem capability))
                {
                    ForceSyncUnsupported++;
                    return phase == 2;
                }
                ForceSyncCalls++;
                try
                {
                    var result = capability.ForceSync(stream);
                    if (result == JournalForceSyncResult.Synchronized)
                        ForceSyncSucceeded++;
                    else if (result == JournalForceSyncResult.Unsupported)
                        ForceSyncUnsupported++;
                }
                catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException || ex is NotSupportedException)
                {
                }
                return phase == 2;
            }
            return true;
        }
    }
}
