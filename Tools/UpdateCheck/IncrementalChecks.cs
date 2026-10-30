using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TraceSoul2.Host;

internal static partial class Program
{
    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    private static byte[] ZipBytes(Dictionary<string, byte[]> files)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
            foreach (var file in files)
            {
                using var stream = zip.CreateEntry(file.Key).Open();
                stream.Write(file.Value);
            }
        return buffer.ToArray();
    }
    private static byte[] JsonBytes(object value) => JsonSerializer.SerializeToUtf8Bytes(value);
    private static string RuntimeId => (OperatingSystem.IsWindows() ? "win-" : "linux-") +
        (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64");
    private static Dictionary<string, byte[]> DeltaFiles(Dictionary<string, byte[]> target, string baseVersion = "0.0.1")
    {
        var files = target.Where(f => f.Key != "model.bin").ToDictionary(f => f.Key, f => f.Value);
        files[IncrementalPackage.InventoryName] = JsonBytes(new { schema = 1, product = "TraceSoul2", version = "9.0.0", runtime = RuntimeId,
            files = target.Select(f => new { path = f.Key, size = f.Value.Length, sha256 = Hash(f.Value) }) });
        files["tracesoul2.delta.json"] = JsonBytes(new { schema = 1, product = "TraceSoul2", version = "9.0.0", runtime = RuntimeId, baseVersion });
        return files;
    }
    private static async Task IncrementalChecks(string root)
    {
        var directory = Path.Combine(root, "incremental"); Directory.CreateDirectory(directory);
        var installed = Path.Combine(directory, "App"); Directory.CreateDirectory(installed);
        var model = new byte[32768]; new Random(1234).NextBytes(model);
        File.WriteAllBytes(Path.Combine(installed, "model.bin"), model);
        File.WriteAllText(Path.Combine(installed, "removed.txt"), "obsolete");
        File.WriteAllBytes(Path.Combine(installed, "tracesoul2.install.json"), JsonBytes(new { product = "TraceSoul2", version = "0.0.1", runtime = RuntimeId }));
        var target = new Dictionary<string, byte[]> {
            ["model.bin"] = model,
            ["TraceSoul2.Host.dll"] = Encoding.UTF8.GetBytes("synthetic host"),
            ["TraceSoul2.Updater.dll"] = Encoding.UTF8.GetBytes("never execute test runner"),
            ["tracesoul2.install.json"] = JsonBytes(new { product = "TraceSoul2", version = "9.0.0", runtime = RuntimeId }),
            ["nested/new.txt"] = Encoding.UTF8.GetBytes("new file")
        };
        var files = DeltaFiles(target);
        var zipPath = Path.Combine(directory, "delta.zip");
        File.WriteAllBytes(zipPath, ZipBytes(files));
        var destination = Path.Combine(directory, "success");
        await IncrementalPackage.PrepareAsync(zipPath, installed, destination, "0.0.1", "9.0.0", RuntimeId, default);
        Require(!File.Exists(Path.Combine(destination, "removed.txt")), "Removed files must not survive materialization");
        foreach (var file in target) Require(File.ReadAllBytes(Path.Combine(destination, file.Key)).SequenceEqual(file.Value), "Target differs: " + file.Key);
        Require(File.Exists(Path.Combine(installed, "removed.txt")), "Live install must remain unchanged");
        await Expect<InvalidDataException>(() => IncrementalPackage.PrepareAsync(zipPath, installed, destination, "0.0.1", "9.0.0", RuntimeId, default));
        await Expect<InvalidDataException>(() => IncrementalPackage.PrepareAsync(zipPath, installed, Path.Combine(directory, "wrong-base"), "0.0.2", "9.0.0", RuntimeId, default));
        File.WriteAllBytes(Path.Combine(installed, "model.bin"), new byte[model.Length]);
        await Expect<InvalidDataException>(() => IncrementalPackage.PrepareAsync(zipPath, installed, Path.Combine(directory, "corrupt-base"), "0.0.1", "9.0.0", RuntimeId, default));
        File.WriteAllBytes(Path.Combine(installed, "model.bin"), model);
        foreach (var path in new[] { "../escape", "unknown.txt", "MODEL.BIN" })
        {
            var invalid = new Dictionary<string, byte[]>(files) { [path] = new byte[] { 1 } };
            File.WriteAllBytes(zipPath, ZipBytes(invalid));
            await Expect<InvalidDataException>(() => IncrementalPackage.PrepareAsync(zipPath, installed, Path.Combine(directory, Guid.NewGuid().ToString("N")), "0.0.1", "9.0.0", RuntimeId, default));
        }
        var badContent = new Dictionary<string, byte[]>(files) { ["TraceSoul2.Host.dll"] = new byte[target["TraceSoul2.Host.dll"].Length] };
        File.WriteAllBytes(zipPath, ZipBytes(badContent));
        await Expect<InvalidDataException>(() => IncrementalPackage.PrepareAsync(zipPath, installed, Path.Combine(directory, "corrupt-patch"), "0.0.1", "9.0.0", RuntimeId, default));
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Expect<OperationCanceledException>(() => IncrementalPackage.PrepareAsync(zipPath, installed, Path.Combine(directory, "cancelled"), "0.0.1", "9.0.0", RuntimeId, cancelled.Token));
        }
        foreach (var scenario in new[] { "delta", "corrupt-base", "corrupt-patch", "skip-version" })
            await IncrementalServiceCheck(directory, target, scenario);
        Console.WriteLine("Incremental update passed: reuse/new/delete, base/content/path validation, cancellation, delta HTTP selection and full fallback, live files unchanged on archive failure.");
    }
    private static async Task IncrementalServiceCheck(string root, Dictionary<string, byte[]> target, string scenario)
    {
        var directory = Path.Combine(root, scenario + "-service"); Directory.CreateDirectory(directory);
        var app = Path.Combine(directory, "App"); Directory.CreateDirectory(app);
        File.WriteAllBytes(Path.Combine(app, "tracesoul2.install.json"), JsonBytes(new { product = "TraceSoul2", version = "0.0.1", runtime = RuntimeId }));
        File.WriteAllText(Path.Combine(app, "TraceSoul2.Host.dll"), "live host");
        File.WriteAllBytes(Path.Combine(app, "model.bin"), scenario == "corrupt-base" ? new byte[target["model.bin"].Length] : target["model.bin"]);
        var name = "tracesoul2-" + RuntimeId + "-v9.0.0.zip";
        var deltaName = "tracesoul2-" + RuntimeId + "-v9.0.0-from-v" + (scenario == "skip-version" ? "0.0.2" : "0.0.1") + ".delta.zip";
        var full = ZipBytes(target);
        var patch = scenario == "corrupt-patch" ? Encoding.UTF8.GetBytes("broken ZIP") : ZipBytes(DeltaFiles(target));
        var assets = new Dictionary<int, (string Name, byte[] Bytes)> {
            [1] = (name, full), [2] = (name + ".sha256", Encoding.ASCII.GetBytes(Hash(full) + "  " + name)),
            [3] = (deltaName, patch), [4] = (deltaName + ".sha256", Encoding.ASCII.GetBytes(Hash(patch) + "  " + deltaName))
        };
        var release = new { tag_name = "v9.0.0", assets = assets.Select(a => new { id = a.Key, name = a.Value.Name, size = a.Value.Bytes.Length,
            browser_download_url = "https://github.com/test/repo/releases/download/v9.0.0/" + a.Value.Name }) };
        var requested = new List<int>();
        using var client = new HttpClient(new Handler((request, _) => {
            if (request.RequestUri.AbsolutePath.EndsWith("/latest"))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(JsonBytes(release)) };
            var id = int.Parse(request.RequestUri.Segments.Last()); requested.Add(id);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(assets[id].Bytes) };
        }));
        using var service = new UpdateService(new TraceHomeLayout { Root = directory, UpdatesDirectory = Path.Combine(directory, "updates"),
            PluginsDirectory = Path.Combine(directory, "plugins"), UpdateRepository = "test/repo", Urls = "http://localhost" }, client, app);
        var archived = false; var shutdown = false;
        service.StartInstall(() => shutdown = true, (_, _) => { archived = true; throw new IOException("stop before live handoff"); });
        var finished = false;
        for (var i = 0; i < 1000; i++)
        {
            if (JsonSerializer.SerializeToElement(service.Status()).GetProperty("install").GetProperty("phase").GetString() == "failed") { finished = true; break; }
            await Task.Delay(10);
        }
        Require(finished && archived && !shutdown, scenario + ": must reach validated archive but not restart");
        Require(File.ReadAllText(Path.Combine(app, "TraceSoul2.Host.dll")) == "live host", "Live files changed before archive");
        Require(requested.SequenceEqual(scenario == "delta" ? new[] { 3, 4 } : scenario == "skip-version" ? new[] { 1, 2 } : new[] { 3, 4, 1, 2 }),
            scenario + ": incorrect downloads " + string.Join(",", requested));
    }
}
