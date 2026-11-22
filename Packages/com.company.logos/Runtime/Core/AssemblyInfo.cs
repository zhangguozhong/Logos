// 这些程序集可以直接访问 Core 的内部类型，供存储、传输及其测试组合流水线使用。
using System.Runtime.CompilerServices;
[assembly: InternalsVisibleTo("Logos.CoreTests")]
[assembly: InternalsVisibleTo("Logos.Storage")]
[assembly: InternalsVisibleTo("Logos.EditModeTests")]
[assembly: InternalsVisibleTo("Logos.TestSupport")]
[assembly: InternalsVisibleTo("Logos.Unity")]
[assembly: InternalsVisibleTo("Logos.PlayModeTests")]
[assembly: InternalsVisibleTo("Logos.StorageTests")]
[assembly: InternalsVisibleTo("Logos.Storage.Unity.PlayModeTests")]
[assembly: InternalsVisibleTo("Logos.Storage.Unity")]
[assembly: InternalsVisibleTo("Logos.StorageBenchmarks")]

[assembly: InternalsVisibleTo("Logos.Transport")]
[assembly: InternalsVisibleTo("Logos.TransportTests")]

[assembly: InternalsVisibleTo("Logos.Transport.Unity")]
[assembly: InternalsVisibleTo("Logos.Transport.Unity.EditModeTests")]
[assembly: InternalsVisibleTo("Logos.Transport.Unity.PlayModeTests")]
