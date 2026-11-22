// Unity 组合层和测试通过友元程序集访问引导与生命周期接口，业务入口仍由公开 API 提供。
using System.Runtime.CompilerServices;
[assembly: InternalsVisibleTo("Logos.EditModeTests")]
[assembly: InternalsVisibleTo("Logos.PlayModeTests")]
[assembly: InternalsVisibleTo("Logos.Storage.Unity")]
[assembly: InternalsVisibleTo("Logos.Storage.Unity.EditModeTests")]
[assembly: InternalsVisibleTo("Logos.Storage.Unity.PlayModeTests")]

[assembly: InternalsVisibleTo("Logos.Transport.Unity")]
[assembly: InternalsVisibleTo("Logos.Transport.Unity.EditModeTests")]
[assembly: InternalsVisibleTo("Logos.Transport.Unity.PlayModeTests")]
