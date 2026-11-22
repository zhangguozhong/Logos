using System;
using System.Numerics;
using System.Security.Cryptography;

namespace Logos.Storage
{
    // 加密与认证使用不同的密钥副本；一次性封块凭证绑定两者的指纹，使同名但内容不同的密钥无法复用凭证。
    public sealed class KeyMaterial : IDisposable
    {
        private readonly byte[] enc,
            mac;
        private bool disposed;
        private readonly byte[] materialFingerprint = new byte[32];
        private bool fingerprintReady;
        public string KeyId
        {
            get;
        }

        public KeyMaterial(string id, byte[] encryptionKey, byte[] authenticationKey)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 64)
                throw new ArgumentException("KeyId must have 1..64 ASCII bytes.", nameof(id));
            foreach (char c in id)
                if (c < 0x21 || c > 0x7e)
                    throw new ArgumentException("KeyId must use printable ASCII.", nameof(id));
            if (
                encryptionKey == null
                || authenticationKey == null
                || encryptionKey.Length != 32
                || authenticationKey.Length != 32
            )
                throw new ArgumentException("AES and MAC keys must each have 32 bytes.");
            int diff = 0;
            for (int i = 0; i < 32; i++)
                diff |= encryptionKey[i] ^ authenticationKey[i];
            if (diff == 0)
                throw new ArgumentException(
                    "Encryption and authentication key material must be independent."
                );
            KeyId = id;
            enc = (byte[])encryptionKey.Clone();
            mac = (byte[])authenticationKey.Clone();
        }

        public byte[] CopyEncryptionKey()
        {
            Check();
            return (byte[])enc.Clone();
        }

        public byte[] CopyAuthenticationKey()
        {
            Check();
            return (byte[])mac.Clone();
        }

        // KeyId 不是密钥材料身份。延迟计算并限定在本密钥生命周期内，不跨轮换缓存。
        internal byte[] CopyMaterialFingerprint()
        {
            EnsureMaterialFingerprint();
            return (byte[])materialFingerprint.Clone();
        }

        internal bool MatchesMaterialFingerprint(byte[] fingerprint)
        {
            EnsureMaterialFingerprint();
            return Wire.Equal(materialFingerprint, fingerprint);
        }

        private void EnsureMaterialFingerprint()
        {
            Check();
            if (fingerprintReady)
                return;
            byte[] material = new byte[64];
            try
            {
                Buffer.BlockCopy(enc, 0, material, 0, 32);
                Buffer.BlockCopy(mac, 0, material, 32, 32);
                byte[] hash = null;
                try
                {
                    using (var sha = SHA256.Create())
                        hash = sha.ComputeHash(material);
                    Buffer.BlockCopy(hash, 0, materialFingerprint, 0, 32);
                    fingerprintReady = true;
                }
                finally
                {
                    Wire.Clear(hash);
                }
            }
            finally
            {
                Wire.Clear(material);
            }
        }

        internal void Check()
        {
            if (disposed)
                throw new ObjectDisposedException(nameof(KeyMaterial));
        }

        public void Dispose()
        {
            if (disposed)
                return;
            Array.Clear(enc, 0, enc.Length);
            Array.Clear(mac, 0, mac.Length);
            Wire.Clear(materialFingerprint);
            disposed = true;
        }
    }
}
