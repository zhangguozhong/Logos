# Logos

Logos 为原生 Android / iOS App 中的 Unity 运行时收集线上问题现场。一个 UPM 包提供 显式业务日志采集、分级准入、加密本地留存和按诊断 UID 回捞。应用可以注入自己的上传服务。

## 安装

在 Unity 2022.3 的 Package Manager 中选择 **Add package from git URL**，输入下方地址，然后在业务程序集引用 `Logos`。当前包版本为 `0.4.0-preview.1`。

```text
https://github.com/zhangguozhong/Logos.git?path=/Packages/com.company.logos
```

## 接入顺序

1. 在 Unity 主线程初始化并保留返回的会话。线上留存模式还需提供密钥，并等待存储就绪。[初始化](Packages/com.company.logos/Documentation~/getting-started.md) · [本地留存](Packages/com.company.logos/Documentation~/local-journal.md)
2. 使用 `UnityLogger` 记录业务日志，Unity 自带的 `Debug.Log` 系列不会落盘。在关键业务节点使用 `KeyInfo`，登录、切号和登出时更新诊断 UID。[分级与丢弃](Packages/com.company.logos/Documentation~/log-levels.md) · [登录、切号与安全卸载](Packages/com.company.logos/Documentation~/Lifecycle.md)
3. 需要回捞时注入上传服务并提供当前授权。宿主卸载前在主线程轮询停止入口，直到会话允许卸载。[上传与回捞](Packages/com.company.logos/Documentation~/transport.md)

## 性能与适用范围

本机 Unity 编辑器的三档五对测试中，默认实现的后台线程 CPU 波动在 100 条/s 时超过 15% 门槛，1,000 和 10,000 条/s 两档在本轮达标。Android/iOS 真机帧耗时、持续能耗及宿主卸载仍待验证。[最新压测结果](Docs/benchmarks/unity-mixed-load.md)

[文档导航](Docs/README.md) · [验收标准](Docs/acceptance.md)
