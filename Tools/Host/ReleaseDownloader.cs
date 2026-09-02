using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace TraceSoul2.Host
{
    internal sealed class ReleaseAsset
    {
        public string ApiUrl;
        public string BrowserUrl;
        public long Size;
    }

    internal sealed class ReleaseDownloadProgress
    {
        public string Phase;
        public string Route;
        public string Message;
        public int Attempt;
        public long Bytes;
        public long Total;
    }

    // All partial files live outside App and are keyed by immutable release asset identity.
    // A completed file is NOT trusted until the caller verifies the release SHA-256.
    internal sealed class ReleaseDownloader
    {
        internal const long MaxPackageBytes = 2L * 1024 * 1024 * 1024;
        internal const int MaxAttempts = 6;
        private readonly HttpClient http;
        private readonly Func<int, CancellationToken, Task> delay;
        private readonly TimeSpan idleTimeout;

        public ReleaseDownloader(HttpClient http,
            Func<int, CancellationToken, Task> delay = null, TimeSpan? idleTimeout = null)
        {
            this.http = http;
            this.delay = delay ?? ((attempt, token) => Task.Delay(TimeSpan.FromSeconds(Math.Min(10, attempt * 2)), token));
            this.idleTimeout = idleTimeout ?? TimeSpan.FromSeconds(45);
        }

        public static HttpClient CreateClient(string version)
        {
            var client = new HttpClient(new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(20),
                AutomaticDecompression = DecompressionMethods.None
            })
            {
                // With ResponseHeadersRead this bounds headers, not the whole slow download.
                Timeout = TimeSpan.FromSeconds(60),
                DefaultRequestVersion = HttpVersion.Version11,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("TraceSoul2-Updater/" + version);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return client;
        }

        public async Task DownloadAsync(ReleaseAsset asset, string destination,
            Action<ReleaseDownloadProgress> report, CancellationToken token)
        {
            if (asset.Size <= 0 || asset.Size > MaxPackageBytes)
                throw new InvalidDataException("Release 资产大小无效或超过 2 GiB。");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination)));
            if (File.Exists(destination) && new FileInfo(destination).Length > asset.Size)
                Quarantine(destination);

            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                token.ThrowIfCancellationRequested();
                var offset = File.Exists(destination) ? new FileInfo(destination).Length : 0;
                var useApi = attempt % 3 != 0 || string.IsNullOrWhiteSpace(asset.BrowserUrl);
                var route = useApi ? "GitHub API" : "GitHub Release";
                void Report(string phase, string message, long bytes) => report?.Invoke(new ReleaseDownloadProgress
                {
                    Phase = phase, Route = route, Attempt = attempt,
                    Message = message, Bytes = bytes, Total = asset.Size
                });
                if (offset == asset.Size)
                {
                    Report("downloaded", "已找到完整缓存，接下来重新校验 SHA-256。", offset);
                    return;
                }

                Report("connecting", "正在连接 " + route + "（第 " + attempt + "/" + MaxAttempts + " 次）" +
                    (offset > 0 ? "，从已有文件继续下载…" : "…"), offset);
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, useApi ? asset.ApiUrl : asset.BrowserUrl)
                    {
                        Version = HttpVersion.Version11,
                        VersionPolicy = HttpVersionPolicy.RequestVersionExact
                    };
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
                    request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
                    if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
                    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                    if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                    {
                        Quarantine(destination);
                        throw new HttpRequestException("远端拒绝续传范围，已保留旧缓存，将重新下载。");
                    }
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
                        throw new HttpRequestException("资产接口返回了元数据而非文件，将重试下载通道。");
                    if (response.StatusCode == HttpStatusCode.PartialContent)
                    {
                        var range = response.Content.Headers.ContentRange;
                        if (range == null || range.Unit != "bytes" || range.From != offset ||
                            range.Length != asset.Size || !range.To.HasValue || range.To < offset ||
                            range.To >= asset.Size || (response.Content.Headers.ContentLength.HasValue &&
                            response.Content.Headers.ContentLength != range.To - range.From + 1))
                            throw new InvalidDataException("续传响应 Content-Range 与请求不一致，已拒绝拼接。");
                    }
                    else if (response.StatusCode == HttpStatusCode.OK)
                    {
                        // Range ignored: replace the partial file, never append a full response.
                        if (response.Content.Headers.ContentLength.HasValue &&
                            response.Content.Headers.ContentLength.Value != asset.Size)
                            throw new InvalidDataException("下载响应大小与 Release 资产不一致。");
                        offset = 0;
                    }
                    else throw new InvalidDataException("下载接口返回了意外状态。");

                    using var source = await response.Content.ReadAsStreamAsync(token);
                    using var target = new FileStream(destination, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
                    target.SetLength(offset);
                    target.Position = offset;
                    var buffer = new byte[128 * 1024];
                    Report("downloading", "正在下载（" + route + "）…", offset);
                    while (true)
                    {
                        // Reset the timeout for every read: 20 KB/s is slow, not a failed download.
                        using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
                        idle.CancelAfter(idleTimeout);
                        var count = await source.ReadAsync(buffer.AsMemory(), idle.Token);
                        if (count == 0) break;
                        if (offset + count > asset.Size)
                            throw new InvalidDataException("下载内容超过 Release 声明大小，已拒绝写入。");
                        await target.WriteAsync(buffer.AsMemory(0, count), token);
                        offset += count;
                        Report("downloading", "正在下载（" + route + "）…", offset);
                    }
                    await target.FlushAsync(token);
                    if (offset != asset.Size) throw new IOException("下载连接提前结束。");
                    Report("downloaded", "下载完成，接下来校验 SHA-256。", offset);
                    return;
                }
                catch (Exception ex) when (!token.IsCancellationRequested &&
                    (ex is HttpRequestException || ex is OperationCanceledException ||
                     (ex is IOException && ex is not InvalidDataException && ex is not PathTooLongException)))
                {
                    var retained = File.Exists(destination) ? new FileInfo(destination).Length : 0;
                    if (attempt == MaxAttempts)
                        throw new IOException("更新下载在 " + MaxAttempts + " 次尝试后仍失败。已保留 " + retained +
                            " 字节；再次点击安装可继续下载。请确认 api.github.com 和 release-assets.githubusercontent.com 可访问。", ex);
                    Report("retrying", route + " 连接中断或超时，已保存进度；即将重试（" +
                        attempt + "/" + MaxAttempts + "）。", retained);
                    await delay(attempt, token);
                }
            }
        }

        public static void Quarantine(string path)
        {
            if (File.Exists(path)) File.Move(path, path + ".invalid-" + Guid.NewGuid().ToString("N"));
        }
    }
}
