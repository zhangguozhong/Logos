# logos-cli 回捞批次解码工具

适用于持有导出密钥的排障人员。输入为 Logos 已交给上传服务的完整批次正文。正文上传模式保存请求正文，文件上传模式保存业务上传接口实际收到的文件。扩展名不限。设备上的 `.logos` 日文件、HTTP 头和参考接收器的归档清单不属于此输入格式。

## 下载与运行

从 [logos-cli v0.1.0 发布页](https://github.com/zhangguozhong/Logos/releases/tag/logos-cli-v0.1.0) 下载 `logos-cli-0.1.0-net8.0.zip`，解压全部文件。安装 .NET 8 运行时后，在解压目录运行以下命令。Windows、macOS 和 Linux 使用相同的 DLL 入口，无需 Unity。可用 `dotnet --list-runtimes` 检查是否已有 `Microsoft.NETCore.App 8.0.x`。

```sh
dotnet logos-cli.dll --help
dotnet logos-cli.dll decode --keys export-keys.json first.batch second.batch
dotnet logos-cli.dll decode --keys export-keys.json --format jsonl first.batch second.batch > timeline.jsonl
```

发布页同时提供 SHA-256 校验文件。在 macOS 或 Linux 上可在下载目录执行 `shasum -a 256 -c SHA256SUMS.txt`；在 Windows PowerShell 中用 `Get-FileHash logos-cli-0.1.0-net8.0.zip -Algorithm SHA256` 对照摘要。

## 从源码构建

安装 .NET 8 SDK，从仓库根目录执行：

```sh
dotnet publish cli/LogosCli.csproj -c Release -o ./Artifacts/logos-cli-bin
dotnet ./Artifacts/logos-cli-bin/logos-cli.dll --help
```

## 查看时间线

默认输出文本，每条记录显示 UTC 时间、等级、安装、会话、实例、访问、UID、序号、记录 ID、事件类型、消息及已有堆栈。JSONL 每行一个对象，另含设备上下文、单调时钟、截断标记及已有诊断元数据。文本中的换行和控制字符会转义。

## 导出密钥

```json
{
  "exportKeys": [
    {
      "id": "export-2026-11",
      "encryptionKeyBase64": "<32 字节 AES 密钥的 Base64>",
      "macKeyBase64": "<32 字节独立 MAC 密钥的 Base64>"
    }
  ]
}
```

使用生成该批次时的导出密钥。轮换期间可同时配置多个不同 ID。配置中同一 JSON 对象出现重复字段时，工具返回退出码 2。文件头里的 ID 仅用于查找候选密钥，外层认证码、内部文件头和块均须通过验证。请限制密钥文件与明文输出的读取权限。

## 时间线与失败处理

- 一次最多读取 512 个批次，每个批次最多 67,424 字节。按安装标识和记录 ID 去重，比较完整记录摘要；冲突时整次失败。全部输入验证通过后才向标准输出写记录。错误发向标准错误。
- 按 UTC 排序；时间相同时以安装、会话、实例、序号、访问 ID 和记录 ID 确定顺序。跨设备时钟偏差可能影响顺序。SDK 在采集时丢弃的记录无法恢复；输入文件无法证明查询时间段内的所有批次都已交付。
- 支持版本 1 和 2。版本 2 会对解密记录重算覆盖统计。离线读取核验交付正文的完整性，当前用户权限、撤权状态和原始查询条件须由交付系统管理。在线接收服务可结合可信任务和当前授权调用 SDK 的接收端校验入口。
- 退出码 0 表示成功；2 表示参数、配置、I/O 或大小限制错误；3 表示找不到文件指定的导出密钥；4 表示文件截断或短于声明长度；5 表示格式或认证失败；6 表示相同记录 ID 的内容冲突。认证失败时请核对密钥并重新取得完整文件。
