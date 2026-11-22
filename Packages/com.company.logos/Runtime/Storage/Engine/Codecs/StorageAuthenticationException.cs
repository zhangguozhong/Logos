using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Logos.Storage
{
    // 认证不通过属于存储格式读取失败，恢复逻辑可统一阻止继续扫描，调用方也能单独识别认证错误。
    public sealed class StorageAuthenticationException : StorageFormatException
    {
    }
}
