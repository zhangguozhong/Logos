using System;
using System.Security.Cryptography;
using System.Text;
using Logos.Storage;
namespace Logos.Transport
{
    /// <summary>版本 1/2 的认证上传容器。仅目录导出器可编码。</summary>
    public static class UploadEnvelope
    {
        public const int PrefixBytes = 1024, MaximumBodyBytes = 67424;
        private static readonly byte[] DomainV1 = Encoding.ASCII.GetBytes("Logos.Transport/Upload/v1\0");
        private static readonly byte[] DomainV2 = Encoding.ASCII.GetBytes("Logos.Transport/Upload/v2\0");
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("LOGOSX01");
        internal static ExportBatch Seal(RetrievalRequest request, long revision, long generation, CommittedBlockCatalog source, DecodedBlock block, long blockOffset, int before, int next, VerifiedFileHeader outputHeader, KeyMaterial key, BlockCodec codec, BlockMetadata metadata, byte[] plain, int plainLength, BatchCoverageManifest coverage)
        {
            byte[] body = new byte[MaximumBodyBytes];
            int wireLength;
            if (!codec.TrySeal(outputHeader, key, metadata, plain, 0, plainLength, body, 1536, 65856, out wireLength, out var failure))
                throw new StorageFormatException();
            int total = 1536 + wireLength + 32;
            Buffer.BlockCopy(Magic, 0, body, 0, 8);
            Wire.Put16(body, 8, 2);
            Wire.Put16(body, 10, PrefixBytes);
            Wire.Put32(body, 12, (uint)total);
            Wire.Put32(body, 16, (uint)(512 + wireLength));
            Wire.Put16(body, 20, (ushort)metadata.RecordCount);
            Wire.Put16(body, 22, (ushort)(metadata.IsErrorClass ? 1 : 0));
            Wire.Put64(body, 24, generation);
            Wire.Put64(body, 32, revision);
            Wire.Put64(body, 40, block.DayId);
            Wire.Put64(body, 48, blockOffset);
            Wire.Put16(body, 56, (ushort)before);
            Wire.Put16(body, 58, (ushort)next);
            Wire.PutU64(body, 64, block.Sequence);
            Buffer.BlockCopy(block.Epoch, 0, body, 72, 16);
            Wire.Put64(body, 88, source.ConfirmedEnd);
            PutId(body, 96, request.TaskId);
            PutId(body, 225, request.DestinationId);
            PutId(body, 354, request.InstallationId);
            Buffer.BlockCopy(source.Header.Hash, 0, body, 512, 32);
            coverage.Write(body, BatchCoverageManifest.Offset);
            Buffer.BlockCopy(outputHeader.CopyBytes(), 0, body, 1024, 512);
            byte[] mac = key.CopyAuthenticationKey();
            try
            {
                Wire.MacInto(mac, body, total - 32, new ArraySegment<byte>(DomainV2), new ArraySegment<byte>(body, 0, total - 32));
            }
            finally
            {
                Wire.Clear(mac);
            }
            var bookmark = new ExportBookmark(block.DayId, source.ConfirmedEnd, source.Header.Hash, blockOffset, next, generation);
            return new ExportBatch(new UploadRequest(request.TaskId, request.DestinationId, request.InstallationId, revision, generation, new UploadBody(body, total, true)), bookmark, metadata.RecordCount, new ExportBookmark(block.DayId, source.ConfirmedEnd, source.Header.Hash, blockOffset, before, generation), coverage);
        }
        private static void PutId(byte[] body, int offset, string id)
        {
            body[offset] = (byte)id.Length;
            Encoding.ASCII.GetBytes(id, 0, id.Length, body, offset + 1);
        }
        private static string ReadId(byte[] body, int offset)
        {
            int length = body[offset];
            if (length < 1 || length > 128)
                throw new StorageFormatException();
            for (int i = offset + 1 + length; i < offset + 129; i++)
                if (body[i] != 0)
                    throw new StorageFormatException();
            string value = Encoding.ASCII.GetString(body, offset + 1, length);
            for (int i = offset + 1; i < offset + 1 + length; i++)
                if (body[i] < 32 || body[i] > 126)
                    throw new StorageFormatException();
            try
            {
                return TransferValidation.Id(value, "metadata");
            }
            catch (ArgumentException)
            {
                throw new StorageFormatException();
            }
        }
        private static void Zero(byte[] body, int offset, int count)
        {
            for (int i = offset; i < offset + count; i++)
                if (body[i] != 0)
                    throw new StorageFormatException();
        }
        /// <summary>接收器辅助接口：先认证外层元数据再解码内部内容，随后将每条记录与请求的授权交集核对。</summary>
        public static VerifiedUploadEnvelope Decode(UploadBody immutableBody, IStorageKeyProvider keys, RetrievalRequest request, TransferAccessController access, long revision, long generation, DateTime utc)
        {
            if (immutableBody == null || keys == null || request == null || access == null)
                throw new ArgumentNullException();
            if (immutableBody.Length < 1888 || immutableBody.Length > MaximumBodyBytes)
                throw new StorageFormatException();
            byte[] body = immutableBody.CopyBytes();
            try
            {
                // 外层 MAC 前只解释有界版本选择和密钥定位信息；两者均不提供授权事实。
                int version = Wire.U16(body, 8);
                if (version != 1 && version != 2)
                    throw new StorageFormatException();
                string keyId = JournalIo.ReadKeyId(body, 1024 + 64, 1024 + 65);
                using (var key = JournalIo.Acquire(keys, keyId))
                {
                    byte[] mac = key.CopyAuthenticationKey();
                    try
                    {
                        var tag = Wire.Mac(mac, version == 1 ? DomainV1 : DomainV2, Wire.Slice(body, 0, body.Length - 32));
                        if (!Wire.Equal(tag, Wire.Slice(body, body.Length - 32, 32)))
                            throw new StorageAuthenticationException();
                    }
                    finally
                    {
                        Wire.Clear(mac);
                    }
                    // 旧格式没有绑定查询范围的摘要，因而无法证明这批数据是针对当前诊断用户筛选的。
                    if (version == 1 && request.Scope.DiagnosticUserId != null)
                        throw new UnauthorizedAccessException("UID retrieval requires an authenticated scope digest.");
                    if (!Wire.Equal(Wire.Slice(body, 0, 8), Magic) || Wire.U16(body, 10) != 1024 || Wire.U32(body, 12) != body.Length || Wire.U32(body, 16) != body.Length - 1056 || Wire.U16(body, 20) < 1 || Wire.U16(body, 20) > 256 || Wire.U16(body, 22) > 1 || Wire.I64(body, 24) != generation || Wire.I64(body, 32) != revision || revision < 0 || generation < 0)
                        throw new StorageFormatException();
                    Zero(body, 60, 4);
                    Zero(body, 483, 29);
                    if (version == 1)
                        Zero(body, 544, 480);
                    else
                        Zero(body, 800, 224);
                    long day = Wire.I64(body, 40), offset = Wire.I64(body, 48), end = Wire.I64(body, 88);
                    int before = Wire.U16(body, 56), next = Wire.U16(body, 58);
                    if (day < request.FirstQuotaDay || day >= request.FirstQuotaDay + request.QuotaDays || offset < 512 || end <= offset || before > 255 || next <= before || next > 256 || Wire.U16(body, 20) > next - before || ReadId(body, 96) != request.TaskId || ReadId(body, 225) != request.DestinationId || ReadId(body, 354) != request.InstallationId)
                        throw new StorageFormatException();
                    var h = FileHeaderCodec.Verify(Wire.Slice(body, 1024, 512), key);
                    if (h.DayId != day || h.MaxDailyFileBytes != 262144 || h.ErrorFatalReservedBytesPerDay != 131072 || h.PolicyVersion != 1)
                        throw new StorageFormatException();
                    using (var records = new RecordCodec(new LoggingOptions()))
                    using (var codec = new BlockCodec(records))
                    {
                        var decoded = codec.Decode(h, key, body, 1536, body.Length - 1568);
                        if (decoded.Records.Length != Wire.U16(body, 20) || decoded.IsErrorClass != (Wire.U16(body, 22) == 1))
                            throw new StorageFormatException();
                        foreach (var record in decoded.Records)
                            if (!access.Matches(request, revision, generation, utc, record))
                                throw new UnauthorizedAccessException("Record outside current request/authorization intersection.");
                        BatchCoverageManifest coverage = null;
                        if (version == 2)
                        {
                            // 根据解码后的记录重新计算覆盖统计，再和封装内的清单核对，避免采信与实际内容不符的统计值。
                            var accumulator = new CoverageAccumulator();
                            foreach (var record in decoded.Records)
                                accumulator.Observe(record);
                            coverage = accumulator.Finish(request.Scope);
                            coverage.ValidateBytes(body);
                        }
                        return new VerifiedUploadEnvelope(decoded.Records, new ExportBookmark(day, end, Wire.Slice(body, 512, 32), offset, next, generation), before, Wire.U64(body, 64), Wire.Slice(body, 72, 16), coverage);
                    }
                }
            }
            finally
            {
                Wire.Clear(body);
            }
        }
    }
}
