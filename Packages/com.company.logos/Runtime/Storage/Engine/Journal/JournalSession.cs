using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Threading;

namespace Logos.Storage
{
    /// <summary>工作线程独占的手动引擎。构造为纯操作；Step 是唯一启动和恢复驱动。</summary>
    public sealed partial class JournalSession : IDisposable
    {
        private readonly FrozenStorageOptions configured;
        private readonly string directory;
        private readonly IStorageKeyProvider keys;
        private readonly IJournalDirectoryAdmission admission;
        private readonly DirectoryQuota directoryQuota;
        private readonly IJournalFileSystem files;
        private readonly IDirectoryLeaseProvider leases;
        private readonly RecordCodec records;
        private readonly BlockCodec codec;
        private readonly byte[] scratch = new byte[StorageRuntimeLimits.MaxWireBlockBytes];
        private readonly JournalDaySummary[] summaries = new JournalDaySummary[1024];
        private IDirectoryLease lease;
        private DirectoryLeaseStatus lastLeaseFailure;
        private JournalStateStore store;
        private JournalGlobalState global;
        private JournalDayState current;
        private KeyMaterial controlKey;
        private KeyMaterial dataKey;
        private Stream writer;
        private Stream openingStateStream;
        private IEnumerator<string> entries;
        private JournalRecovery recovery;
        private JournalDayState recoveringDay;
        private bool recoveringDayRepair;
        private readonly ReadLeaseRegistry readers = new ReadLeaseRegistry();
        private readonly CommittedBlockCatalog[] catalogs = new CommittedBlockCatalog[4];
        private DailyQuotaState quota;
        private int slot;
        private int phase;
        private int ownerThread;
        private bool disposalStarted;
        private long requestedDay;
        public JournalStatus Status
        {
            get;
            private set;
        }
        public JournalBlockReason BlockReason
        {
            get;
            private set;
        }
        public VerifiedFileHeader CurrentHeader
        {
            get;
            private set;
        }
        public FrozenStorageOptions EffectiveOptions
        {
            get;
            private set;
        }
        public long WriteGeneration
        {
            get;
            private set;
        }
        = 1;
        public long CleanupGeneration => global?.CleanupGeneration ?? 0;
        public long CommittedEnd => current?.CommittedEnd ?? 0;
        public long NormalBytes => quota?.NormalBytes ?? 0;
        public long ErrorBytes => quota?.ErrorBytes ?? 0;
        public bool NormalClosed => quota?.NormalClosed ?? false;
        public bool ErrorClosed => quota?.ErrorClosed ?? false;
        public DirectoryUsage DirectoryUsage => new DirectoryUsage(directoryQuota);
        public long RecoveryBlocksAuthenticated
        {
            get;
            private set;
        }

        /// <summary>保守数组存储：每份含四个标记和五个 long 的摘要按 64 字节计，包括对齐填充。</summary>
        public int SummaryBufferPayloadBytes => summaries.Length * 64;

        /// <summary>共享 magic/domain 字节数组和脱敏器引用数组存储的保守预算。</summary>
        public int SharedBufferPayloadUpperBoundBytes => 512;

        /// <summary>固定自有数组存储上界，包括引用槽位。不含 CLR 对象头或原生分配。</summary>
        public int FixedBufferPayloadBytes =>
            scratch.Length
            + SummaryBufferPayloadBytes
            + catalogs.Length * IntPtr.Size
            + JournalStateStore.FixedBufferCapacityBytes
            + codec.WorkspaceBytes
            + records.WorkspaceBytes
            + directoryQuota.FixedBufferPayloadBytes
            + readers.FixedBufferPayloadBytes
            + DropStatistics.FixedBufferPayloadBytes
            + SharedBufferPayloadUpperBoundBytes;

        /// <summary>八个日期模型、四个全局模型、六个头、
        /// 十六个密钥及材料摘要、恢复 epoch、头读回和认证暂存的保守有界字节数组载荷。别名分别计入。</summary>
        public int MaximumModelBufferPayloadBytes =>
            8 * (64 + 48) + 4 * 48 + 6 * (512 + 32 + 16) + 16 * 96 + 16 + 512 + 1024;

