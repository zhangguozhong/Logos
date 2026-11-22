# HTTP 上传适配器

可用 `HttpUploadService` 实现上传注入。显式提供 HTTPS 地址、目的地标识和返回缓存令牌的委托，再将服务传给 `LogosUploadOptions.Service`。委托应快速返回，令牌刷新由应用另行完成。

```csharp
var service = new HttpUploadService(
    new Uri("https://your-service.example/v1/batches"),
    destinationId,
    () => cachedBearerToken);
// 将服务传入应用的上传配置。
```

## 接收端约定

参考端点为 `POST /v1/batches`。请求使用 Bearer 认证，正文类型为 `application/vnd.logos.batch-v1`，批次最大 67,424 字节。请求头包含 `X-Logos-Task-Id`、`X-Logos-Batch-Id`。接收端应验证请求并幂等处理重复批次。

成功时返回 HTTP 200 和 `application/vnd.logos.receipt-v1`。回执恰好 80 字节，依次为 8 字节 ASCII 标记 `LOGOSAK1`、32 字节批次标识、32 字节正文 SHA-256 和 8 字节小端序正文长度。

| 返回或异常 | 应用侧处理 |
|---|---|
| 401、403 | 检查令牌、有效期与目的地授权。 |
| 413 | 检查接收端允许的批次大小。 |
| 429 | 接收端可通过 `Retry-After` 指定整数秒数；客户端接受 1～3,600 秒。 |
| 408、5xx 或临时网络异常 | 由 SDK 按额度重试，自定义服务不要重复发送。 |
| 回执不匹配、重定向或其他无效响应 | 检查端点与协议；该响应不能确认上传成功。 |

正常校验证书，禁止重定向。本机联调可显式允许字面地址 `127.0.0.1` 或 `::1` 的 HTTP。请求的取消时限为 15 秒，超时后仍应等待操作实际关闭。服务释放规则见[上传接入](transport.md)。
