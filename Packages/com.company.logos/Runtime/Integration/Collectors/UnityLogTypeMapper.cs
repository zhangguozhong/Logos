using UnityEngine;
namespace Logos
{
    internal static class UnityLogTypeMapper
    {
        internal static bool TryMap(LogType type, out LogLevel level)
        {
            // 按 Unity 提供的枚举映射。调用者根据返回值跳过未知类型，不从正文或堆栈猜测严重性。
            switch (type)
            {
                case LogType.Log:
                    level = LogLevel.Info;
                    return true;
                case LogType.Warning:
                    level = LogLevel.Warn;
                    return true;
                case LogType.Error:
                case LogType.Assert:
                case LogType.Exception:
                    level = LogLevel.Error;
                    return true;
                default:
                    level = default;
                    return false;
            }
        }
    }
}
