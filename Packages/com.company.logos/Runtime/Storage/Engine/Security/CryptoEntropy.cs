using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Logos.Storage
{
    // 由系统随机数生成器填满调用者提供的数组；封块若取不到所需随机数据，必须向上报告失败。
    internal sealed class CryptoEntropy : IStorageEntropy
    {
        public void Fill(byte[] bytes)
        {
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);
        }
    }
}
