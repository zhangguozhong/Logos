using System;
using System.IO;
using System.Security.Cryptography;

namespace Logos.Storage
{
    public sealed partial class JournalSession
    {
        /// <summary>借用的线格式数据在本次调用内完成认证并全部消费。不重试，也不保留调用者缓冲区。</summary>
        public JournalAppendResult TryAppend(
            byte[] wire,
            int offset,
            int length,
            long day,
            long writeGeneration
        )
        {
            JournalAppendResult result = TryAppendCore(wire, offset, length, day, writeGeneration);
            ObserveAppend();
            return result;
        }

        /// <summary>仅接受由暂存槽封块产生的一次性凭证；所有返回和异常路径都结束凭证。</summary>
        internal JournalAppendResult TryAppend(SealedBlockLease lease)
        {
            if (lease == null)
                return JournalAppendResult.InvalidBlock;
            try
            {
                CheckThread();
                if (!lease.TryConsume(out var bytes))
                    return JournalAppendResult.InvalidBlock;
                var result = TryAppendCore(bytes.Array, bytes.Offset, bytes.Count,
                    lease.Day, lease.WriteGeneration, lease);
                ObserveAppend();
                return result;
            }
            finally
            {
                lease.Invalidate();
            }
        }


        internal JournalAppendResult TryAppendMapped(SealedBlockLease sealedLease, ReadOnlySpan<byte> mappedWire)
        {
            if (sealedLease == null)
                return JournalAppendResult.InvalidBlock;
            CheckThread();
            try
            {
                bool valid = sealedLease.TryConsume(out var bytes) && mappedWire.Length == bytes.Count;
                if (!valid)
                    return JournalAppendResult.InvalidBlock;
                var result = TryAppendCore(bytes.Array, bytes.Offset, bytes.Count, sealedLease.Day,
                    sealedLease.WriteGeneration, sealedLease, mappedWire);
                ObserveAppend();
                return result;
            }
            finally { sealedLease.Invalidate(); }
        }

        // 恢复探针借用映射内的完整密文。调用者必须在本次调用内保持映射只读并持有目录所有权。
        internal JournalAppendResult TryAppendMapped(ReadOnlySpan<byte> wire, long day)
        {
            CheckThread();
            if (wire.Length < BlockCodec.MinPhysicalWireBlockBytes || wire.Length > scratch.Length)
                return JournalAppendResult.InvalidBlock;

            // 现有解码器接收数组。认证使用 Journal 自有工作区，正式追加直接读取映射。
            wire.CopyTo(scratch);
            try
            {
                var result = TryAppendCore(scratch, 0, wire.Length, day, WriteGeneration, mappedWire: wire);
                ObserveAppend();
                return result;
            }
            finally
            {
                Array.Clear(scratch, 0, wire.Length);
            }
        }

        private void ObserveAppend()
        {
            // 观察唯一变化的文件为 O(1)；元数据失败不能推翻数据提交事实。
            if (current != null && current.Exists && directoryQuota.IsReady)
            {
                try
                {
                    directoryQuota.ObserveDay(
                        current.DayId,
                        writer != null
                            ? writer.Length
                            : files.GetEntryInfo(DayPath(current.DayId)).Length,
                        current.NormalBytes,
                        current.ErrorBytes
                    );
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    directoryQuota.RestartScan();
                    if (Status == JournalStatus.Ready)
                    {
                        phase = 6;
                        Status = JournalStatus.Recovering;
                    }
                }
            }
        }

