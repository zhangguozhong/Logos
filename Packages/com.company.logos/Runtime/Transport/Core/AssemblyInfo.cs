using System.Runtime.CompilerServices;
// 传输测试需要直接构造授权、状态页与恢复场景。
[assembly: InternalsVisibleTo("Logos.TransportTests")]
[assembly: InternalsVisibleTo("Logos.Transport.HttpTests")]

// 仅测试使用的自有进程验收探针；不提供运行时故障钩子。
[assembly: InternalsVisibleTo("Logos.TransportCrashProbe")]

[assembly: InternalsVisibleTo("Logos.Transport.Unity")]
[assembly: InternalsVisibleTo("Logos.Transport.Unity.EditModeTests")]
[assembly: InternalsVisibleTo("Logos.Transport.Unity.PlayModeTests")]

// 有界本地墙上时间验收工具。
[assembly: InternalsVisibleTo("Logos.StorageBenchmarks")]