        public int MaximumBoundedControlBufferPayloadBytes =>
            Clear.MaximumBufferPayloadBytes + MaximumModelBufferPayloadBytes;

        /// <summary>固定与有界控制缓冲区载荷的兼容名称，不代表托管或原生堆总量。
        /// 不含对象头、字符串、解码记录、流、provider、加密内部结构和调用者缓冲区。</summary>
        public int FixedWorkspaceBytes =>
            FixedBufferPayloadBytes + MaximumBoundedControlBufferPayloadBytes;

        public JournalSession(
            FrozenStorageOptions options,
            string directory,
            IStorageKeyProvider keys,
            IJournalFileSystem fileSystem = null,
            IDirectoryLeaseProvider leases = null
        )
            : this(options, directory, keys, null, fileSystem, leases)
        {
        }

        public JournalSession(
            FrozenStorageOptions options,
            string directory,
            IStorageKeyProvider keys,
            IJournalDirectoryAdmission admission,
            IJournalFileSystem fileSystem = null,
            IDirectoryLeaseProvider leases = null
        )
        {
            configured = options ?? throw new ArgumentNullException(nameof(options));
            this.directory = directory ?? throw new ArgumentNullException(nameof(directory));
            this.keys = keys ?? throw new ArgumentNullException(nameof(keys));
            this.admission = admission;
            files = fileSystem ?? new SystemJournalFileSystem();
            directoryQuota = new DirectoryQuota(files, directory);
            this.leases = leases ?? new DirectoryLeaseProvider();
            records = new RecordCodec(new LoggingOptions());
            codec = new BlockCodec(records);
            Clear = new ClearCoordinator(this);
            DropStatistics = new DropStatisticsCheckpoint(CheckThread);
        }

        public static string FileName(long day) =>
            new DateTime(checked(day * TimeSpan.TicksPerDay), DateTimeKind.Utc).ToString(
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture
            ) + ".logos";

        private string DayPath(long day) => Path.Combine(lease.CanonicalPath, FileName(day));

        private string StatePath => Path.Combine(lease.CanonicalPath, ".journal-state");

        /// <summary>显式受信任的底层 API。调用者负责确认此日期。</summary>
        public void Step(long confirmedEffectiveDay, JournalStepContext context) => StepCore(confirmedEffectiveDay, context);

