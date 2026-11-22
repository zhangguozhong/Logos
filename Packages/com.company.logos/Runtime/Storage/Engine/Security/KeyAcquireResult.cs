using System;
using System.Numerics;

namespace Logos.Storage
{
    // 成功结果携带调用方接管的密钥材料；不可用结果允许恢复流程停止在原位置，保留原有密文和检查点。
    public sealed class KeyAcquireResult
    {
        public KeyAcquireStatus Status
        {
            get;
        }
        public KeyMaterial Material
        {
            get;
        }

        private KeyAcquireResult(KeyAcquireStatus status, KeyMaterial material)
        {
            Status = status;
            Material = material;
        }

        public static KeyAcquireResult Unavailable() =>
            new KeyAcquireResult(KeyAcquireStatus.KeyUnavailable, null);

        public static KeyAcquireResult Acquired(KeyMaterial ownedMaterial) =>
            new KeyAcquireResult(
                KeyAcquireStatus.Acquired,
                ownedMaterial ?? throw new ArgumentNullException(nameof(ownedMaterial))
            );
    }
}
