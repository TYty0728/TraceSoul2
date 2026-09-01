using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
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
    /// 从家目录外置 runner 启动更新器。角色数据与插件数据不进入替换目录；
    /// Release 内声明的官方插件代码包随应用一起事务式升级。
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
        private InstallProgress installProgress = InstallProgress.Idle();
        private string lastLoggedInstallPhase = string.Empty;
        private int lastLoggedInstallPercent = -10;

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
            InstallProgress install;
            lock (stateGate)
            {
                release = lastRelease;
                error = lastError;
                checkedAt = lastCheckedUtc;
                install = installProgress.Copy();
            }
            return PublicStatus(release, error, checkedAt, install);
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
            BeginInstallProgress();
            try
            {
                var repository = NormalizeRepository(home.UpdateRepository, allowEmpty: false);
                SetInstallProgress("checking", 2, "正在读取最新正式 Release…");
                var release = await ReadLatestReleaseAsync(repository, cancellationToken);
                if (!IsNewer(release.Version, TraceHome.HostVersion()))
                    throw new InvalidOperationException("当前已经是最新正式版 v" + TraceHome.HostVersion() + "。");
                SetInstallProgress("preparing", 4, "正在检查本机安装目录…", version: release.Version);

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
                SetInstallProgress("verifying", 78, "正在下载并核对 SHA-256 文件…");
                var shaText = await http.GetStringAsync(release.Sha256Url, cancellationToken);
                var expectedHashMatch = Sha256Pattern.Match(shaText ?? string.Empty);
                if (!expectedHashMatch.Success)
                    throw new InvalidOperationException("Release 的 SHA-256 文件格式无效。");
                SetInstallProgress("verifying", 80, "正在计算更新包 SHA-256…");
                var actualHash = await ComputeSha256Async(zipPath, cancellationToken);
                if (!string.Equals(actualHash, expectedHashMatch.Value, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("更新包 SHA-256 校验失败，已拒绝安装。");

                SetInstallProgress("extracting", 84, "校验通过，正在安全解压更新包…");
                ExtractZipSafely(zipPath, extractedRoot);
                SetInstallProgress("validating", 90, "正在验证程序与官方插件清单…");
                ValidateReleasePackage(extractedRoot, release.Version);

                var installParent = Directory.GetParent(installRoot)?.FullName;
                if (string.IsNullOrWhiteSpace(installParent))
                    throw new InvalidOperationException("应用目录没有可用的父目录，拒绝热更新。");
                var installName = Path.GetFileName(installRoot);
                var preparedRoot = Path.Combine(
                    installParent, "." + installName + ".tracesoul2-update-" + release.Version + "-" + runId);
                Directory.CreateDirectory(preparedRoot);
                SetInstallProgress("staging", 93, "正在准备可原子替换的新版本目录…");
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
                AddArgument(startInfo, "--plugins", home.PluginsDirectory);
                AddArgument(startInfo, "--version", release.Version);
                startInfo.Environment[TraceHome.EnvHome] = home.Root;
                startInfo.Environment[TraceHome.EnvPlugins] = home.PluginsDirectory;
                startInfo.Environment[TraceHome.EnvUrls] = home.Urls;
                SetInstallProgress("handoff", 98, "正在启动外置更新器…");
                if (Process.Start(startInfo) == null)
                    throw new InvalidOperationException("无法启动外置更新器。");
                SetInstallProgress("restarting", 100, "更新包准备完成，宿主正在重启…");

                return new
                {
                    started = true,
                    version = release.Version,
                    message = "更新包校验完成，宿主即将退出并由外置更新器替换后重启。角色数据和插件配置不会被覆盖；Release 内置插件会保留备份后同步升级。"
                };
            }
            catch (Exception exception)
            {
                FailInstallProgress(exception.Message);
                throw;
            }
        }

        private object PublicStatus(
            ReleaseInfo release,
            string error,
            DateTimeOffset? checkedAt,
            InstallProgress install)
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
                install = new
                {
                    inProgress = install.InProgress,
                    phase = install.Phase,
                    version = install.Version,
                    percent = install.Percent,
                    message = install.Message,
                    downloadedBytes = install.DownloadedBytes,
                    totalBytes = install.TotalBytes,
                    error = install.Error,
                    updatedAtUtc = install.UpdatedAtUtc
                },
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
                {
                    if (response.StatusCode == HttpStatusCode.NotFound &&
                        await RepositoryIsPubliclyReadableAsync(repository, cancellationToken))
                        throw new InvalidOperationException(
                            "仓库 " + repository + " 可以公开读取，但还没有正式 GitHub Release。" +
                            "普通 main 提交不会成为更新；请先创建与产品版本一致的 v* 标签并等待 Release 构建完成。");
                    throw new InvalidOperationException(
                        "GitHub Release 查询失败（HTTP " + (int)response.StatusCode +
                        "）。请确认仓库名称正确且仓库可公开读取；HTTP 404 也可能表示仓库还没有正式 Release。");
                }
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

        private async Task<bool> RepositoryIsPubliclyReadableAsync(
            string repository,
            CancellationToken cancellationToken)
        {
            var url = "https://api.github.com/repos/" + repository;
            using (var response = await http.GetAsync(url, cancellationToken))
                return response.IsSuccessStatusCode;
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
                var totalBytes = response.Content.Headers.ContentLength ?? 0;
                if (totalBytes > 2L * 1024 * 1024 * 1024)
                    throw new InvalidOperationException("更新包超过 2 GiB，拒绝下载。");
                using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
                using (var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[128 * 1024];
                    long downloadedBytes = 0;
                    SetInstallProgress("downloading", 5, "正在下载更新包…",
                        downloadedBytes: 0, totalBytes: totalBytes);
                    while (true)
                    {
                        var read = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                        if (read <= 0) break;
                        await target.WriteAsync(buffer, 0, read, cancellationToken);
                        downloadedBytes += read;
                        var percent = totalBytes > 0
                            ? 5 + (int)Math.Min(70, downloadedBytes * 70 / totalBytes)
                            : 5;
                        var sizeText = FormatBytes(downloadedBytes) +
                                       (totalBytes > 0 ? " / " + FormatBytes(totalBytes) : string.Empty);
                        SetInstallProgress("downloading", percent, "正在下载更新包：" + sizeText,
                            downloadedBytes: downloadedBytes, totalBytes: totalBytes);
                    }
                    SetInstallProgress("downloading", 75,
                        "更新包下载完成：" + FormatBytes(downloadedBytes),
                        downloadedBytes: downloadedBytes, totalBytes: totalBytes);
                }
            }
        }

        private void BeginInstallProgress()
        {
            lock (stateGate)
            {
                if (installProgress.InProgress)
                    throw new InvalidOperationException("已有更新正在安装，请不要重复提交。");
                installProgress = new InstallProgress
                {
                    InProgress = true,
                    Phase = "checking",
                    Percent = 1,
                    Message = "正在启动更新检查…",
                    UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O")
                };
                lastLoggedInstallPhase = string.Empty;
                lastLoggedInstallPercent = -10;
            }
            AppendInstallLog("[1%] 正在启动更新检查…");
        }

        private void SetInstallProgress(
            string phase,
            int percent,
            string message,
            string version = null,
            bool inProgress = true,
            long downloadedBytes = -1,
            long totalBytes = -1,
            string error = "")
        {
            var shouldLog = false;
            lock (stateGate)
            {
                installProgress.InProgress = inProgress;
                installProgress.Phase = phase ?? string.Empty;
                installProgress.Percent = Math.Max(0, Math.Min(100, percent));
                installProgress.Message = message ?? string.Empty;
                installProgress.Error = error ?? string.Empty;
                installProgress.UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O");
                if (version != null) installProgress.Version = version;
                if (downloadedBytes >= 0) installProgress.DownloadedBytes = downloadedBytes;
                if (totalBytes >= 0) installProgress.TotalBytes = totalBytes;
                shouldLog = !string.Equals(lastLoggedInstallPhase, installProgress.Phase,
                                StringComparison.Ordinal) ||
                            installProgress.Percent >= lastLoggedInstallPercent + 10 ||
                            installProgress.Percent == 100 || installProgress.Phase == "failed";
                if (shouldLog)
                {
                    lastLoggedInstallPhase = installProgress.Phase;
                    lastLoggedInstallPercent = installProgress.Percent;
                }
            }
            if (shouldLog) AppendInstallLog("[" + Math.Max(0, Math.Min(100, percent)) + "%] " + message);
        }

        private void FailInstallProgress(string error)
        {
            int percent;
            lock (stateGate) percent = installProgress.Percent;
            SetInstallProgress("failed", percent, "安装失败：" + error,
                inProgress: false, error: error);
        }

        private void AppendInstallLog(string message)
        {
            try
            {
                Directory.CreateDirectory(home.UpdatesDirectory);
                File.AppendAllText(Path.Combine(home.UpdatesDirectory, "update.log"),
                    DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz") + " " + message + Environment.NewLine);
            }
            catch
            {
                // 日志不可写不能阻断更新；进度仍可通过状态接口读取。
            }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024L * 1024) return (bytes / 1024d).ToString("0.0") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / 1024d / 1024d).ToString("0.0") + " MB";
            return (bytes / 1024d / 1024d / 1024d).ToString("0.00") + " GB";
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
                var manifest = document.RootElement;
                if (!string.Equals(StringOf(manifest, "product"), "TraceSoul2",
                        StringComparison.Ordinal) ||
                    !string.Equals(NormalizeVersion(StringOf(manifest, "version")), expectedVersion,
                        StringComparison.Ordinal) ||
                    !string.Equals(StringOf(manifest, "runtime"), CurrentRuntimeIdentifier(),
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("更新包产品或版本与 GitHub Release 不一致。");
                ValidateBundledPlugins(packageRoot, manifest);
            }
        }

        private static void ValidateBundledPlugins(string packageRoot, JsonElement manifest)
        {
            if (!manifest.TryGetProperty("bundledPlugins", out var names) ||
                names.ValueKind == JsonValueKind.Null) return;
            if (names.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("更新包 bundledPlugins 清单格式无效。");
            var root = Path.Combine(packageRoot, "BundledPlugins");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in names.EnumerateArray())
            {
                var name = item.ValueKind == JsonValueKind.String ? item.GetString() ?? string.Empty : string.Empty;
                if (!IsSafePackageName(name) || !seen.Add(name))
                    throw new InvalidOperationException("更新包包含无效或重复的官方插件名：" + name);
                var folder = Path.Combine(root, name);
                var pluginManifest = Path.Combine(folder, "plugin.json");
                if (!Directory.Exists(folder) || !File.Exists(pluginManifest))
                    throw new InvalidOperationException("更新包缺少官方插件目录或清单：" + name);
                using (var pluginDocument = JsonDocument.Parse(File.ReadAllText(pluginManifest)))
                {
                    var dll = StringOf(pluginDocument.RootElement, "dll");
                    if (string.IsNullOrWhiteSpace(dll) ||
                        !string.Equals(Path.GetFileName(dll), dll, StringComparison.Ordinal) ||
                        !File.Exists(Path.Combine(folder, dll)))
                        throw new InvalidOperationException("官方插件包缺少清单指定的 DLL：" + name);
                }
            }
        }

        private static bool IsSafePackageName(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 80 || value.StartsWith(".", StringComparison.Ordinal))
                return false;
            return string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal) &&
                   value.All(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == '.');
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

        private sealed class InstallProgress
        {
            public bool InProgress;
            public string Phase = "idle";
            public string Version = string.Empty;
            public int Percent;
            public string Message = string.Empty;
            public long DownloadedBytes;
            public long TotalBytes;
            public string Error = string.Empty;
            public string UpdatedAtUtc = string.Empty;

            public static InstallProgress Idle() => new InstallProgress();

            public InstallProgress Copy() => new InstallProgress
            {
                InProgress = InProgress,
                Phase = Phase,
                Version = Version,
                Percent = Percent,
                Message = Message,
                DownloadedBytes = DownloadedBytes,
                TotalBytes = TotalBytes,
                Error = Error,
                UpdatedAtUtc = UpdatedAtUtc
            };
        }
    }
}
