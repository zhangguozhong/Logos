# 内存采集示例

在包管理器中导入 `Basic Capture`，创建游戏对象并挂载 `BasicCaptureExample`。可指定 `LogosSettings` 资产，或使用示例默认配置。避免同时启用另一个初始化组件。

进入播放模式后示例通过 `LogosLog` 写入六个等级的显式业务日志。默认过滤 Trace 和 Debug。调用 `QueryHealth()` 查看计数；示例仅在内存中处理日志。需要从文件找回现场时，使用[本地留存示例](../LocalJournal/README.md)。

禁用组件会请求停止。实际宿主释放 Unity 前仍需按帧检查是否可安全卸载。[会话与停止](../../Documentation~/Lifecycle.md)
