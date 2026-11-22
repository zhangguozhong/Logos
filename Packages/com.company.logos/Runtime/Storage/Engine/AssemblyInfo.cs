using System.Runtime.CompilerServices;

// 传输程序集可通过内部目录游标读取已提交块；测试程序集还需要访问状态副本和故障边界。
[assembly: InternalsVisibleTo("Logos.StorageTests")]
[assembly: InternalsVisibleTo("Logos.Storage.Unity.PlayModeTests")]

[assembly: InternalsVisibleTo("Logos.Transport")]

[assembly: InternalsVisibleTo("Logos.TransportTests")]

// 仅测试使用的自有进程验收探针；不提供运行时故障钩子。
[assembly: InternalsVisibleTo("Logos.TransportCrashProbe")]

[assembly: InternalsVisibleTo("Logos.Transport.Unity")]
[assembly: InternalsVisibleTo("Logos.Transport.Unity.EditModeTests")]
[assembly: InternalsVisibleTo("Logos.Transport.Unity.PlayModeTests")]
