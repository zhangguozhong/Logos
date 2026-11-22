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

## 对接只接收文件路径的上传服务

初始化时将 `new FileUploadAdapter(businessUploader)` 赋给同一个 `LogosUploadOptions.Service`。业务上传器实现 `IFileUploader`，其 `DestinationId` 应与配置一致。需要复用同一会话时保留该适配器实例。

文件工作区在 POSIX 平台仅支持 x64 和 arm64 进程，沿用本地日志默认目录租约对 64 位 POSIX 的要求。32 位 Android 进程会在创建暂存目录或文件前拒绝文件导出。若使用其他目录租约打开已有暂存目录，且当前平台无法确认该目录的占用，SDK 保留残留文件，暂停新增落盘，并在 `DirectoryUsage.IsUploadFileUsageKnown` 返回 `false`。

`TryStart` 接收本批次已授权、校验完成的临时文件路径，须立即返回单次操作句柄。业务决定协议和鉴权方式，按只读方式打开路径；用服务端实际返回的批次 ID、正文 SHA-256 和接收长度构造回执。接收端须按批次幂等保存。限流、临时失败和取消分别通过原有操作接口报告，Logos 负责退避和重新授权检查。业务关闭底层自动重传；只有读文件、网络传输及相关回调均已释放该路径时，才能报告实际关闭。Logos 随后删除文件。跨进程离线排队的上传接口不能保证这一生命周期。

撤权和交付路径有明确的先后顺序。撤权成功返回后，旧授权尚未交付的路径不会再传给业务。若 `TryStart` 已进入，业务会收到取消请求；在操作确认实际关闭之前，文件仍由该操作借用。`TryStart` 只安排一次异步发送并立即返回，禁止同步等待 Unity 主线程、网络响应或文件读取。否则同时发生的撤权或停止可能被业务回调阻塞。

异常退出后，无论下次注入正文服务还是文件适配器，Logos 都会在取得日志目录所有权后清理已有工作区，无需用户重新授权。磁盘额度按剩余文件计费。工作区内的未知文件会保留，并阻止新的文件导出；同目录下可确认归属的普通暂存文件仍会清理。业务应将自己的文件放在独立目录。

<a id="export-key-alignment"></a>

## 从 Unity 的 TransferKeys 到离线解密

Unity 创建回捞批次时，从 `TransferKeys.TryAcquireCurrent()` 取得导出密钥，并把密钥 ID 写入批次。排障人员在受控环境中单独取得对应的密钥配置。解码时，logos-cli 按文件中的 ID 精确查找配置，再用对应的 AES 密钥解密、用 MAC 密钥校验。两端须对齐三个值：区分大小写的 ID、32 字节加密密钥、32 字节独立认证密钥。CLI 配置中的 Base64 表示这两组原始字节。本地留存使用的 `storageKeys` 可独立管理，解码回捞批次只需提供导出密钥。

### 1. 在 Unity 注入导出密钥

下面两组公开测试值仅用于本地演示。生产环境由业务的密钥管理渠道向两端分别提供对应版本的材料，请勿将生产密钥写入源码、公开仓库或日志。

```csharp
using System;
using Logos.Storage;

sealed class DemoTransferKeys : IStorageKeyProvider
{
    private readonly string currentId;

    public DemoTransferKeys(string currentId)
    {
        this.currentId = currentId;
    }

    public KeyAcquireResult TryAcquireCurrent() => TryAcquire(currentId);

    public KeyAcquireResult TryAcquire(string keyId)
    {
        switch (keyId)
        {
            case "acceptance-key":
                return Acquire(keyId, "BwAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", "CQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=");
            case "acceptance-key-v2":
                return Acquire(keyId, "CwAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", "DQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=");
            default:
                return KeyAcquireResult.Unavailable();
        }
    }

    private static KeyAcquireResult Acquire(string id, string aes, string mac)
    {
        return KeyAcquireResult.Acquired(new KeyMaterial(id,
            Convert.FromBase64String(aes), Convert.FromBase64String(mac)));
    }
}
```

在本页开头的初始化示例前定义 `var transferKeys = new DemoTransferKeys("acceptance-key");`，再将它赋给 `LogosUploadOptions.TransferKeys` 并调用 `LogosSdk.TryInitialize`。初始化成功后，按本页授权与回捞步骤提交请求。让业务接收端完整保存一个已交付批次，命名为 `retrieval-v1.batch`。正文模式保存请求的原始正文字节，文件模式保存上传器收到的文件内容。设备上的 `.logos` 日文件不能作为此命令的输入。

### 2. 配置 logos-cli 并解码

在受控的排障目录创建 `export-keys.json`。下面同时配置当前版本和下一版本，以便演示轮换。仅使用第一版时，只需保留第一项。

```json
{
  "exportKeys": [
    {
      "id": "acceptance-key",
      "encryptionKeyBase64": "BwAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
      "macKeyBase64": "CQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="
    },
    {
      "id": "acceptance-key-v2",
      "encryptionKeyBase64": "CwAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
      "macKeyBase64": "DQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="
    }
  ]
}
```

在解压后的 CLI 目录执行以下命令，示例假定配置和批次也在该目录。成功时退出码为 0，`timeline.jsonl` 每行一条已验证的记录。

```sh
dotnet logos-cli.dll decode --keys export-keys.json --format jsonl retrieval-v1.batch > timeline.jsonl
```

### 3. 轮换后读取旧批次

先在解码端加入新 ID 和新材料，保留旧条目。随后在 Unity 下次初始化时，将提供器改为 `new DemoTransferKeys("acceptance-key-v2")`。新创建的批次使用新 ID，已导出的旧批次继续引用原 ID。避免在同一 ID 下覆盖密钥。将新批次保存为 `retrieval-v2.batch` 后，可用同一份配置读取两代批次：

```sh
dotnet logos-cli.dll decode --keys export-keys.json --format jsonl retrieval-v1.batch retrieval-v2.batch > combined.jsonl
```

旧批次仍需回读或仍有旧批次待交付时，保留对应版本的密钥。找不到批次指定的 ID 时，CLI 返回 3；ID 存在但材料错误导致认证失败时返回 5。两种情况下整次命令均不输出日志记录。修正配置后可对同一批次重试，无需重新回捞。
