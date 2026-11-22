using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Logos.Storage
{
    // 封块通过此接口索取随机字节，使测试可提供可重复的输入并单独覆盖随机源失败。
    internal interface IStorageEntropy
    {
        void Fill(byte[] bytes);
    }
}
