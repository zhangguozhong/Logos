using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
namespace Logos.Transport.Http
{
    // 每次上传尝试一个有限任务，不为每条采集记录创建任务。
    internal sealed class HttpUploadOperation : IUploadOperation
    {
        private readonly HttpClient client;
        private readonly Uri endpoint;
        private readonly Func<string> bearer;
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        private UploadRequest upload;
        private byte[] requestBytes;
        private TrackedBodyContent bodyContent;
        private HttpRequestMessage requestMessage;
        private HttpResponseMessage responseMessage;
        private Task work;
        private UploadResult result;
        private int canceled, cleanupComplete;
        internal HttpUploadOperation(HttpClient client, Uri endpoint, Func<string> bearer, UploadRequest upload)
        {
            this.client = client;
            this.endpoint = endpoint;
            this.bearer = bearer;
            this.upload = upload;
        }
        internal void Start()
        {
            work = Task.Run(Run);
        }
        public bool TryGetResult(out UploadResult value)
        {
            value = Volatile.Read(ref result);
            return value != null;
        }
        public bool TryClose() => work != null && work.IsCompleted && Volatile.Read(ref cleanupComplete) != 0;
        public void RequestCancel()
        {
            if (Interlocked.Exchange(ref canceled, 1) != 0)
                return;
            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
        private async Task Run()
        {
            // 立即保留已解析响应；输出写入完成只约束关闭。
            try
            {
                var outcome = await Send().ConfigureAwait(false);
                Volatile.Write(ref result, outcome);
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                Volatile.Write(ref result, new UploadResult(ex is IOException || ex is HttpRequestException || ex is OperationCanceledException ? UploadStatus.Transient : UploadStatus.Permanent));
            }
            finally
            {
                bool closed = true;
                // 处理器可能在输出内容写入结束前提前返回响应。
                // 即使发送失败，也保留正文直到序列化任务实际退出。
                if (bodyContent != null)
                    try
                    {
                        await bodyContent.CloseSerializationAdmission().ConfigureAwait(false);
                    }
                    catch (Exception ex) when (!(ex is OutOfMemoryException))
                    {
                    }
                // HttpResponseMessage 拥有其内容流。Dispose 可能阻塞或失败；
                // 无论取消还是发布结果，都不能证明借用正文已关闭。
                try
                {
                    responseMessage?.Dispose();
                    responseMessage = null;
                }
                catch (Exception ex) when (!(ex is OutOfMemoryException))
                {
                    closed = false;
                }
                try
                {
                    requestMessage?.Dispose();
                    requestMessage = null;
                }
                catch (Exception ex) when (!(ex is OutOfMemoryException))
                {
                    closed = false;
                }
                try
                {
                    cancellation.Dispose();
                }
                catch (Exception ex) when (!(ex is OutOfMemoryException))
                {
                    closed = false;
                }
                if (closed)
                {
                    bodyContent = null;
                    requestBytes = null;
                    upload = null;
                    Volatile.Write(ref cleanupComplete, 1);
                }
            }
        }
        private async Task<UploadResult> Send()
        {
            var token = cancellation.Token;
            token.ThrowIfCancellationRequested();
            string credential = bearer();
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(credential))
                return new UploadResult(UploadStatus.Unauthorized);
            if (credential.Length > 4096)
                return new UploadResult(UploadStatus.Permanent);
            foreach (char c in credential)
                if (c < 33 || c > 126)
                    return new UploadResult(UploadStatus.Permanent);
            requestBytes = upload.Body.CopyBytes();
            requestMessage = new HttpRequestMessage(HttpMethod.Post, endpoint);
            bodyContent = new TrackedBodyContent(requestBytes, token);
            requestMessage.Content = bodyContent;
            requestMessage.Content.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.logos.batch-v1");
            requestMessage.Content.Headers.ContentLength = requestBytes.Length;
            requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            requestMessage.Headers.TryAddWithoutValidation("X-Logos-Task-Id", upload.TaskId);
            requestMessage.Headers.TryAddWithoutValidation("X-Logos-Batch-Id", upload.BatchId);
            responseMessage = await client.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!HeadersBounded(responseMessage))
                return new UploadResult(UploadStatus.Permanent);
            var content = responseMessage.Content;
            if (content?.Headers.ContentLength > 4096)
                return new UploadResult(UploadStatus.Permanent);
            // 多读一个字节才能区分恰好 4096 字节的响应和未声明长度的超限响应。
            byte[] response = new byte[4097];
            int length = 0;
            if (content != null)
            {
                var stream = await content.ReadAsStreamAsync().ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                while (length < response.Length)
                {
                    int count = await stream.ReadAsync(response, length, response.Length - length, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (count == 0)
                        break;
                    length += count;
                }
            }
            if (length > 4096)
                return new UploadResult(UploadStatus.Permanent);
            int status = (int)responseMessage.StatusCode;
            if (status == 200)
                return HttpReceipt.Parse(response, length, upload);
            if (status == 401 || status == 403)
                return new UploadResult(UploadStatus.Unauthorized);
            if (status == 413)
                return new UploadResult(UploadStatus.TooLarge);
            if (status == 429)
                return new UploadResult(UploadStatus.Throttled, retryAfterSeconds: RetryAfter(responseMessage));
            return new UploadResult(status == 408 || (status >= 500 && status <= 599) ? UploadStatus.Transient : UploadStatus.Permanent);
        }
        private sealed class TrackedBodyContent : HttpContent
        {
            private readonly byte[] bytes;
            private readonly CancellationToken token;
            private readonly object gate = new object();
            private Task serialization = Task.CompletedTask;
            private bool accepting = true;
            internal TrackedBodyContent(byte[] bytes, CancellationToken token)
            {
                this.bytes = bytes;
                this.token = token;
            }
            internal Task CloseSerializationAdmission()
            {
                lock (gate)
                {
                    accepting = false;
                    return serialization;
                }
            }
            protected override bool TryComputeLength(out long length)
            {
                length = bytes.Length;
                return true;
            }
            protected override Task SerializeToStreamAsync(Stream stream, TransportContext context)
            {
                lock (gate)
                {
                    if (!accepting)
                        throw new InvalidOperationException("Request serialization admission is closed.");
                    if (!serialization.IsCompleted)
                        throw new InvalidOperationException("Concurrent request serialization is not permitted.");
                    serialization = stream.WriteAsync(bytes, 0, bytes.Length, token);
                    return serialization;
                }
            }
        }
        private static bool HeadersBounded(HttpResponseMessage response)
        {
            int used = 32 + (response.ReasonPhrase?.Length ?? 0);
            if (used > 16384)
                return false;
            return AddHeaders(response.Headers, ref used) && (response.Content == null || AddHeaders(response.Content.Headers, ref used));
        }
        private static bool AddHeaders(HttpHeaders headers, ref int used)
        {
            foreach (var header in headers)
            {
                if (header.Key.Length > 16384 - used)
                    return false;
                used += header.Key.Length + 4;
                if (used > 16384)
                    return false;
                foreach (string value in header.Value)
                {
                    if (value.Length > 16384 - used)
                        return false;
                    foreach (char c in value)
                    {
                        used += c <= 127 ? 1 : c <= 2047 ? 2 : 3;
                        if (used > 16384)
                            return false;
                    }
                    used += 2;
                    if (used > 16384)
                        return false;
                }
            }
            return true;
        }
        private static int RetryAfter(HttpResponseMessage response)
        {
            if (!response.Headers.TryGetValues("Retry-After", out var values))
                return 2;
            string text = null;
            foreach (string value in values)
            {
                if (text != null)
                    return 2;
                text = value;
            }
            if (text == null)
                return 2;
            text = text.Trim();
            if (text.Length == 0)
                return 2;
            int seconds = 0;
            foreach (char c in text)
            {
                if (c < '0' || c > '9')
                    return 2;
                seconds = Math.Min(3600, seconds * 10 + c - '0');
            }
            return Math.Max(1, seconds);
        }
    }
}