        private void StepCore(long confirmedEffectiveDay, JournalStepContext context)
        {
            CheckThread();
            if (Status == JournalStatus.Disposed || disposalStarted)
                throw new ObjectDisposedException(nameof(JournalSession));
            if (
                context.ShouldYield
                || Status == JournalStatus.Blocked
                || Status == JournalStatus.DirectoryBusy
            )
                return;
            if (
                confirmedEffectiveDay < 0
                || confirmedEffectiveDay > DateTime.MaxValue.Ticks / TimeSpan.TicksPerDay
            )
                throw new ArgumentOutOfRangeException(nameof(confirmedEffectiveDay));
            try
            {
                if (Status == JournalStatus.New)
                {
                    var result = leases.TryAcquire(directory);
                    if (result.Status != DirectoryLeaseStatus.Acquired)
                    {
                        lastLeaseFailure = result.Status;
                        Status =
                            result.Status == DirectoryLeaseStatus.DirectoryBusy
                                ? JournalStatus.DirectoryBusy
                                : JournalStatus.Blocked;
                        BlockReason = JournalBlockReason.LeaseUnavailable;
                        return;
                    }

                    lease = result.Lease;
                    VerifyOwner();
                    requestedDay = confirmedEffectiveDay;
                    Status = JournalStatus.Recovering;
                    return;
                }

                VerifyOwner();
                if (!safeTimeStep)
                    ConfirmExplicitDay(confirmedEffectiveDay);
                if (Clear.IsActive)
                {
                    StepClear(context);
                    return;
                }

                if (ControlKeyRotationStatus == ControlKeyRotationStatus.Pending)
                {
                    StepControlKeyRotation(context);
                    return;
                }

                readers.RequestExpired(System.Diagnostics.Stopwatch.GetTimestamp());
                readers.CloseCancelled(context);
                if (Status == JournalStatus.Ready)
                {
                    if (confirmedEffectiveDay < global.HighestDay)
                        throw new JournalBlockedException(JournalBlockReason.TimeUnconfirmed);
                    if (confirmedEffectiveDay > global.HighestDay)
                        PublishNewDay(confirmedEffectiveDay);
                    else
                    {
                        if (timeAuthorityDirty)
                        {
                            store.WriteGlobalBoth(global, controlKey);
                            timeAuthorityDirty = false;
                            return;
                        }
                        // 当前观察不确定时，已持久化的最高日期不授权新的过期处理。
                        // 先前已认证的删除意图仍可完成其有界恢复。
                        if (
                            !safeTimeStep
                            || (global.TimeConfirmed && CanAuthorizeNewTimeState)
                            || (expiringRow != null && expiringRow.DeletionPhase != 0)
                        )
                            StepMaintenance(confirmedEffectiveDay, context);
                        StepStatistics(context);
                    }

                    return;
                }

                if (recovery != null)
                {
                    RecoverOneBlock();
                    return;
                }

                if (phase == 0)
                {
                    OpenState();
                    return;
                }

                if (phase == 1)
                {
                    EstablishVirgin();
                    return;
                }

                if (phase == 2)
                {
                    ReadOneDaySlot();
                    return;
                }

                if (phase == 3)
                {
                    ReadDirectoryPage();
                    return;
                }

                if (phase == 4)
                {
                    PrepareCurrent();
                    return;
                }

                if (phase == 7)
                {
                    if (expiringRow != null)
                        StepMaintenance(requestedDay, context);
                    else
                        PublishNewDay(requestedDay);
                    return;
                }

                if (phase == 9)
                {
                    StepMaintenance(requestedDay, context);
                    if (expiringRow == null)
                        phase = 2;
                    return;
                }

                if (phase == 10)
                {
                    RetryStartup();
                    return;
                }

                if (phase == 8)
                {
                    RetryRecovery();
                    return;
                }

                if (phase == 6)
                {
                    directoryQuota.Step(context);
                    if (directoryQuota.IsReady)
                        phase = 4;
                    return;
                }

                if (phase == 5)
                {
                    BeginStateResync();
                    return;
                }
            }
            catch (JournalBlockedException ex)
            {
                Block(ex.Reason);
            }
            catch (StorageFormatException)
            {
                Block(JournalBlockReason.HeaderInvalid);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Block(JournalBlockReason.IoFailed);
            }
        }

        private void OpenState()
        {
            VerifyOwner();
            if (!files.Exists(StatePath))
            {
                entries = files.EnumerateEntries(lease.CanonicalPath).GetEnumerator();
                phase = 1;
                return;
            }

            OpenStateStore(FileMode.Open, false);
            global = store.ReadGlobal();
            if (safeTimeStep)
                ApplyObservedTime();
            else
                ConfirmExplicitDay(requestedDay);
            if (requestedDay < global.HighestDay)
                throw new JournalBlockedException(JournalBlockReason.TimeUnconfirmed);
            controlKey = JournalIo.Acquire(keys, global.ControlKeyId);
            DropStatistics.Load(store, keys);
            RestoreClearIntent();
            phase = 2;
        }

        // 缺少状态文件时还要枚举目录。除所有者锁以外只要存在其他条目，就不能按空目录创建新的配额和目录身份。
        private void EstablishVirgin()
        {
            for (int i = 0; i < 8; i++)
            {
                if (!entries.MoveNext())
                {
                    entries.Dispose();
                    entries = null;
                    controlKey = JournalIo.Acquire(keys, null);
                    if (safeTimeStep)
                        EstablishVirginTime();
                    global = new JournalGlobalState
                    {
                        HighestDay = requestedDay,
                        TimeConfirmed = !safeTimeStep || dayClock.Decision.IsConfirmed,
                        FileLimit = configured.MaxDailyFileBytes,
                        ErrorLimit = configured.ErrorFatalReservedBytesPerDay,
                        PolicyVersion = 1,
                        ControlKeyId = controlKey.KeyId,
                    };
                    using (var random = RandomNumberGenerator.Create())
                        random.GetBytes(global.DirectoryId);
                    OpenStateStore(FileMode.CreateNew, true);
                    current = EmptyDay(requestedDay, global.FileLimit, global.ErrorLimit);
                    store.WriteDay(current, controlKey, true);
                    store.WriteGlobalBoth(global, controlKey);
                    SaveSummary(current);
                    phase = 6;
                    return;
                }

                if (Path.GetFileName(entries.Current) != ".owner.lock")
                    throw new JournalBlockedException(JournalBlockReason.GlobalStateInvalid);
            }
        }

