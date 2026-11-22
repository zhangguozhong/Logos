using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Logos.Storage
{
    // 解码器在字段、长度或协议版本无法解释时抛出此异常，使恢复流程保留原文件供后续处理。
    public class StorageFormatException : Exception
    {
        public StorageFormatException()
            : base("Invalid or unsupported bounded storage data.")
        {
        }
    }
}
