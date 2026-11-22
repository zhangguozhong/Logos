# 开始使用

在 Unity 2022.3 的 Package Manager 中选择 Add package from disk，打开 `Packages/com.company.logos/package.json`。业务 asmdef 添加一个 `Logos` 引用。在 Samples 中可按需导入 Basic Capture、Local Journal 或 Injected Upload。

线上留存使用[本地日志重载](local-journal.md)，需要回捞时使用[支持注入上传服务的重载](transport.md)。下面的 Basic Capture 示例使用自定义内存处理器。

在唯一 Unity 主线程入口（如 Start，运行时 SubsystemRegistration 后）显式初始化：

```csharp
var settings = ScriptableObject.CreateInstance<LogosSettings>();
// processor 必须是已就绪、遵守同步借用协议的 ILogBatchProcessor。
bool initialized = LogosSdk.TryInitialize(settings, processor, out var owner, out var status);
// 保留本次返回的 owner；冲突时不要接管 Current。
```

也可通过 Unity 菜单 `Create > Logos > Settings` 创建配置资产。动态创建的资产由调用方在主线程销毁。调整队列或速率预算前应先安全停止；启用开关、最低等级等策略可通过 `TryUpdatePolicy` 更新。

业务写入接口返回 `AdmissionResult`。昂贵字符串可先用 `IsEnabled` 判断是否启用。需要查询留存或丢弃时，按需读取 `owner.GetHealthSnapshot()`。[等级与准入结果](log-levels.md)

三种模式在主线程比较启动时的配置值与依赖实例。相同请求返回 `AlreadyInitialized` 与同一会话；不同的有效模式、配置或服务返回 `ConfigurationConflict`。正在停止时返回 `Stopping`。无效输入仍返回参数或依赖错误。初始化成功表示已安装；落盘还需等待存储 Ready，上传还需有效授权。

宿主卸载时在主线程逐帧调用 `LogosSdk.TryStopForUnload(owner, TimeSpan.FromMilliseconds(100))`，直到返回 true。首次调用确定停止预算，随后只轮询。传入 null 或已停止的旧会话不会停止其他活动会话。若启动失败但返回了会话，也要保留它并走同一流程。参见[生命周期](Lifecycle.md)。

## 业务事件与用户切换

仅显式调用 Logos 的日志进入 SDK。已有 `Debug.Log`、`Debug.LogWarning`、`Debug.LogError` 或 `Debug.LogException` 调用需要业务按需迁移。已有异常堆栈可传给 `UnityLogger.Exception`。关键节点可调用 `UnityLogger.KeyInfo(message, eventTypeId)`，使用稳定事件类型。Unity 启动时采集一次设备快照。登录、切号和登出由业务在主线程调用 `LogosSession.UpdateUser(generation, expectedIdentityVersion, diagnosticUserId, out identityVersion)`，使用后台映射的诊断标识，登出传 null。版本冲突时先核对当前会话，避免迟到的登录回调覆盖新用户。跨切号完成的异步操作可在开始时 `CaptureContext()`，再用会话的 `TryLog(handle, ...)` 写入。[生命周期](Lifecycle.md) · [按 UID 回捞](transport.md)