        private void ReadOneDaySlot()
        {
            if (slot == 1024)
            {
                entries = files.EnumerateEntries(lease.CanonicalPath).GetEnumerator();
                phase = 3;
                return;
            }

            int index = slot++;
            var row = store.ReadDayForRecovery(
                index,
                global,
                requestedDay,
                configured.RetentionDays,
                out var replaced,
                out bool preparedSlot
            );
            bool repair = store.LastDayRequiresRepair;
            if (row == null)
                return;
            if (row.DayId > global.HighestDay)
            {
                // 只有已确认且经过认证的空准备状态，才可完成中断的槽位复用。
                if (
                    !preparedSlot
                    || row.DayId > requestedDay
                    || files.Exists(DayPath(row.DayId))
                    || (replaced != null && files.Exists(DayPath(replaced.DayId)))
                )
                    throw new JournalBlockedException(JournalBlockReason.StateInvalid);
                if (!CanAuthorizeNewTimeState)
                {
                    slot--; // 当前样本允许发布后，再处理此已认证准备状态。
                    return;
                }
                store.WriteDay(row, controlKey, true);
                global.HighestDay = row.DayId;
                global.FileLimit = row.FileLimit;
                global.ErrorLimit = row.ErrorLimit;
                store.WriteGlobalBoth(global, controlKey);
                repair = false;
            }

            if (
                row.DayId % 1024 != index
                || row.DayId > global.HighestDay
                || row.CleanupGeneration > global.CleanupGeneration
                || !Wire.Equal(row.DirectoryId, global.DirectoryId)
            )
                throw new JournalBlockedException(JournalBlockReason.StateInvalid);
            if (row.Tombstone)
            {
                SaveSummary(row);
                if (row.DeletionPhase != 2)
                {
                    if (row.DeletionKind != 1 || row.ExpiryConfirmedDay > requestedDay)
                        throw new JournalBlockedException(JournalBlockReason.StateInvalid);
                    expiringRow = row;
                    maintenanceConfirmedDay = row.ExpiryConfirmedDay;
                    expiryPhase = 0;
                    // 先完成持久过期处理，再考虑缺失数据检查或槽位复用。
                    phase = 9;
                    return;
                }

                if (repair || !store.VerifyDeletionBoth(row, 2))
                {
                    store.WriteDay(row, controlKey, true);
                    if (!store.VerifyDeletionBoth(row, 2))
                        throw new IOException("Completed tombstone repair verification failed.");
                }
                if (row.DayId == global.HighestDay)
                    current = row;
                return;
            }

            if (files.Exists(DayPath(row.DayId)))
            {
                recoveringDayRepair = repair;
                BeginRecovery(row.DayId, row);
                return;
            }

            if (row.Exists)
                throw new JournalBlockedException(JournalBlockReason.DataMissing);
            if (row.CommittedEnd != 0)
                throw new JournalBlockedException(JournalBlockReason.KnownCommitMissing);
            if (repair)
                store.WriteDay(row, controlKey, true);
            SaveSummary(row);
            if (row.DayId == global.HighestDay)
                current = row;
        }

