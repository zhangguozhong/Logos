# 本地日志留存

需要线上留存时，创建 `LogosSettings` 和 `StorageSettings` 资产，在 Unity 主线程显式初始化。应用提供持久可恢复的密钥；`appTime` 可传 `null` 使用保守本地时间。[密钥与时间接口](key-and-time-providers.md)

```csharp
using Logos;
using Logos.Storage;

bool installed = LogosSdk.TryInitialize(
    loggingSettings, storageSettings, appKeys, appTime,
    out StorageLoggingSession owner, out StorageInitializationStatus status);
```

保留返回的 owner。安装成功后，通过 `owner.GetHealthSnapshot()` 查看存储状态。首次恢复期间，显式 Error、Fatal、异常和带事件 ID 的 KeyInfo 可以在现有额度内暂存。请检查每次调用的返回值。普通 Info 和 Warn 在此期间仍可能返回 NotReady 或 Recovering。已接收但尚未写入文件的记录可能随进程终止而丢失。被拒绝的记录不会在就绪后补录。`Ready` 表示存储恢复完成；判断能否继续写入还需结合 `EffectiveDay`、`TimeReason` 和剩余额度。

启用诊断时，内置的 `session.start` 保留 Unity 实例启动时的时间和身份。恢复期间切号也会保留先前已接收记录的原归属。

## 配置留存范围

默认目录为 `Application.persistentDataPath/Logos`，每个有效 UTC 日使用一个 `.logos` 文件。

| StorageSettings 属性 | 默认值 | 作用 |
|---|---:|---|
| `MaxDailyFileBytes` | 8 MiB | 当天文件总额度 |
| `ErrorFatalReservedBytesPerDay` | 2 MiB | 从当日总额中划给 Error/Fatal，两类共用 |
| `RetentionDays` | 7 天 | 以 UTC 日计算的保留期，可配置 1～1,024 天 |
| `MaxDirectoryBytes` | 64 MiB | 日志目录总预算，含元数据 |
| `EnableMappedSealedBuffer` | 关闭 | 可选的文件映射恢复，用于异常退出后恢复已封装的日志 |

普通和错误额度分别计量，互不借用。某类当日可用空间放不下待写块时，会丢弃该块并关闭该类当日写入；其余类别仍受自身额度约束。到期文件可清理，目录满时保留未过期文件。保留天数不能保证忙碌时每条日志都能留到期满。

配置在初始化时生效。修改资产后需安全停止并重新初始化；新的每日总额和错误预留在下一确认有效日采用，当天恢复沿用已有文件的策略。调整目录上限会约束后续写入。

## 可选的文件映射恢复

在目标平台完成兼容性和性能验证后，可在初始化前设置 `storageSettings.EnableMappedSealedBuffer = true`。若出现配置冲突，安全停止当前会话后再切换。映射占用计入日志目录额度。平台接口不可用、目录额度不足或映射初始化失败时，SDK 继续使用普通文件写入。

初始化时可通过 `owner.GetHealthSnapshot()` 观察 `MappedBufferInitializing` 和 `MappedBufferActive`，通过 `MappedBlocksRecovered` 查看重启时恢复或已提交核对的块数。恢复需要可信时间、匹配的密钥和有效的本地文件。异常退出时，仍在队列或尚未封装的记录可能丢失，突然断电后的持久性也受设备和文件系统影响。

## 失败与恢复

| 观察到的情况 | 接入方处理 |
|---|---|
| `AlreadyInitialized` 或 `ConfigurationConflict` | 相同配置和依赖可复用原会话。配置或依赖冲突时联系原 owner，安全停止后再切换。 |
| `TimeConfirmationRequired` | 核对可信时间来源和有效日，时间未确认时避免把拒绝误判为磁盘已满。 |
| 密钥不可用 | 恢复原密钥提供者及文件引用的历史密钥。SDK 不会降级写明文。 |
| `DirectoryBusy`、`Unsupported`、`InvalidPath` | 核实目录独占、平台支持和路径。处理启动时返回的非空 owner，条件修复后再初始化。 |
| 异常重启或写入失败 | 等待后台恢复并检查健康状态。可验证的当天文件可按剩余额度续写；恢复受阻时保留现场，不自行截断文件或删除目录。 |

入队后仍可能在写入或停止时丢失。存储快照的 `CommittedRecords` 统计已完成本地提交的记录，不承诺突然断电后的持久性或服务端已接收。通过 `owner.UnitySession` 关联用户和宿主状态；卸载前按帧轮询统一停止入口。[会话与安全卸载](Lifecycle.md) · [本地留存样例](../Samples~/LocalJournal/README.md) · [上传与回捞](transport.md)
