# 提供密钥与可信时间

在初始化本地留存前准备 `IStorageKeyProvider`。提供者从已准备好的缓存快速返回，须支持后台线程调用；避免访问 Unity 对象、发起网络请求或等待用户交互。

- `TryAcquireCurrent()` 提供当前密钥，`TryAcquire(keyId)` 提供历史密钥。密钥标识为 1～64 个 ASCII 字符；加密密钥和认证密钥各为独立生成的 32 字节。每次成功返回的 `KeyMaterial` 由 SDK 接管。无法取得时返回 `Unavailable()`。
- 应用负责安全保存、轮换和恢复密钥。当天文件继续使用原密钥；只要留存文件或上传状态仍引用旧标识，就应能按标识取得旧密钥。密钥不可用时受影响的数据无法写入或恢复；SDK 不会改写明文。日志正文及标识均应避免包含凭据。

## 跨日与时间异常

可传入 `IJournalTimeAuthority`，由 `Observe()` 提供 UTC、单调时间、可信度和证据标识。只有应用确实取得可信时间证据时才返回 `Confirmed`。传入 `null` 使用保守的本地时间策略。

冷启动跨日、时间回拨等情况下可能报告 `TimeConfirmationRequired`，限制新日写入及过期清理。检查存储快照中的 `TimeReason`，通过提供者补充可信证据后再判断恢复情况。拒绝期间的日志不会自动补录。[本地留存](local-journal.md)
