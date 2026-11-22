# 注入上传服务示例

在包管理器中导入 `Injected Upload`。导入示例不会启动上传。显式创建日志、存储、上传配置，将应用持有的服务、密钥提供者和安装及目的地标识传入 `LogosUploadOptions`。在 Unity 主线程调用 `InjectedUploadSample.TryStart`。

安装后先绑定用户，再调用 `PublishAuthorization` 发布有效授权。`TryRetrieve` 返回请求票据；通过 `Session.TryTakeControlResult` 取结果，用 `Session.GetTransferSnapshot()` 查看持久任务状态。卸载时逐帧调用 `TryStopAndReleaseHost`，返回真后再释放宿主和上传服务。[上传接入及失败处理](../../Documentation~/transport.md)
