using System;
using System.Net;
using System.Net.Http;
using System.Threading;
namespace Logos.Transport.Http
{
    public sealed class HttpUploadService : IUploadService, IDisposable
    {
        internal const int MaximumBodyBytes = 67424;
        private readonly object gate = new object();
        private readonly Uri endpoint;
        private readonly Func<string> cachedBearer;
        private readonly HttpClient client;
        private HttpUploadOperation active;
        private bool stopping, disposed;
        public string DestinationId
        {
            get;
        }
        public HttpUploadService(Uri endpoint, string destinationId, Func<string> cachedBearer, bool allowInsecureLoopbackForTests = false)
        {
            ValidateEndpoint(endpoint, allowInsecureLoopbackForTests);
            ValidateDestination(destinationId);
            this.cachedBearer = cachedBearer ?? throw new ArgumentNullException(nameof(cachedBearer));
            DestinationId = destinationId;
            this.endpoint = endpoint;
            var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.None, UseProxy = false, MaxResponseHeadersLength = 16 };
            client = new HttpClient(handler, true) { Timeout = Timeout.InfiniteTimeSpan };
        }
        private static void ValidateDestination(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 128 || value[0] == ' ' || value[value.Length - 1] == ' ')
                throw new ArgumentException("A bounded destination identifier is required.");
            foreach (char c in value)
                if (c < 32 || c > 126)
                    throw new ArgumentException("Destination must be printable ASCII without edge spaces.");
        }
        // 明文回环地址只供本机测试使用，正常上传端点必须通过 HTTPS 校验。
        private static void ValidateEndpoint(Uri endpoint, bool testOnly)
        {
            if (endpoint == null || !endpoint.IsAbsoluteUri || endpoint.UserInfo.Length != 0 || endpoint.Fragment.Length != 0)
                throw new ArgumentException("An absolute endpoint without credentials or fragment is required.");
            if (endpoint.Scheme != Uri.UriSchemeHttps && endpoint.Scheme != Uri.UriSchemeHttp)
                throw new ArgumentException("HTTPS is required.");
            // Uri 会隐藏空用户信息并归一化数值 IP 别名；因此检查原始 authority。
            string original = endpoint.OriginalString;
            int begin = original.IndexOf("://", StringComparison.Ordinal) + 3, end = original.IndexOfAny(new[] { '/', '?', '#' }, begin);
            if (end < 0)
                end = original.Length;
            string authority = original.Substring(begin, end - begin);
            if (authority.IndexOf('@') >= 0)
                throw new ArgumentException("Endpoint user-info is not allowed.");
            if (endpoint.Scheme == Uri.UriSchemeHttps)
                return;
            if (!testOnly)
                throw new ArgumentException("HTTPS is required.");
            bool literal = authority == "127.0.0.1" || authority.StartsWith("127.0.0.1:", StringComparison.Ordinal) || authority == "[::1]" || authority.StartsWith("[::1]:", StringComparison.Ordinal);
            if (!literal || (endpoint.Host != "127.0.0.1" && endpoint.Host != "[::1]" && endpoint.Host != "::1"))
                throw new ArgumentException("Test HTTP requires literal 127.0.0.1 or ::1.");
        }
        internal HttpUploadService(Uri endpoint, string destinationId, Func<string> cachedBearer, HttpMessageHandler handler)
        {
            ValidateEndpoint(endpoint, true);
            ValidateDestination(destinationId);
            this.cachedBearer = cachedBearer ?? throw new ArgumentNullException(nameof(cachedBearer));
            DestinationId = destinationId;
            this.endpoint = endpoint;
            client = new HttpClient(handler ?? throw new ArgumentNullException(nameof(handler)), true) { Timeout = Timeout.InfiniteTimeSpan };
        }
        /// <summary>预留一次尝试；正文复制和网络工作在其有界后台任务中执行。</summary>
        public bool TryStart(UploadRequest request, out IUploadOperation operation)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));
            operation = null;
            if (request.DestinationId != DestinationId || request.Body.Length > MaximumBodyBytes)
                return false;
            lock (gate)
            {
                if (stopping || disposed)
                    return false;
                if (active != null && !active.TryClose())
                    return false;
                active = new HttpUploadOperation(client, endpoint, cachedBearer, request);
                operation = active;
                active.Start();
                return true;
            }
        }
        /// <summary>服务由调用者拥有。未完成的操作会被取消并保留；实际关闭后重试释放。</summary>
        public void Dispose()
        {
            HttpUploadOperation operation;
            lock (gate)
            {
                if (disposed)
                    return;
                stopping = true;
                operation = active;
            }
            operation?.RequestCancel();
            lock (gate)
            {
                if (active != null && !active.TryClose())
                    throw new InvalidOperationException("HTTP operation resources have not actually closed.");
                if (disposed)
                    return;
                client.Dispose();
                active = null;
                disposed = true;
            }
        }
    }
}
