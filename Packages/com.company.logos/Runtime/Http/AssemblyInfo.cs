using System.Runtime.CompilerServices;
// 允许 HTTP 测试直接验证内部回执解析和上传操作的关闭时序。
[assembly: InternalsVisibleTo("Logos.Transport.HttpTests")]
