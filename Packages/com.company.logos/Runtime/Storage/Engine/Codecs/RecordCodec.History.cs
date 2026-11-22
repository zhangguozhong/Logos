using System;
namespace Logos.Storage
{
    // 历史数据可能来自更宽松的采集配置。按协议允许的最大值重编码，才能在转移已认证记录时保留原字段。
    public sealed partial class RecordCodec
    {
        private readonly bool historical;
        // 只有传输友元程序集可取得此重编码器。输入来自经过认证的目录块。
        private RecordCodec()
        {
            historical = true;
            maxMessage = LogRecord.MaxMessageLength;
            maxStack = LogRecord.MaxStackLength;
            coreByteLimit = int.MaxValue;
            sanitized = Array.Empty<char>();
            MaximumEncodedSingleRecordBytes = 65536;
        }
        internal static RecordCodec CreateHistoricalReencoder() => new RecordCodec();
        internal bool TryReencodeAuthenticated(DecodedBlock authenticatedBlock, int index, byte[] destination, int offset, int capacity, out int written, out RecordEncodeFailure failure)
        {
            if (!historical || authenticatedBlock == null)
                throw new InvalidOperationException("Authenticated history reencoder required.");
            return TryEncode(authenticatedBlock.Records[index], destination, offset, capacity, out written, out failure);
        }
    }
}
