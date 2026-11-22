# 日志等级与线上采集

默认最低等级为 Info，线上默认采集 Info、Warn、Error 和 Fatal。Unity 自动采集只覆盖 Unity 已输出且触发回调的消息。以下映射适用于默认策略，所有等级均受配额限制。

| 等级 | Unity 自动来源 | 业务使用 |
|---|---|---|
| Trace、Debug | 无 | 通过同名 `UnityLogger` 接口显式记录，启用相应等级后才采集 |
| Info | `LogType.Log` | 一般业务信息用 `Info`，重建现场所必需的业务节点用 `KeyInfo` |
| Warn | `Warning` | 可恢复异常、降级或重试 |
| Error | `Error`、`Assert`、`Exception` | 明确失败；`UnityLogger.Exception` 可传入已有堆栈 |
| Fatal | 无 | 由业务显式调用 `Fatal`；Unity 异常默认映射到 Error |
| Off | 无 | 用于关闭采集 |

例如 `UnityLogger.KeyInfo("支付已提交", "payment.submitted")`。事件类型应稳定且非空。关键 Info 在普通预算内享有有限预留；普通 `Info` 即使带事件 ID 仍使用普通额度。Error/Fatal 共用独立的有限预留。高成本消息可先用 `IsEnabled` 判断等级是否启用；实际准入以写入调用的返回值为准。

## 重复 Info 如何还原

普通自动采集模式下，同一身份中正文与堆栈完全相同的短 Unity Info 可能以首条加重复摘要保存。还原时按首条 RecordId 汇总 `logos.unity.repeat` 摘要中的增量，再加上首条本身。业务显式 Info 和 Warn、Error、Fatal 独立准入。限流、停止或存储额度耗尽时，文件可能只保留部分重复次数。

## 如何判断丢在了哪里

业务日志接口返回 `AdmissionResult`。默认消息和堆栈最多保留 1,024 和 4,096 个 UTF-16 代码单元，截断与整条拒绝分别统计。

| 观察到的结果 | 含义与处理 |
|---|---|
| `Accepted` | 已入队。确认本地留存需核对存储提交；确认上传需匹配回执。 |
| `Filtered` | 策略过滤或自动 Info 已折叠。用健康快照的 `CoalescedUnityInfo` 查看入口折叠数，最终重复次数以可解码文件为准。 |
| `RateLimited`、`QueueFull`、`ContentionRejected` | 额度耗尽、队列满或并发争用导致本次准入失败。满队列丢新，避免在业务线程循环重试。 |
| `InvalidInput`、`RecordTooLarge` | 核对事件标识、消息长度和单条预算。 |
| `Stopping`、`Faulted`、`StaleGeneration` | 核对当前会话和生命周期，停止向已失效的实例提交。 |

用 `LogosSession.GetHealthSnapshot()` 按等级和原因区分 `RejectedBeforeAccept` 与 `DroppedAfterAccept`。运行中计数可能跨越多个时刻；需要完整对账时在安全停止后读取。恢复和磁盘配额相关的拒绝见[本地留存](local-journal.md)。
