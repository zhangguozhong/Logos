using System;
using System.Numerics;

namespace Logos.Storage
{
    // 只有 Acquired 对应可用于认证的密钥材料；无法获取时，恢复不能把文件当作空目录初始化。
    public enum KeyAcquireStatus
    {
        Acquired,
        KeyUnavailable,
    }
}
