# 会话、线程与宿主卸载

让一个生命周期覆盖 Unity runtime 的对象持有初始化返回的会话（owner）。页面可以多次进出；只在实际卸载 runtime 或需要更换启动配置时停止 SDK。

## 哪些操作在哪个线程调用

| 调用位置 | 接口与约束 |
|---|---|
| Unity 主线程 | 读取配置资产，调用 `LogosSdk.TryInitialize`、`TryAttach`、身份与上下文更新、前后台通知，以及停止和轮询。原生宿主收到的通知应派发到 Unity 主线程。 |
| 任意产生日志的线程 | 调用 `LogosLog` 或 `LogosSession.TryLog`。业务格式化消息和获取堆栈的成本由调用线程承担。 |
| SDK 后台线程 | 自定义 `ILogBatchProcessor` 同步借用批次，仅在本次调用内访问记录，按确定完成的前缀返回。处理器应检查取消和截止时间，避免调用 Unity API 或使用 `Debug.Log` 记录自己的处理过程。 |

## 初始化与重复接入

在 Unity 运行时初始化后，从主线程调用所选模式的 `LogosSdk.TryInitialize`。启动前没有历史消息缓冲。相同配置值和相同依赖实例返回 `AlreadyInitialized` 和原会话；不同的有效请求返回 `ConfigurationConflict`。遇到冲突应由原 owner 协调，避免通过全局会话接管其他调用方的实例。调整队列或速率预算需安全停止后重建；运行中的开关和等级可用 `TryUpdatePolicy` 更新。[初始化示例](getting-started.md)

自定义处理器模式直接返回 `LogosSession`；本地留存和上传模式使用返回对象的 `UnitySession` 访问以下接口。保存 `UnityInstanceId` 和 `Generation` 随宿主通知传回，`TryAttach(instanceId, generation)` 可确认当前运行实例。

## 登录、切号与页面访问

- **更新用户**。登录或切号请求发起时保存 `Generation` 和 `Context.IdentityContextVersion`。在主线程调用 `UpdateUser(generation, expectedIdentityVersion, diagnosticUserId, out identityVersion)`。使用后台映射的诊断标识，登出传 `null`。启动时身份为匿名，恢复登录态后再绑定。
- **处理冲突**。`Updated` 或 `Unchanged` 表示该请求的身份已生效。`Conflict` 表示版本已变化，应丢弃迟到的请求；`StaleSession` 或 `Stopping` 时停止向旧会话提交。遇到 `WrongThread` 应回到主线程并重新核对原请求是否有效。
- **跨切号异步操作**。订单等请求发起时可用 `CaptureContext()` 保存句柄，完成时用 `TryLog(handle, ...)` 记录结果。未传句柄的业务日志按调用时的身份归属。切号后，已接收记录仍归原用户。
- **页面与前后台**。`TryUpdateContext` 和 `TryLeaveVisit` 使用请求发起时保存的 `ContextRevision`。更新页面时保留当前身份版本。页面离开而 runtime 留驻时调用 `TryLeaveVisit`；前后台变化调用 `TryNotifyHostForeground(instanceId, generation, foreground)`。宿主通知优先，Unity pause 可作补充，失焦本身不表示进入后台。

## 安全停止

宿主准备销毁 runtime 时，在 Unity 主线程**每帧调用一次** `LogosSdk.TryStopForUnload(owner, budget)`，直到返回 `true`。首次调用确定排空预算；后续轮询不会延长截止时间。需要尽快放弃待处理记录时，首次调用可传 `dropPending: true`。

返回 `false` 时保留 owner 和 runtime，下帧继续。即使出现 `StopTimedOut` 或 `Faulted`，也要等安全卸载入口放行。初始化返回 `false` 但给出了非空 owner 时同样需要收尾；传入空值或旧 owner 不会停止另一个活动会话。需要最终计数时，通过保留的 `LogosSession.GetHealthSnapshot()` 查询。

崩溃或系统强杀可能丢失尚未持久化的尾部。当前已有 Editor 验证；Android/iOS 真机的卸载与再次挂接仍需在实际宿主验收。WebGL Player 不支持此线程模式。