        private void ReadDirectoryPage()
        {
            for (int i = 0; i < 8; i++)
            {
                if (!entries.MoveNext())
                {
                    entries.Dispose();
                    entries = null;
                    if (current == null)
                        throw new JournalBlockedException(JournalBlockReason.StateInvalid);
                    phase = 6;
                    return;
                }

                string name = Path.GetFileName(entries.Current);
                if (name == ".owner.lock" || name == ".journal-state")
                    continue;
                if (
                    !DateTime.TryParseExact(
                        name,
                        "yyyy-MM-dd'.logos'",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out var date
                    )
                )
                    continue;
                long day = date.Ticks / TimeSpan.TicksPerDay;
                if (day > global.HighestDay)
                    throw new JournalBlockedException(JournalBlockReason.StateInvalid);
                var summary = summaries[day % 1024];
                if (summary.Valid)
                {
                    if (summary.DayId != day)
                        throw new JournalBlockedException(JournalBlockReason.MetadataLimit);
                    continue;
                }

                BeginRecovery(day, null);
                return;
            }
        }

        // 恢复器从文件头开始重新认证块。会话在扫描完成前维持恢复状态，防止旧检查点被新的追加覆盖。
        private void BeginRecovery(long day, JournalDayState row)
        {
            VerifyOwner();
            Stream stream = null;
            KeyMaterial key = null;
            try
            {
                stream = files.Open(DayPath(day), FileMode.Open, FileAccess.ReadWrite);
                if (stream.Length < 512)
                    throw new JournalBlockedException(JournalBlockReason.ShortHeader);
                var headerBytes = new byte[512];
                JournalIo.ReadExactly(stream, headerBytes, 0, 512);
                key = JournalIo.Acquire(keys, JournalIo.ReadKeyId(headerBytes, 64, 65));
                var header = FileHeaderCodec.Verify(headerBytes, key);
                if (
                    header.DayId != day
                    || header.PolicyVersion != 1
                    || !Wire.Equal(header.DirectoryId, global.DirectoryId)
                )
                    throw new JournalBlockedException(JournalBlockReason.PolicyMismatch);
                if (
                    row != null
                    && (
                        row.FileLimit != header.MaxDailyFileBytes
                        || row.ErrorLimit != header.ErrorFatalReservedBytesPerDay
                        || row.PolicyVersion != header.PolicyVersion
                        || (
                            row.Exists
                            && (
                                row.DataKeyId != header.KeyId
                                || !Wire.Equal(row.HeaderHash, header.Hash)
                            )
                        )
                    )
                )
                    throw new JournalBlockedException(JournalBlockReason.PolicyMismatch);
                if (
                    day == global.HighestDay
                    && (
                        header.MaxDailyFileBytes != global.FileLimit
                        || header.ErrorFatalReservedBytesPerDay != global.ErrorLimit
                        || header.PolicyVersion != global.PolicyVersion
                    )
                )
                    throw new JournalBlockedException(JournalBlockReason.PolicyMismatch);
                recoveringDay =
                    row
                    ?? EmptyDay(
                        day,
                        header.MaxDailyFileBytes,
                        header.ErrorFatalReservedBytesPerDay
                    );
                recovery = new JournalRecovery(
                    stream,
                    key,
                    header,
                    codec,
                    scratch,
                    row,
                    VerifyOwner
                );
                stream = null;
                key = null;
            }
            finally
            {
                try
                {
                    stream?.Dispose();
                }
                finally
                {
                    key?.Dispose();
                }
            }
        }

        private void RecoverOneBlock()
        {
            recovery.Step();
            if (recovery.AuthenticatedBlockLastStep)
                RecoveryBlocksAuthenticated++;
            if (!recovery.Complete)
                return;
            var row = recoveringDay;
            row.Exists = true;
            row.CommittedEnd = recovery.ConfirmedEnd;
            row.NormalBytes = recovery.NormalBytes;
            row.ErrorBytes = recovery.ErrorBytes;
            row.DataKeyId = recovery.Header.KeyId;
            row.HeaderHash = (byte[])recovery.Header.Hash.Clone();
            row.Intent = null;
            row.Epoch = (byte[])recovery.LastEpoch.Clone();
            row.Clean = false;
            if (row.DayId == global.HighestDay)
            {
                current = row;
                CurrentHeader = recovery.Header;
            }

            store.WriteDay(
                row,
                controlKey,
                row.Sequence == 0 || row.SchemaVersion == 1 || recoveringDayRepair
            );
            SaveSummary(row);
            recovery.Dispose();
            recovery = null;
            recoveringDay = null;
            recoveringDayRepair = false;
        }

