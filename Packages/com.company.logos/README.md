# Logos Unity 包

在业务 asmdef 中引用 `Logos`。主线程通过 `LogosSdk.TryInitialize` 选择自定义处理器、本地留存或带上传的留存。应用负责提供密钥、更新诊断 UID 并管理宿主卸载。包导入后可从 Package Manager 的 Samples 中选择示例。

| 接入任务 | 文档与示例 |
|---|---|
| 初始化、重复初始化和冲突处理 | [开始使用](Documentation~/getting-started.md) · [内存采集示例](Samples~/BasicCapture/README.md) |
| 选择线上等级与关键事件 | [分级、重复折叠和丢弃](Documentation~/log-levels.md) |
| 配置本地文件和密钥 | [本地留存](Documentation~/local-journal.md) · [密钥与时间](Documentation~/key-and-time-providers.md) · [本地留存示例](Samples~/LocalJournal/README.md) |
| 登录、切号、暂停与卸载 | [身份与生命周期](Documentation~/Lifecycle.md) |
| 按 UID 回捞并注入上传服务 | [传输接入](Documentation~/transport.md) · [HTTP 适配器](Documentation~/Http-README.md) · [注入上传示例](Samples~/InjectedUpload/README.md) |
