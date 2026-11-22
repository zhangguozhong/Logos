namespace Logos.Transport.Http
{
    internal static class HttpReceipt
    {
        internal static UploadResult Parse(byte[] bytes, int length, UploadRequest request)
        {
            if (bytes == null || length != 80 || bytes.Length < length || request == null)
                return new UploadResult(UploadStatus.Permanent);
            const string magic = "LOGOSAK1";
            // 回执中的批次 ID 和正文摘要各占 32 字节，本协议用同一个正文哈希校验这两个位置。
            int mismatch = 0;
            for (int i = 0; i < 8; i++)
                mismatch |= bytes[i] ^ magic[i];
            for (int i = 0; i < 32; i++)
            {
                int expected = (Hex(request.BatchId[i * 2]) << 4) | Hex(request.BatchId[i * 2 + 1]);
                mismatch |= bytes[8 + i] ^ expected;
                mismatch |= bytes[40 + i] ^ expected;
            }
            ulong received = 0;
            for (int i = 0; i < 8; i++)
                received |= (ulong)bytes[72 + i] << (8 * i);
            if (mismatch != 0 || received != (ulong)request.Body.Length)
                return new UploadResult(UploadStatus.Permanent);
            return new UploadResult(UploadStatus.Accepted, request.BatchId, request.BodySha256, (long)received);
        }
        private static int Hex(char c) => c <= '9' ? c - '0' : c - 'a' + 10;
    }
}
