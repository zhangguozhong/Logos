# 版本与接入兼容性

## 当前 Git 版本的接入变化

业务日志和异常均需通过 Logos 显式记录。此前依赖 Unity 控制台采集的接入方请迁移相关调用。上传仍使用 `LogosUploadOptions.Service`，只接收路径的上传接口可通过 `FileUploadAdapter` 接入。[日志等级](Documentation~/log-levels.md) · [上传指南](Documentation~/transport.md)

## 0.4.0-preview.1

安装单一 UPM 包 `com.company.logos`，业务程序集引用 `Logos`。已有预览版本迁移时需配套更新客户端与接收端，并使用新版本对应的存储目录。默认目录为 `Application.persistentDataPath/Logos`，日志文件扩展名为 `.logos`。[包接入说明](README.md) · [接收端要求](Documentation~/Http-README.md)