        private JournalAppendResult TryAppendCore(
            byte[] wire,
            int offset,
            int length,
            long day,
            long writeGeneration,
            SealedBlockLease lease = null,
            ReadOnlySpan<byte> mappedWire = default
        )
        {
            CheckThread();
            if (
                Status != JournalStatus.Ready
                || ControlKeyRotationStatus == ControlKeyRotationStatus.Pending
            )
                return JournalAppendResult.NotReady;
            if (IsCurrentClearTarget || current.Tombstone)
                return JournalAppendResult.QuotaClosed;
            if (day != current.DayId || writeGeneration != WriteGeneration)
                return JournalAppendResult.StaleDay;
            if (
                wire == null
                || offset < 0
                || length < BlockCodec.MinPhysicalWireBlockBytes
                || length > StorageRuntimeLimits.MaxWireBlockBytes
                || offset > wire.Length - length
            )
                return JournalAppendResult.InvalidBlock;
            byte[] authenticatedHash = null;
            try
            {
                VerifyOwner();
                bool error;
                if (lease != null)
                {
                    if (!lease.MatchesHeader(CurrentHeader))
                        return JournalAppendResult.StaleDay;
                    bool valid = lease.MatchesKey(dataKey)
                        && (mappedWire.IsEmpty
                            ? lease.TryAuthenticate(wire, offset, length, out authenticatedHash)
                            : lease.TryAuthenticateMapped(mappedWire, out authenticatedHash));
                    if (!valid)
                        return JournalAppendResult.InvalidBlock;
                    error = lease.IsErrorClass;
                }
                else
                {
                    try
                    {
                        var decoded = codec.Decode(CurrentHeader, dataKey, wire, offset, length);
                        error = decoded.IsErrorClass;
                        Array.Clear(decoded.Records, 0, decoded.Records.Length);
                        Wire.Clear(decoded.Epoch);
                    }
                    catch (StorageFormatException)
                    {
                        return JournalAppendResult.InvalidBlock;
                    }
                }

                bool needsHeader = !current.Exists;
                if (!quota.CanFit(error, length, needsHeader))
                    return JournalAppendResult.QuotaClosed;
                if (
                    !directoryQuota.CanAppend(
                        new JournalAdmission(
                            error,
                            length,
                            needsHeader,
                            EffectiveOptions,
                            day,
                            writeGeneration
                        )
                    )
                )
                    return JournalAppendResult.DirectoryLimit;
                if (
                    admission != null
                    && !admission.CanAppend(
                        new JournalAdmission(
                            error,
                            length,
                            needsHeader,
                            EffectiveOptions,
                            day,
                            writeGeneration
                        )
                    )
                )
                    return JournalAppendResult.DirectoryLimit;
                if (needsHeader)
                {
                    try
                    {
                        CreateHeader();
                    }
                    catch (Exception ex)
                        when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        DeferStateResync();
                        return JournalAppendResult.IoFailed;
                    }
                }

                if (writer == null)
                    writer = files.Open(DayPath(day), FileMode.Open, FileAccess.ReadWrite);
                if (writer.Length != current.CommittedEnd)
                {
                    DeferStateResync();
                    return JournalAppendResult.Uncertain;
                }

                var intent = MakeIntentCore(wire, offset, length, error, authenticatedHash, mappedWire);
                // 摘要所有权转交持久化意图，避免再扫描一次完整块。
                authenticatedHash = null;
                // 意图必须先写入日状态，再触碰目标数据区。若此时发布失败，恢复时仍有机会从幸存副本还原原先的提交下界。
                current.Intent = intent;
                current.Clean = false;
                try
                {
                    store.WriteDay(current, controlKey);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    DeferStateResync();
                    return JournalAppendResult.IoFailed;
                }

                bool certain;
                try
                {
                    VerifyOwner();
                    writer.Position = intent.Start;
                    if (mappedWire.IsEmpty)
                        writer.Write(wire, offset, length);
                    else
                        writer.Write(mappedWire);
                    writer.Flush();
                    certain = true;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    certain = VerifyUncertainWrite(intent);
                }

                if (!certain)
                {
                    DeferStateResync();
                    return JournalAppendResult.Uncertain;
                }

                current.CommittedEnd = intent.ExpectedEnd;
                quota.Charge(error, length, false);
                current.NormalBytes = quota.NormalBytes;
                current.ErrorBytes = quota.ErrorBytes;
                Buffer.BlockCopy(intent.Epoch, 0, current.Epoch, 0, 16);
                current.Intent = null;
                SaveSummary(current);
                // 完整线格式写入且普通 Flush 成功就是事实，即使此检查点仍滞后。
                try
                {
                    store.WriteDay(current, controlKey);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    DeferStateResync();
                }
                catch (JournalBlockedException ex)
                {
                    Block(ex.Reason);
                }

                return JournalAppendResult.Committed;
            }
            catch (JournalBlockedException ex)
            {
                Block(ex.Reason);
                return ex.Reason == JournalBlockReason.MetadataLimit
                    ? JournalAppendResult.MetadataLimit
                    : JournalAppendResult.IoFailed;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return JournalAppendResult.IoFailed;
            }
            finally
            {
                Wire.Clear(authenticatedHash);
            }
        }

