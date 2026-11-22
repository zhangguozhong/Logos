# 日志等级与线上采集

默认最低等级为 Info。通过 `LogosLog` 或会话的 `TryLog` 显式记录业务日志，各等级均受额度限制。

| 等级 | 使用方式 |
|---|---|
| Trace、Debug | 临时诊断，需启用相应等级 |
| Info | 一般业务节点用 `Info`，还原现场所需的关键节点用 `KeyInfo` |
| Warn | 可恢复异常、降级或重试 |
| Error | 明确失败，有异常堆栈时可调用 `LogosLog.Exception` |
| Fatal | 业务判定为致命的故障 |
| Off | 关闭采集 |

例如 `LogosLog.KeyInfo("支付已提交", "payment.submitted")`。事件类型应稳定且非空。关键 Info 在普通预算内享有有限预留；普通 `Info` 即使带事件 ID 仍使用普通额度。Error/Fatal 共用独立的有限预留。高成本消息可先用 `IsEnabled` 判断等级是否启用；实际准入以写入调用的返回值为准。

## 如何判断丢在了哪里

业务日志接口返回 `AdmissionResult`。默认消息和堆栈最多保留 1,024 和 4,096 个 UTF-16 代码单元，截断与整条拒绝分别统计。

| 观察到的结果 | 含义与处理 |
|---|---|
| `Accepted` | 已入队。确认本地留存需核对存储提交；确认上传需匹配回执。 |
| `Filtered` | 当前策略过滤了这条日志。 |
| `RateLimited`、`QueueFull`、`ContentionRejected` | 额度耗尽、队列满或并发争用导致本次准入失败。满队列丢新，避免在业务线程循环重试。 |
| `InvalidInput`、`RecordTooLarge` | 核对事件标识、消息长度和单条预算。 |
| `Stopping`、`Faulted`、`StaleGeneration` | 核对当前会话和生命周期，停止向已失效的实例提交。 |

用 `LogosSession.GetHealthSnapshot()` 按等级和原因区分 `RejectedBeforeAccept` 与 `DroppedAfterAccept`。运行中计数可能跨越多个时刻；需要完整对账时在安全停止后读取。恢复和磁盘配额相关的拒绝见[本地留存](local-journal.md)。