        private void PrepareCurrent()
        {
            dataKey?.Dispose();
            dataKey =
                current.Tombstone ? null
                : current.Exists ? JournalIo.Acquire(keys, current.DataKeyId)
                : JournalIo.Acquire(keys, null);
            if (!current.Exists && !current.Tombstone)
                CurrentHeader = FileHeaderCodec.Create(
                    current.DayId,
                    current.FileLimit,
                    current.ErrorLimit,
                    current.PolicyVersion,
                    global.DirectoryId,
                    dataKey
                );
            if (store.RequiresGlobalRepair || timeAuthorityDirty)
            {
                store.WriteGlobalBoth(global, controlKey);
                timeAuthorityDirty = false;
            }
            EffectiveOptions = DeriveEffectiveOptions();
            if (quota == null)
                quota = new DailyQuotaState(
                    current.FileLimit,
                    current.ErrorLimit,
                    current.NormalBytes,
                    current.ErrorBytes
                );
            else
                quota.SetRecoveredUsage(current.NormalBytes, current.ErrorBytes);
            if (current.Tombstone)
                quota.CloseBoth();
            Status = JournalStatus.Ready;
            if (requestedDay > global.HighestDay)
                PublishNewDay(requestedDay);
        }

        private JournalDayState EmptyDay(long day, long f, long e) =>
            new JournalDayState
            {
                DayId = day,
                FileLimit = f,
                ErrorLimit = e,
                PolicyVersion = 1,
                CleanupGeneration = global.CleanupGeneration,
                ControlKeyId = controlKey.KeyId,
                DirectoryId = (byte[])global.DirectoryId.Clone(),
            };

        private void PublishNewDay(long day)
        {
            if (!CanAuthorizeNewTimeState)
                return;
            // 槽位复用的过期授权之前，就先关闭准入和实际旧数据流。
            Status = JournalStatus.Recovering;
            CloseAllCatalogs();
            writer?.Dispose();
            writer = null;
            dataKey?.Dispose();
            dataKey = null;
            var row = store.ReadDay((int)(day % 1024), out bool evidence);
            if (row != null || evidence)
            {
                if (
                    row == null
                    || !RetentionPolicy.IsExpired(row.DayId, day, configured.RetentionDays)
                )
                    throw new JournalBlockedException(JournalBlockReason.MetadataLimit);
                if (!row.Tombstone || row.DeletionPhase != 2 || row.DeletionKind != 1)
                {
                    BeginExpiry(row, day);
                    requestedDay = day;
                    phase = 7;
                    Status = JournalStatus.Recovering;
                    return;
                }

                if (files.Exists(DayPath(row.DayId)))
                    throw new JournalBlockedException(JournalBlockReason.StateInvalid);
            }

            var next = EmptyDay(
                day,
                configured.MaxDailyFileBytes,
                configured.ErrorFatalReservedBytesPerDay
            );
            if (row != null)
                next.Sequence = row.Sequence;
            // 先写好新日的两份空行，再发布全局最高日期。中间退出会留下准备行，重启可检查它是否有权替换旧槽。
            store.WriteDay(next, controlKey, true);
            global.HighestDay = day;
            global.TimeConfirmed = true;
            global.FileLimit = next.FileLimit;
            global.ErrorLimit = next.ErrorLimit;
            store.WriteGlobalBoth(global, controlKey);
            current = next;
            quota = null;
            CurrentHeader = null;
            WriteGeneration = checked(WriteGeneration + 1);
            requestedDay = day;
            SaveSummary(next);
            phase = 4;
            Status = JournalStatus.Recovering;
        }

