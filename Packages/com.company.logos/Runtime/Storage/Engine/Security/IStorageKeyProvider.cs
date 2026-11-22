using System;
using System.Numerics;

namespace Logos.Storage
{
    // 创建新状态时请求当前密钥，读取历史文件则按其持久化标识取回旧密钥，两次请求可能返回不同材料。
    public interface IStorageKeyProvider
    {
        KeyAcquireResult TryAcquireCurrent();
        KeyAcquireResult TryAcquire(string keyId);
    }
}
