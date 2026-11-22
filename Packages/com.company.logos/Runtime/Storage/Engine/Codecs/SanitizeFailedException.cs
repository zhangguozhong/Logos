using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Logos.Storage
{
    // 批量编码辅助入口将逐条编码返回的 SanitizeFailed 转换为此异常，调用方可区分脱敏失败和其他格式错误。
    public sealed class SanitizeFailedException : StorageFormatException
    {
    }
}