        private FrozenStorageOptions DeriveEffectiveOptions()
        {
            // 当日文件上限与错误保留额来自已认证的全局状态；目录大小与保留天数采用本次配置，因此目录准入可立即收紧。
            long budget = configured.MaxDirectoryBytes - StorageOptions.MetadataBudgetBytes;
            long error = (long)
                BigInteger.Min(
                    (BigInteger)global.ErrorLimit * configured.RetentionDays,
                    (BigInteger)budget * global.ErrorLimit / global.FileLimit
                );
            return new FrozenStorageOptions(
                global.FileLimit,
                global.ErrorLimit,
                configured.RetentionDays,
                configured.MaxDirectoryBytes,
                error,
                budget - error,
                (BigInteger)configured.MaxDirectoryBytes
                    < (BigInteger)global.FileLimit * configured.RetentionDays
                        + StorageOptions.MetadataBudgetBytes
            );
        }

        private void SaveSummary(JournalDayState row)
        {
            directoryQuota.ClassifyDay(
                row.DayId,
                row.Tombstone ? 0 : row.NormalBytes,
                row.Tombstone ? 0 : row.ErrorBytes
            );
            summaries[row.DayId % 1024] = new JournalDaySummary
            {
                Valid = true,
                Tombstone = row.Tombstone,
                DeletionKind = row.DeletionKind,
                DeletionPhase = row.DeletionPhase,
                DayId = row.DayId,
                ConfirmedEnd = row.CommittedEnd,
                NormalBytes = row.NormalBytes,
                ErrorBytes = row.ErrorBytes,
                CleanupGeneration = row.CleanupGeneration,
            };
        }

        private void CheckThread()
        {
            int thread = Thread.CurrentThread.ManagedThreadId;
            if (ownerThread == 0)
                ownerThread = thread;
            else if (ownerThread != thread)
                throw new InvalidOperationException("Journal methods belong to one owner worker.");
        }

        private void VerifyOwner()
        {
            if (lease == null || !lease.ValidateIdentity())
                throw new JournalBlockedException(JournalBlockReason.LeaseIdentityChanged);
        }

        private void Block(JournalBlockReason reason)
        {
            transferClearIntentVerified = false;
            BlockReason = reason;
            Status = JournalStatus.Blocked;
        }

        public void Dispose()
        {
            if (Status == JournalStatus.Disposed)
                return;
            if (ownerThread != 0)
                CheckThread();
            transferClearIntentVerified = false;
            disposalStarted = true;
            Exception failure = null;
            foreach (var cursor in catalogs)
                DisposeOne(cursor, ref failure);
            DisposeOne(Clear.UnknownEntries, ref failure);
            DisposeOne(recovery, ref failure);
            if (recovery?.ResourcesClosed ?? true)
                recovery = null;
            DisposeOne(directoryQuota, ref failure);
            DisposeOne(entries, ref failure);
            entries = null;
            DisposeOne(writer, ref failure);
            if (JournalIo.IsClosed(writer))
                writer = null;
            DisposeOne(openingStateStream, ref failure);
            if (JournalIo.IsClosed(openingStateStream))
                openingStateStream = null;
            DisposeOne(store, ref failure);
            if (store?.ResourcesClosed ?? true)
                store = null;
            DisposeOne(dataKey, ref failure);
            dataKey = null;
            DisposeOne(rotationKey, ref failure);
            rotationKey = null;
            DisposeOne(controlKey, ref failure);
            controlKey = null;
            DisposeOne(codec, ref failure);
            DisposeOne(records, ref failure);
            Wire.Clear(scratch);
            DropStatistics.ClearBuffers();
            // 关闭在句柄实际关闭前失败时保留所有权。后续所有者 Dispose 会重试。
            if (readers.ActiveCount != 0 || recovery != null || writer != null || store != null || openingStateStream != null)
            {
                Status = JournalStatus.Blocked;
                BlockReason = JournalBlockReason.IoFailed;
                throw failure ?? new IOException("Owned resources remain open.");
            }

            // 所有自有流、密钥材料和编解码器都尝试清理后，才释放所有权。
            DisposeOne(lease, ref failure);
            lease = null;
            Status = JournalStatus.Disposed;
            if (failure != null)
                throw failure;
        }

        private static void DisposeOne(IDisposable resource, ref Exception firstFailure)
        {
            try
            {
                resource?.Dispose();
            }
            catch (Exception ex)
            {
                if (firstFailure == null)
                    firstFailure = ex;
            }
        }
    }
}