        private void CreateHeader()
        {
            VerifyOwner();
            writer = files.Open(DayPath(current.DayId), FileMode.CreateNew, FileAccess.ReadWrite);
            byte[] bytes = CurrentHeader.CopyBytes();
            writer.Write(bytes, 0, bytes.Length);
            writer.Flush();
            // 有效完整头的 Flush 成功前，绝不发布此下界。
            current.Exists = true;
            current.CommittedEnd = 512;
            current.NormalBytes = 512;
            current.DataKeyId = CurrentHeader.KeyId;
            current.HeaderHash = (byte[])CurrentHeader.Hash.Clone();
            quota.Charge(false, 0, true);
            store.WriteDay(current, controlKey);
            SaveSummary(current);
        }

        private AppendIntent MakeIntent(byte[] wire, int offset, int length, bool error, byte[] authenticatedHash = null)
            => MakeIntentCore(wire, offset, length, error, authenticatedHash);

        private AppendIntent MakeIntentCore(byte[] wire, int offset, int length, bool error, byte[] authenticatedHash,
            ReadOnlySpan<byte> mappedWire = default)
        {
            byte[] hash = authenticatedHash;
            if (hash == null)
            {
                using (var sha = SHA256.Create())
                    hash = sha.ComputeHash(wire, offset, length);
            }
            return new AppendIntent
            {
                CleanupGeneration = current.CleanupGeneration,
                DayId = current.DayId,
                Start = current.CommittedEnd,
                PreviousCommittedEnd = current.CommittedEnd,
                ExpectedEnd = checked(current.CommittedEnd + length),
                IsErrorClass = error,
                Epoch = mappedWire.IsEmpty ? Wire.Slice(wire, offset + 44, 16) : mappedWire.Slice(44, 16).ToArray(),
                BlockSequence = mappedWire.IsEmpty ? Wire.U64(wire, offset + 60)
                    : System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(mappedWire.Slice(60)),
                WireHash = hash,
            };
        }

        // 写入异常后以意图记录的物理范围、摘要、epoch 和块序号逐项核对。读回一致且再次刷新成功才能确认这一次追加。
        private bool VerifyUncertainWrite(AppendIntent intent)
        {
            try
            {
                VerifyOwner();
                if (writer.Length != intent.ExpectedEnd)
                    return false;
                int length = checked((int)(intent.ExpectedEnd - intent.Start));
                writer.Position = intent.Start;
                JournalIo.ReadExactly(writer, scratch, 0, length);
                byte[] hash;
                using (var sha = SHA256.Create())
                    hash = sha.ComputeHash(scratch, 0, length);
                bool same = Wire.Equal(hash, intent.WireHash);
                Wire.Clear(hash);
                if (!same)
                    return false;
                var decoded = codec.Decode(CurrentHeader, dataKey, scratch, 0, length);
                same =
                    decoded.Sequence == intent.BlockSequence
                    && Wire.Equal(decoded.Epoch, intent.Epoch);
                Array.Clear(decoded.Records, 0, decoded.Records.Length);
                Wire.Clear(decoded.Epoch);
                if (!same)
                    return false;
                writer.Flush();
                return true;
            }
            catch (Exception ex)
                when (ex is IOException
                    || ex is UnauthorizedAccessException
                    || ex is StorageFormatException
                )
            {
                return false;
            }
            finally
            {
                Wire.Clear(scratch);
            }
        }

        private void DeferStateResync()
        {
            // 此处不执行恢复 I/O：设置错误不得重新分类已证实的数据提交。
            phase = 5;
            Status = JournalStatus.Recovering;
        }

        private void BeginStateResync()
        {
            transferClearIntentVerified = false;
            CloseAllCatalogs();
            writer?.Dispose();
            writer = null;
            dataKey?.Dispose();
            dataKey = null;
            var known = current;
            var authenticated = store.ReadDay((int)(global.HighestDay % 1024), out _);
            current = authenticated ?? EmptyDay(known.DayId, known.FileLimit, known.ErrorLimit);
            // 即使后续检查点失败，已确认提交仍构成下界。
            current.CommittedEnd = Math.Max(current.CommittedEnd, known.CommittedEnd);
            current.NormalBytes = Math.Max(current.NormalBytes, known.NormalBytes);
            current.ErrorBytes = Math.Max(current.ErrorBytes, known.ErrorBytes);
            if (known.Exists)
            {
                current.Exists = true;
                current.DataKeyId = known.DataKeyId;
                current.HeaderHash = (byte[])known.HeaderHash.Clone();
            }

            BeginRecovery(global.HighestDay, current);
            phase = 4;
        }
    }
}
