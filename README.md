# Logos

Logos 为原生 Android / iOS App 中的 Unity 运行时收集线上问题现场。一个 UPM 包提供 Unity 自动采集、分级准入、加密本地留存和按诊断 UID 回捞。应用可以注入自己的上传服务。

## 安装

在 Unity 2022.3 的 Package Manager 中选择 **Add package from git URL**，输入下方地址，然后在业务程序集引用 `Logos`。当前包版本为 `0.4.0-preview.1`。

```text
https://github.com/zhangguozhong/Logos.git?path=/Packages/com.company.logos
```

## 接入顺序

1. 在 Unity 主线程初始化并保留返回的会话。线上留存模式还需提供密钥，并等待存储就绪。[初始化](Packages/com.company.logos/Documentation~/getting-started.md) · [本地留存](Packages/com.company.logos/Documentation~/local-journal.md)
2. Unity 已输出的日志会自动进入采集回调。在需要重建的业务节点显式记录关键 Info，登录、切号和登出时更新诊断 UID。[分级与丢弃](Packages/com.company.logos/Documentation~/log-levels.md) · [登录、切号与安全卸载](Packages/com.company.logos/Documentation~/Lifecycle.md)
3. 需要回捞时注入上传服务并提供当前授权。宿主卸载前在主线程轮询停止入口，直到会话允许卸载。[上传与回捞](Packages/com.company.logos/Documentation~/transport.md)

## 性能与适用范围

本机 Unity 编辑器的三档五对测试中，当前实现的后台线程 CPU 波动分别为 **5.91%、13.40%、16.74%**。10,000 条/s 仍超过 15% 门槛。Android/iOS 真机帧耗时、持续能耗及宿主卸载仍待验证。[最新压测结果](Docs/benchmarks/unity-mixed-load.md)

[文档导航](Docs/README.md) · [验收标准](Docs/acceptance.md)
