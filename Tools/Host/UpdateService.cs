using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TraceSoul2.Host
{
    /// <summary>
    /// 正式版更新：只读取配置仓库的 GitHub Release，校验 zip.sha256 后，
    /// 从家目录外置 runner 启动更新器。角色数据与插件从不进入替换目录。
    /// </summary>
    public sealed class UpdateService : IDisposable
    {
        private static readonly Regex RepositoryPattern = new Regex(
            @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.Compiled);
        private static readonly Regex Sha256Pattern = new Regex(
            @"(?i)\b[0-9a-f]{64}\b", RegexOptions.Compiled);

        private readonly TraceHomeLayout home;
        private readonly HttpClient http;
        private readonly object stateGate = new object();
        private ReleaseInfo lastRelease;
        private string lastError = string.Empty;
        private DateTimeOffset? lastCheckedUtc;

        public UpdateService(TraceHomeLayout home)
        {
            this.home = home ?? throw new ArgumentNullException(nameof(home));
            http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("TraceSoul2-Updater/" + TraceHome.HostVersion());
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        }

        public object Status()
        {
            ReleaseInfo release;
            string error;
            DateTimeOffset? checkedAt;
            lock (stateGate)
            {
                release = lastRelease;
                error = lastError;
                checkedAt = lastCheckedUtc;
            }
            return PublicStatus(release, error, checkedAt);
        }

        public object ConfigureRepository(string repository)
        {
            repository = NormalizeRepository(repository, allowEmpty: true);
            TraceHome.RememberUpdateRepository(repository);
            lock (stateGate)
            {
                lastRelease = null;
                lastError = string.Empty;
                lastCheckedUtc = null;
            }
            return Status();
        }

        public async Task<object> CheckAsync(CancellationToken cancellationToken)
        {
            var repository = NormalizeRepository(home.UpdateRepository, allowEmpty: false);
            try
            {
                var release = await ReadLatestReleaseAsync(repository, cancellationToken);
                lock (stateGate)
                {
                    lastRelease = release;
                    lastError = string.Empty;
                    lastCheckedUtc = DateTimeOffset.UtcNow;
                }
                return Status();
            }
            catch (Exception exception)
            {
                lock (stateGate)
                {
                    lastError = exception.Message;
                    lastCheckedUtc = DateTimeOffset.UtcNow;
                }
                throw;
            }
        }

        public async Task<object> BeginInstallAsync(CancellationToken cancellationToken)
        {
            var repository = NormalizeRepository(home.UpdateRepository, allowEmpty: false);
            var release = await ReadLatestReleaseAsync(repository, cancellationToken);
            if (!IsNewer(release.Version, TraceHome.HostVersion()))
                throw new InvalidOperationException("当前已经是最新正式版 v" + TraceHome.HostVersion() + "。");

            var installRoot = Path.GetFullPath(AppContext.BaseDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            ValidateInstalledApplication(installRoot);

            var runId = Guid.NewGuid().ToString("N");
            var downloadRoot = Path.Combine(home.UpdatesDirectory, "downloads", release.Version, runId);
            var extractedRoot = Path.Combine(downloadRoot, "package");
            Directory.CreateDirectory(downloadRoot);
            Directory.CreateDirectory(extractedRoot);
            var zipPath = Path.Combine(downloadRoot, release.ZipName);

            await DownloadFileAsync(release.ZipUrl, zipPath, cancellationToken);
            var shaText = await http.GetStringAsync(release.Sha256Url, cancellationToken);
            var expectedHashMatch = Sha256Pattern.Match(shaText ?? string.Empty);
            if (!expectedHashMatch.Success)
                throw new InvalidOperationException("Release 的 SHA-256 文件格式无效。");
            var actualHash = await ComputeSha256Async(zipPath, cancellationToken);
            if (!string.Equals(actualHash, expectedHashMatch.Value, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("更新包 SHA-256 校验失败，已拒绝安装。");

            ExtractZipSafely(zipPath, extractedRoot);
            ValidateReleasePackage(extractedRoot, release.Version);

            var installParent = Directory.GetParent(installRoot)?.FullName;
            if (string.IsNullOrWhiteSpace(installParent))
                throw new InvalidOperationException("应用目录没有可用的父目录，拒绝热更新。");
            var installName = Path.GetFileName(installRoot);
            var preparedRoot = Path.Combine(
                installParent, "." + installName + ".tracesoul2-update-" + release.Version + "-" + runId);
            Directory.CreateDirectory(preparedRoot);
            CopyDirectory(extractedRoot, preparedRoot);

            var runnerRoot = Path.Combine(home.UpdatesDirectory, "runner", runId);
            Directory.CreateDirectory(runnerRoot);
            foreach (var file in Directory.GetFiles(extractedRoot, "TraceSoul2.Updater*"))
                File.Copy(file, Path.Combine(runnerRoot, Path.GetFileName(file)), overwrite: true);
            var runnerExe = Path.Combine(runnerRoot, "TraceSoul2.Updater.exe");
            var runnerDll = Path.Combine(runnerRoot, "TraceSoul2.Updater.dll");
            if (!File.Exists(runnerExe) && !File.Exists(runnerDll))
                throw new InvalidOperationException("更新包缺少外置更新器。");

            var startInfo = new ProcessStartInfo
            {
                FileName = File.Exists(runnerExe) ? runnerExe : "dotnet",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = runnerRoot
            };
            if (!File.Exists(runnerExe)) startInfo.ArgumentList.Add(runnerDll);
            AddArgument(startInfo, "--pid", Environment.ProcessId.ToString());
            AddArgument(startInfo, "--source", preparedRoot);
            AddArgument(startInfo, "--target", installRoot);
            AddArgument(startInfo, "--home", home.Root);
            AddArgument(startInfo, "--version", release.Version);
            startInfo.Environment[TraceHome.EnvHome] = home.Root;
            startInfo.Environment[TraceHome.EnvPlugins] = home.PluginsDirectory;
            startInfo.Environment[TraceHome.EnvUrls] = home.Urls;
            if (Process.Start(startInfo) == null)
                throw new InvalidOperationException("无法启动外置更新器。");

            return new
            {
                started = true,
                version = release.Version,
                message = "更新包校验完成，宿主即将退出并由外置更新器替换后重启。角色数据和插件不会被覆盖。"
            };
        }

        private object PublicStatus(ReleaseInfo release, string error, DateTimeOffset? checkedAt)
        {
            var current = TraceHome.HostVersion();
            return new
            {
                currentVersion = current,
                runtime = CurrentRuntimeIdentifier(),
                repository = home.UpdateRepository ?? string.Empty,
                configured = !string.IsNullOrWhiteSpace(home.UpdateRepository),
                installable = File.Exists(Path.Combine(AppContext.BaseDirectory, "tracesoul2.install.json")),
                checkedAtUtc = checkedAt?.ToString("O") ?? string.Empty,
                error = error ?? string.Empty,
                latest = release == null ? null : new
                {
                    version = release.Version,
                    release.Tag,
                    release.PageUrl,
                    release.PublishedUtc,
                    updateAvailable = IsNewer(release.Version, current),
                    release.ZipName
                }
            };
        }

        private async Task<ReleaseInfo> ReadLatestReleaseAsync(
            string repository,
            CancellationToken cancellationToken)
        {
            var url = "https://api.github.com/repos/" + repository + "/releases/latest";
            using (var response = await http.GetAsync(url, cancellationToken))
            {
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException(
                        "GitHub Release 查询失败（HTTP " + (int)response.StatusCode +
                        "）。请确认仓库名称正确、仓库可公开读取且已经发布正式 Release。");
                using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken))
                using (var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken))
                {
                    var root = document.RootElement;
                    var tag = StringOf(root, "tag_name");
                    var version = NormalizeVersion(tag);
                    var assets = root.TryGetProperty("assets", out var assetArray) &&
                                 assetArray.ValueKind == JsonValueKind.Array
                        ? assetArray.EnumerateArray().ToList()
                        : new List<JsonElement>();
                    var runtime = CurrentRuntimeIdentifier();
                    var zip = assets.FirstOrDefault(x =>
                    {
                        var name = StringOf(x, "name");
                        return name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                               name.IndexOf(runtime, StringComparison.OrdinalIgnoreCase) >= 0;
                    });
                    if (zip.ValueKind == JsonValueKind.Undefined)
                        throw new InvalidOperationException(
                            "最新 Release 没有 " + runtime + " 更新 ZIP。");
                    var zipName = StringOf(zip, "name");
                    var sha = assets.FirstOrDefault(x =>
                        string.Equals(StringOf(x, "name"), zipName + ".sha256",
                            StringComparison.OrdinalIgnoreCase));
                    if (sha.ValueKind == JsonValueKind.Undefined)
                        throw new InvalidOperationException("最新 Release 缺少同名 .sha256，拒绝更新。");
                    return new ReleaseInfo
                    {
                        Version = version,
                        Tag = tag,
                        PageUrl = StringOf(root, "html_url"),
                        PublishedUtc = StringOf(root, "published_at"),
                        ZipName = zipName,
                        ZipUrl = StringOf(zip, "browser_download_url"),
                        Sha256Url = StringOf(sha, "browser_download_url")
                    };
                }
            }
        }

        private static string NormalizeRepository(string repository, bool allowEmpty)
        {
            repository = (repository ?? string.Empty).Trim();
            if (repository.Length == 0 && allowEmpty) return string.Empty;
            if (!RepositoryPattern.IsMatch(repository))
                throw new InvalidOperationException("更新仓库必须是 owner/repository 格式。");
            return repository;
        }

        private static string NormalizeVersion(string tag)
        {
            var value = (tag ?? string.Empty).Trim();
            if (value.StartsWith("v", StringComparison.OrdinalIgnoreCase)) value = value.Substring(1);
            if (!Version.TryParse(value, out var parsed) || parsed.Major < 0)
                throw new InvalidOperationException("Release 标签不是稳定版本号：" + tag);
            return parsed.ToString(3);
        }

        private static bool IsNewer(string candidate, string current)
        {
            if (!Version.TryParse(NormalizeVersion(candidate), out var next)) return false;
            if (!Version.TryParse(NormalizeVersion(current), out var now)) return false;
            return next > now;
        }

        private async Task DownloadFileAsync(string url, string destination, CancellationToken cancellationToken)
        {
            using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > 2L * 1024 * 1024 * 1024)
                    throw new InvalidOperationException("更新包超过 2 GiB，拒绝下载。");
                using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
                using (var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await source.CopyToAsync(target, cancellationToken);
            }
        }

        private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var sha = SHA256.Create())
            {
                var hash = await sha.ComputeHashAsync(stream, cancellationToken);
                return Convert.ToHexString(hash).ToLowerInvariant();
            }
        }

        private static void ExtractZipSafely(string zipPath, string destinationRoot)
        {
            var root = Path.GetFullPath(destinationRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                       Path.DirectorySeparatorChar;
            using (var archive = ZipFile.OpenRead(zipPath))
            {
                foreach (var entry in archive.Entries)
                {
                    var destination = Path.GetFullPath(Path.Combine(destinationRoot, entry.FullName));
                    if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("更新包包含越界路径，已拒绝解压。");
                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        Directory.CreateDirectory(destination);
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    entry.ExtractToFile(destination, overwrite: false);
                }
            }
        }

        private static void ValidateInstalledApplication(string installRoot)
        {
            var marker = Path.Combine(installRoot, "tracesoul2.install.json");
            if (!File.Exists(marker))
                throw new InvalidOperationException(
                    "当前是开发/非发布目录，没有 tracesoul2.install.json；只能在正式安装包中一键更新。");
        }

        private static void ValidateReleasePackage(string packageRoot, string expectedVersion)
        {
            var hostDll = Path.Combine(packageRoot, "TraceSoul2.Host.dll");
            var marker = Path.Combine(packageRoot, "tracesoul2.install.json");
            if (!File.Exists(hostDll) || !File.Exists(marker))
                throw new InvalidOperationException("更新包缺少 Host 或安装标记。");
            using (var document = JsonDocument.Parse(File.ReadAllText(marker)))
            {
                if (!string.Equals(StringOf(document.RootElement, "product"), "TraceSoul2",
                        StringComparison.Ordinal) ||
                    !string.Equals(NormalizeVersion(StringOf(document.RootElement, "version")), expectedVersion,
                        StringComparison.Ordinal) ||
                    !string.Equals(StringOf(document.RootElement, "runtime"), CurrentRuntimeIdentifier(),
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("更新包产品或版本与 GitHub Release 不一致。");
            }
        }

        private static string CurrentRuntimeIdentifier()
        {
            var architecture = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "x64",
                Architecture.Arm64 => "arm64",
                _ => throw new PlatformNotSupportedException(
                    "更新器暂不支持 " + RuntimeInformation.ProcessArchitecture + " 架构。")
            };
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "win-" + architecture;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "linux-" + architecture;
            throw new PlatformNotSupportedException("更新器目前只支持 Windows 和 Linux。");
        }

        private static void CopyDirectory(string source, string destination)
        {
            foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(source, directory);
                Directory.CreateDirectory(Path.Combine(destination, relative));
            }
            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(source, file);
                var target = Path.Combine(destination, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target, overwrite: false);
            }
        }

        private static void AddArgument(ProcessStartInfo info, string name, string value)
        {
            info.ArgumentList.Add(name);
            info.ArgumentList.Add(value ?? string.Empty);
        }

        private static string StringOf(JsonElement element, string property)
        {
            return element.ValueKind == JsonValueKind.Object &&
                   element.TryGetProperty(property, out var value) &&
                   value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
        }

        public void Dispose()
        {
            http.Dispose();
        }

        private sealed class ReleaseInfo
        {
            public string Version;
            public string Tag;
            public string PageUrl;
            public string PublishedUtc;
            public string ZipName;
            public string ZipUrl;
            public string Sha256Url;
        }
    }
}
