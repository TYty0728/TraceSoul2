using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TraceSoul2.Host;

internal static class Program
{
    private static readonly byte[] Payload = Encoding.ASCII.GetBytes("abcdefgh");
    private static ReleaseAsset Asset => new ReleaseAsset
    {
        ApiUrl = "https://api.github.com/repos/test/repo/releases/assets/1",
        BrowserUrl = "https://github.com/test/repo/releases/download/v1.0.0/test.zip", Size = Payload.Length
    };
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
    private static HttpResponseMessage Response(int offset = 0, bool partial = false)
    {
        var response = new HttpResponseMessage(partial ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
        { Content = new ByteArrayContent(Payload.Skip(offset).ToArray()) };
        if (partial) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, Payload.Length - 1, Payload.Length);
        return response;
    }
    private static ReleaseDownloader Downloader(HttpClient http) => new ReleaseDownloader(http, (_, _) => Task.CompletedTask,
        TimeSpan.FromMilliseconds(30));

    private static async Task Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--live") { await LiveCheck(); return; }
        var root = Path.Combine(Path.GetTempPath(), "tracesoul2-download-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await ServiceCheck(root);
            var file = Path.Combine(root, "api.zip");
            using (var http = new HttpClient(new Handler((request, _) =>
            {
                Require(request.RequestUri.Host == "api.github.com", "API must be the primary route");
                Require(request.Version == HttpVersion.Version11 && request.VersionPolicy == HttpVersionPolicy.RequestVersionExact, "Must force HTTP/1.1");
                Require(request.Headers.Accept.Single().MediaType == "application/octet-stream", "Must request binary content");
                return Response();
            })))
            {
                await Downloader(http).DownloadAsync(Asset, file, null, default);
                Require(File.ReadAllBytes(file).SequenceEqual(Payload), "Full download mismatch");
            }
            using (var http = new HttpClient(new Handler((_, _) => throw new Exception("Complete cache should avoid network"))))
                await Downloader(http).DownloadAsync(Asset, file, null, default);

            file = Path.Combine(root, "resume.zip");
            using (var http = new HttpClient(new Handler((request, call) =>
            {
                if (call == 1)
                {
                    var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BrokenStream()) };
                    response.Content.Headers.ContentLength = Payload.Length;
                    return response;
                }
                Require(request.Headers.Range.Ranges.Single().From == 4, "Interrupted download must resume at 4");
                return Response(4, true);
            }))) await Downloader(http).DownloadAsync(Asset, file, null, default);
            Require(File.ReadAllBytes(file).SequenceEqual(Payload), "Resume corrupted bytes");

            file = Path.Combine(root, "ignored-range.zip");
            File.WriteAllBytes(file, Payload.Take(4).ToArray());
            using (var http = new HttpClient(new Handler((request, _) =>
            {
                Require(request.Headers.Range.Ranges.Single().From == 4, "Missing range");
                return Response();
            }))) await Downloader(http).DownloadAsync(Asset, file, null, default);
            Require(File.ReadAllBytes(file).SequenceEqual(Payload), "200 response must replace, not append");

            file = Path.Combine(root, "fallback.zip");
            using (var http = new HttpClient(new Handler((request, call) =>
            {
                if (call <= 2) throw new HttpRequestException("offline");
                Require(request.RequestUri.Host == "github.com", "Third attempt must use official fallback");
                return Response();
            }))) await Downloader(http).DownloadAsync(Asset, file, null, default);

            file = Path.Combine(root, "persistent.zip");
            File.WriteAllBytes(file, Payload.Take(4).ToArray());
            var attempts = 0;
            using (var http = new HttpClient(new Handler((_, _) => { attempts++; throw new HttpRequestException("offline"); })))
                await Expect<IOException>(() => Downloader(http).DownloadAsync(Asset, file, null, default));
            Require(attempts == ReleaseDownloader.MaxAttempts && new FileInfo(file).Length == 4, "Exhaustion must preserve partial bytes");
            using (var http = new HttpClient(new Handler((request, _) =>
            {
                Require(request.Headers.Range.Ranges.Single().From == 4, "New install must reuse old partial");
                return Response(4, true);
            }))) await Downloader(http).DownloadAsync(Asset, file, null, default);

            file = Path.Combine(root, "bad-range.zip");
            File.WriteAllBytes(file, Payload.Take(4).ToArray());
            using (var http = new HttpClient(new Handler((_, _) => Response(3, true))))
                await Expect<InvalidDataException>(() => Downloader(http).DownloadAsync(Asset, file, null, default));
            Require(new FileInfo(file).Length == 4, "Bad range must not change partial file");

            file = Path.Combine(root, "range416.zip");
            File.WriteAllBytes(file, Payload.Take(4).ToArray());
            using (var http = new HttpClient(new Handler((request, call) =>
            {
                if (call == 1) return new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable);
                Require(request.Headers.Range == null, "416 must restart a new file");
                return Response();
            }))) await Downloader(http).DownloadAsync(Asset, file, null, default);
            Require(Directory.GetFiles(root, "range416.zip.invalid-*").Length == 1, "Old cache must be recoverable");

            file = Path.Combine(root, "wrong-size.zip");
            using (var http = new HttpClient(new Handler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent(new byte[9]) })))
                await Expect<InvalidDataException>(() => Downloader(http).DownloadAsync(Asset, file, null, default));

            var cancelledCalls = 0;
            using (var http = new HttpClient(new Handler((_, _) => { cancelledCalls++; return Response(); })))
            {
                using var cts = new CancellationTokenSource(); cts.Cancel();
                await Expect<OperationCanceledException>(() => Downloader(http).DownloadAsync(Asset, Path.Combine(root, "cancel.zip"), null, cts.Token));
            }
            Require(cancelledCalls == 0, "Cancellation should not retry");
            file = Path.Combine(root, "idle.zip");
            using (var http = new HttpClient(new Handler((_, call) =>
            {
                if (call != 1) return Response();
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new IdleStream()) };
                response.Content.Headers.ContentLength = Payload.Length;
                return response;
            }))) await Downloader(http).DownloadAsync(Asset, file, null, default);
            Require(File.ReadAllBytes(file).SequenceEqual(Payload), "Idle timeout did not recover");
            Console.WriteLine("UpdateCheck passed: API + HTTP/1.1, cached/restarted resume, interrupted stream, ignored/bad Range, 416, fallback, bounds, cancellation, idle timeout.");
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task Expect<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private static async Task ServiceCheck(string root)
    {
        var gate = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = new UpdateService(new TraceHomeLayout
        {
            Root = root, UpdatesDirectory = Path.Combine(root, "updates"),
            PluginsDirectory = Path.Combine(root, "plugins"), UpdateRepository = "test/repo", Urls = "http://localhost"
        }, new HttpClient(new AsyncHandler(_ => gate.Task)));
        var ready = false;
        var submitted = JsonSerializer.SerializeToElement(service.StartInstall(() => ready = true));
        Require(submitted.GetProperty("started").GetBoolean() && !gate.Task.IsCompleted, "POST must return before remote download completes");
        var active = JsonSerializer.SerializeToElement(service.Status());
        Require(active.GetProperty("install").GetProperty("inProgress").GetBoolean(), "Background job must be visible");
        await Expect<InvalidOperationException>(() => { service.StartInstall(() => {}); return Task.CompletedTask; });
        gate.SetResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        for (var i = 0; i < 100; i++)
        {
            var state = JsonSerializer.SerializeToElement(service.Status()).GetProperty("install");
            if (state.GetProperty("phase").GetString() == "failed")
            {
                Require(!state.GetProperty("inProgress").GetBoolean() && !ready, "Failure must unlock retry and never signal shutdown");
                Console.WriteLine("UpdateService background job / status / duplicate-install guard passed.");
                return;
            }
            await Task.Delay(10);
        }
        throw new Exception("Background failure was not reported");
    }

    private static async Task LiveCheck()
    {
        using var http = ReleaseDownloader.CreateClient("test");
        using var document = JsonDocument.Parse(await http.GetStringAsync("https://api.github.com/repos/TYty0728/TraceSoul2/releases/latest"));
        var asset = document.RootElement.GetProperty("assets").EnumerateArray().First(x => x.GetProperty("name").GetString().EndsWith(".sha256"));
        var path = Path.Combine(Path.GetTempPath(), "tracesoul2-live-" + Guid.NewGuid().ToString("N") + ".sha256");
        try
        {
            await new ReleaseDownloader(http).DownloadAsync(new ReleaseAsset
            {
                ApiUrl = asset.GetProperty("url").GetString(), BrowserUrl = asset.GetProperty("browser_download_url").GetString(),
                Size = asset.GetProperty("size").GetInt64()
            }, path, p => { if (p.Phase != "downloading") Console.WriteLine(p.Message); }, default);
            Require(System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(path), @"^[a-fA-F0-9]{64}\s"), "Live SHA file is not binary asset content");
            Console.WriteLine("Live official API asset download passed (including GitHub redirect).");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, int, HttpResponseMessage> action;
        private int calls;
        public Handler(Func<HttpRequestMessage, int, HttpResponseMessage> action) { this.action = action; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(action(request, ++calls));
    }
    private sealed class AsyncHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> action;
        public AsyncHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> action) { this.action = action; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => action(request);
    }
    private class BrokenStream : MemoryStream
    {
        public BrokenStream() : base(Payload) { }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position >= 4) throw new IOException("simulated dropped connection");
            return base.ReadAsync(buffer.Slice(0, 4), cancellationToken);
        }
    }
    private sealed class IdleStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken); return 0;
        }
    }
}
