# 本地留存示例

在包管理器中导入 `Local Journal`，把 `LocalJournalExample` 挂到与运行时同寿命的游戏对象。在 Unity 主线程显式调用 `Initialize(appKeys, appTime)`，提供可恢复历史文件的密钥。时间提供者可传 `null`。

用 `QueryStorageHealth()` 检查 `Ready`、`Recovering`、`Blocked` 和 `TimeReason`。初始化成功只表示已安装，恢复期间被拒绝的记录不会补写。请勿把示例挂在频繁隐藏的页面上。宿主卸载时逐帧调用 `TryStopForUnload()`，直到返回真。[留存配置和失败处理](../../Documentation~/local-journal.md)
