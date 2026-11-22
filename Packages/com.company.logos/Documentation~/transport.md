# 注入上传服务与按用户回捞

在 Unity 主线程准备日志、存储和上传配置，并提供安装标识、目的地、可信时间及密钥。目的地须与上传服务声明的一致。

```csharp
var upload = new LogosUploadOptions
{
    Settings = transportSettings,
    Service = myUploadService,
    TransferKeys = transferKeys,
    InstallationId = installationId,
    DestinationId = destinationId
};
bool installed = LogosSdk.TryInitialize(loggingSettings, storageSettings, storageKeys, trustedTime,
    upload, out var session, out var status);
```

`TransportSettings` 要求每日上传总额度、错误预留和自动任务的输入字节、输出字节、批次数均为有限正数；错误预留须小于每日总额。关闭自动任务时也应设置这些上限。`AutoErrorEnabled` 默认开启，仅在已获授权时处理本地已提交的 Error/Fatal。

## 授权与回捞

1. 初始化成功后，从 `session.UnitySession` 取得实例、代次并绑定诊断 UID。等待 `session.GetStorageHealthSnapshot().Ready` 后再判断本地留存。没有上传授权时，应用仍可采集本地日志。
2. 按后台可信时间和修订号生成 `TransferAuthorizationSnapshot`，用 `TryUpdateAuthorization` 发布。请求范围应同时限定诊断 UID、安装、会话、身份版本、时间和等级。同一用户在多个安装上各自派单。无法核验身份时，任务可返回 `IdentityUnverifiable`；请勿扩大为整机导出。
3. 在主线程调用 `TryQueueManual(instanceId, generation, request, out ticket, out admission)`。`Queued` 表示请求已排队。`Busy` 时先读取已有结果，稍后再试。用 `TryTakeControlResult` 读取票据，每份结果只能取一次。
4. `AcceptedInMemory` 表示已受理，`Existing` 表示已识别相同请求。用 `GetTransferSnapshot()` 查看任务状态；`DurableObserved` 表示已观察到持久状态。收到匹配回执才能判定对应批次上传成功。需释放已结束的手动任务时，使用持久状态中的句柄调用 `TryQueueRelease`。
5. 登出或切号时用 `UpdateUser` 更新新日志归属。如果还要收回既有上传权限，调用 `TryRevokeAuthorization` 并推进修订号。撤权无法收回已经发送的数据。

## 自定义服务与停止

`IUploadService.TryStart` 应快速返回，交给 SDK 管理重试。返回的操作借用只读 `UploadBody`，直至 `TryClose` 返回真。接收端达到约定的持久化条件后再返回成功；回执必须匹配批次标识、SHA-256 和接收长度。

`RequestCancel` 应立即返回。上传仍在进行或未真正关闭时，保留服务和会话并继续轮询。服务由调用方释放；请在主线程逐帧调用 `LogosSdk.TryStopForUnload`，返回真后再销毁宿主资源。[停止与卸载](Lifecycle.md) · [上传示例](../Samples~/InjectedUpload/README.md) · [HTTP 接入](Http-README.md)
