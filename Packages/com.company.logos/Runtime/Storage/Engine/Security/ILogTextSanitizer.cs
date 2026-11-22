using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Logos.Storage
{
    // 成功时 written 指定输出缓冲区中可编码的字符数，失败时当前记录应走脱敏失败路径。
    public interface ILogTextSanitizer
    {
        bool TrySanitize(string input, char[] output, int capacity, out int written);
    }
}
