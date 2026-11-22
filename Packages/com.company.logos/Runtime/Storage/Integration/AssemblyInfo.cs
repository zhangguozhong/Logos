using System.Runtime.CompilerServices;
// Unity 的传输启动逻辑需要核对已有存储会话，友元声明让这些组合入口保持 internal。
[assembly: InternalsVisibleTo("Logos.Storage.Unity.EditModeTests")]
[assembly: InternalsVisibleTo("Logos.Storage.Unity.PlayModeTests")]

[assembly: InternalsVisibleTo("Logos.Transport.Unity")]
[assembly: InternalsVisibleTo("Logos.Transport.Unity.EditModeTests")]
[assembly: InternalsVisibleTo("Logos.Transport.Unity.PlayModeTests")]
