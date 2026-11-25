# Logos 解码器

面向排障人员的离线桌面工具。从 [GUI 发布页](https://github.com/zhangguozhong/Logos/releases/tag/logos-decoder-v0.1.0) 下载适配系统和芯片的独立发布包，解压后在 Mac 打开「Logos 解码器.app」，在 Windows 打开 LogosDecoder.exe。自包含发布包自带 .NET，无需 Unity。

1. 从上传服务取得完整的回捞批次，拖入窗口上方，或点「添加文件…」多选。整个批次列表都可接收拖入文件。点 × 可移除单个文件，点「清空列表」可重新选择，两者都保留磁盘原件。单次最多处理 512 个批次；设备端 .logos 日文件请先通过 SDK 回捞。
2. 点「导出密钥」右侧的「选择配置…」，选择与项目 TransferKeys 对应的 export-keys.json。GUI 按批次 ID 选择密钥，新旧批次可同时导出。配置文件格式见 [CLI 使用说明](../cli/README.md)。
3. 选「文本 (.txt)」或 JSONL，再点「解密并另存为…」。单个批次沿用原名，多批次默认合并为「日志时间线」。导出后可打开结果或定位到所在目录。

文件在本机校验、去重、排序和解密。缺少密钥时补充相应 ID；认证失败时核对 AES 和 MAC 材料，批次截断时重新下载。取消或失败会清理本次临时输出并保留已有目标。导出文件包含明文，请保存在有相应访问权限的目录。

## 预览与筛选

导出成功后点击「预览时间线」。窗口从已保存的文本或 JSONL 读取首个 Error/Fatal 及其在合并时间线上的前后各最多五条。选中记录可看 UID、事件、消息和完整堆栈。点「返回完整时间线」可定位到所选记录所在页。

点击输入框右侧 ▾ 可从当前导出的候选中选择，也可以手动输入。候选包含最多 128 个 UID、64 个事件名或消息片段，以及前 64 个不同 UTC 日期和末条记录的日期，支持直接采用所选记录的日期。

可组合填写精确 UID、UTC 起止日期（yyyy-MM-dd，含首尾两天全天）和关键词，再点「筛选」。关键词匹配消息、事件和堆栈，忽略大小写。每页最多 100 条。筛选只改变预览，不会修改导出的文件。

## 本地构建

安装 .NET 8 SDK，在 Logos 源码根目录运行：

```sh
dotnet run --project gui
gui/package-macos.sh osx-arm64
gui/package-macos.sh osx-x64
dotnet publish gui -c Release -r win-x64 --self-contained true -o Artifacts/LogosDecoder/win-x64
```

macOS 打包脚本在 Mac 上运行，产出 .app 和 ZIP，使用本地临时签名。本版未进行 Apple Developer ID 签名与公证。
