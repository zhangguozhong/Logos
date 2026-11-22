namespace Logos.Storage
{
    // 目标缓冲区太小、输入无效和加密服务不可用分别上报；只有成功封块才有可追加的字节范围。
    public enum BlockSealFailure
    {
        None,
        DestinationTooSmall,
        InvalidInput,
        CryptoUnavailable,
    }
}
