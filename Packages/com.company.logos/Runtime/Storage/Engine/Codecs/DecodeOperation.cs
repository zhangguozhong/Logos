namespace Logos.Storage
{
    // 仅用于认证顺序测试的内部观察接口，不是加密或 provider 插件。
    // 测试用这些观测点确认伪造的认证码会提前退出，避免让未认证的内容进入解密或解压。
    internal enum DecodeOperation
    {
        Decrypt,
        Inflate,
    }
}
