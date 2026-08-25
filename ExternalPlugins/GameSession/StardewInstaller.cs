using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace TraceSoul2.ExternalPlugins.GameSession
{
    internal sealed class StardewInstallStatus
    {
        public string game_path { get; set; }
        public bool game_found { get; set; }
        public bool smapi_installed { get; set; }
        public string smapi_path { get; set; }
        public string mods_path { get; set; }
        public string mcp_root { get; set; }
        public bool mcp_downloaded { get; set; }
        public bool mcp_server_built { get; set; }
        public string mcp_server_entry { get; set; }
        public bool mod_installed { get; set; }
        public string mod_path { get; set; }
        public string bridge_path { get; set; }
        public string action_dir { get; set; }
        public string steam_launch_option { get; set; }
        public bool node_available { get; set; }
        public bool npm_available { get; set; }
        public bool git_available { get; set; }
        public bool dotnet_available { get; set; }
        public bool all_installed { get; set; }
        public bool installing { get; set; }
        public int progress { get; set; }
        public string stage { get; set; }
        public string message { get; set; }
        public string error { get; set; }
        public string[] log { get; set; }
        public bool single_companion_patch { get; set; }
        public bool native_appearance_patch { get; set; }
        public bool tracesoul_patch_installed { get; set; }
        public bool custom_sprite { get; set; }
        public bool custom_portrait { get; set; }
    }

    /// <summary>Installs the user-approved Stardew MCP stack without touching saves or Steam metadata.</summary>
    internal sealed class StardewInstaller : IDisposable
    {
        private const string McpRepository = "https://github.com/amarisaster/StardewValley-MCP.git";
        private const string SmapiLatestApi = "https://api.github.com/repos/Pathoschild/SMAPI/releases/latest";
        private readonly string dataDirectory;
        private readonly string mcpRoot;
        private readonly object gate = new object();
        private readonly CancellationTokenSource shutdown = new CancellationTokenSource();
        private readonly List<string> installLog = new List<string>();
        private bool installing;
        private int progress;
        private string stage = "idle";
        private string message = "尚未安装";
        private string error = string.Empty;
        private string selectedGamePath = string.Empty;

        public StardewInstaller(string dataDirectory)
        {
            this.dataDirectory = Path.GetFullPath(dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory)));
            mcpRoot = Path.Combine(this.dataDirectory, "stardew", "StardewValley-MCP");
        }

        public StardewInstallStatus GetStatus(string requestedGamePath = null)
        {
            var gamePath = ResolveGamePath(requestedGamePath);
            var gameFound = IsGamePath(gamePath);
            var modsPath = gameFound ? Path.Combine(gamePath, "Mods") : string.Empty;
            var modPath = gameFound ? FindBridgeMod(modsPath) : string.Empty;
            var smapiPath = gameFound ? Path.Combine(gamePath, "StardewModdingAPI.exe") : string.Empty;
            var serverEntry = Path.Combine(mcpRoot, "mcp-server", "build", "index.js");
            var customRoot = Path.Combine(dataDirectory, "stardew", "custom");
            bool busy;
            int currentProgress;
            string currentStage;
            string currentMessage;
            string currentError;
            string[] currentLog;
            lock (gate)
            {
                busy = installing;
                currentProgress = progress;
                currentStage = stage;
                currentMessage = message;
                currentError = error;
                currentLog = installLog.ToArray();
            }

            var result = new StardewInstallStatus
            {
                game_path = gamePath,
                game_found = gameFound,
                smapi_installed = File.Exists(smapiPath),
                smapi_path = smapiPath,
                mods_path = modsPath,
                mcp_root = mcpRoot,
                mcp_downloaded = Directory.Exists(Path.Combine(mcpRoot, ".git")),
                mcp_server_built = File.Exists(serverEntry),
                mcp_server_entry = serverEntry,
                mod_installed = !string.IsNullOrWhiteSpace(modPath),
                mod_path = modPath,
                bridge_path = string.IsNullOrWhiteSpace(modPath) ? string.Empty : Path.Combine(modPath, "bridge_data.json"),
                action_dir = string.IsNullOrWhiteSpace(modPath) ? string.Empty : Path.Combine(modPath, "actions"),
                steam_launch_option = File.Exists(smapiPath) ? "\"" + smapiPath + "\" %command%" : string.Empty,
                node_available = CommandExists("node.exe"),
                npm_available = CommandExists("npm.cmd"),
                git_available = CommandExists("git.exe"),
                dotnet_available = CommandExists("dotnet.exe"),
                installing = busy,
                progress = currentProgress,
                stage = currentStage,
                message = currentMessage,
                error = currentError,
                log = currentLog,
                single_companion_patch = IsSingleCompanionPatched(),
                native_appearance_patch = IsNativeAppearancePatched(),
                tracesoul_patch_installed = !string.IsNullOrWhiteSpace(modPath) &&
                    File.Exists(Path.Combine(modPath, "tracesoul-patch-v2.json")),
                custom_sprite = Directory.Exists(customRoot) &&
                    Directory.EnumerateFiles(customRoot, "Companion*_sprite.png").Any(),
                custom_portrait = Directory.Exists(customRoot) &&
                    Directory.EnumerateFiles(customRoot, "Companion*_portrait.png").Any()
            };
            result.all_installed = result.game_found && result.smapi_installed &&
                                   result.mcp_downloaded && result.mcp_server_built && result.mod_installed &&
                                   result.single_companion_patch && result.native_appearance_patch &&
                                   result.tracesoul_patch_installed;
            return result;
        }

        public StardewInstallStatus BeginInstall(string requestedGamePath)
        {
            var gamePath = ResolveGamePath(requestedGamePath);
            if (!IsGamePath(gamePath))
                throw new InvalidOperationException("没有找到 Stardew Valley.exe，请确认 Steam 游戏目录。");
            if (IsGameRunning())
                throw new InvalidOperationException("星露谷或 SMAPI 正在运行。请先退出游戏，再开始安装。");

            lock (gate)
            {
                if (installing) return GetStatus(gamePath);
                installing = true;
                selectedGamePath = gamePath;
                progress = 1;
                stage = "preflight";
                message = "正在检查安装环境";
                error = string.Empty;
                installLog.Clear();
                AddLogUnsafe("已确认游戏目录：" + gamePath);
            }
            _ = Task.Run(() => InstallAsync(gamePath, shutdown.Token));
            return GetStatus(gamePath);
        }

        public object Launch(string requestedGamePath)
        {
            var status = GetStatus(requestedGamePath);
            if (!status.smapi_installed)
                throw new InvalidOperationException("SMAPI 还没有安装完成。");
            if (IsGameRunning())
                return new { launched = false, already_running = true, path = status.smapi_path };
            Process.Start(new ProcessStartInfo
            {
                FileName = status.smapi_path,
                WorkingDirectory = status.game_path,
                UseShellExecute = true
            });
            return new { launched = true, already_running = false, path = status.smapi_path };
        }

        public object CustomizeAppearance(string requestedGamePath, string companion,
            string spriteBase64, string portraitBase64)
        {
            var gamePath = ResolveGamePath(requestedGamePath);
            if (!IsGamePath(gamePath))
                throw new InvalidOperationException("没有找到星露谷游戏目录。");
            companion = string.Equals(companion, "Companion2", StringComparison.Ordinal)
                ? "Companion2" : "Companion1";
            if (string.IsNullOrWhiteSpace(spriteBase64) && string.IsNullOrWhiteSpace(portraitBase64))
                throw new InvalidOperationException("请至少选择一张行走精灵或头像 PNG。");
            var customRoot = Path.Combine(dataDirectory, "stardew", "custom");
            Directory.CreateDirectory(customRoot);
            var changed = new List<string>();
            if (!string.IsNullOrWhiteSpace(spriteBase64))
            {
                var bytes = DecodePng(spriteBase64, 6 * 1024 * 1024, "行走精灵");
                var size = ReadPngSize(bytes);
                if (size.Item1 != 64 || size.Item2 != 128)
                    throw new InvalidOperationException("行走精灵必须是 64×128 PNG（4×4、每格 16×32）。");
                WriteAsset(customRoot, gamePath, companion + "_sprite.png", bytes);
                changed.Add("sprite");
            }
            if (!string.IsNullOrWhiteSpace(portraitBase64))
            {
                var bytes = DecodePng(portraitBase64, 6 * 1024 * 1024, "头像");
                var size = ReadPngSize(bytes);
                if (size.Item1 < 16 || size.Item2 < 16 || size.Item1 > 4096 || size.Item2 > 4096)
                    throw new InvalidOperationException("头像 PNG 尺寸必须在 16×16 到 4096×4096 之间。");
                WriteAsset(customRoot, gamePath, companion + "_portrait.png", bytes);
                changed.Add("portrait");
            }
            AddLog("已保存 " + companion + " 自定义外观：" + string.Join(", ", changed));
            return new
            {
                saved = true,
                companion,
                changed,
                restart_required = IsGameRunning(),
                message = IsGameRunning()
                    ? "外观已保存；当前游戏仍使用已加载贴图，退出并重新启动后生效。"
                    : "外观已保存，下次启动游戏时生效。"
            };
        }

        private async Task InstallAsync(string gamePath, CancellationToken token)
        {
            var stagingRoot = Path.Combine(dataDirectory, "install-staging", Guid.NewGuid().ToString("N"));
            try
            {
                RequireCommand("node.exe", "Node.js 18+");
                RequireCommand("npm.cmd", "npm");
                RequireCommand("git.exe", "Git");
                RequireCommand("dotnet.exe", ".NET SDK");
                Directory.CreateDirectory(stagingRoot);

                if (!File.Exists(Path.Combine(gamePath, "StardewModdingAPI.exe")))
                    await InstallSmapiAsync(gamePath, stagingRoot, token);
                else
                {
                    Update(28, "smapi", "已检测到 SMAPI，跳过重复安装");
                    AddLog("SMAPI 已存在：" + Path.Combine(gamePath, "StardewModdingAPI.exe"));
                }

                await InstallMcpSourceAsync(token);
                await BuildMcpServerAsync(token);
                await BuildSmapiModAsync(gamePath, token);
                WriteConnectionConfig(gamePath);

                var status = GetStatus(gamePath);
                if (!status.all_installed)
                    throw new InvalidOperationException("构建已结束，但安装检查没有全部通过。请查看安装日志。");
                Update(100, "complete", "安装完成，可以用 SMAPI 启动游戏了");
                AddLog("全部完成。加载存档后，桥接数据才会开始出现。");
            }
            catch (OperationCanceledException)
            {
                Fail("安装已取消。");
            }
            catch (Exception exception)
            {
                Fail(exception.Message);
                AddLog(exception.ToString());
            }
            finally
            {
                TryDeleteStaging(stagingRoot);
                lock (gate) installing = false;
            }
        }

        private async Task InstallSmapiAsync(string gamePath, string stagingRoot, CancellationToken token)
        {
            Update(5, "smapi-release", "正在查询 SMAPI 官方最新版");
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(40) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("TraceSoul2-Stardew-Installer/0.1");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            var json = await client.GetStringAsync(SmapiLatestApi, token);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? "unknown";
            JsonElement selected = default;
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? string.Empty;
                if (name.StartsWith("SMAPI-", StringComparison.OrdinalIgnoreCase) &&
                    name.EndsWith("-installer.zip", StringComparison.OrdinalIgnoreCase) &&
                    name.IndexOf("double", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    selected = asset;
                    break;
                }
            }
            if (selected.ValueKind == JsonValueKind.Undefined)
                throw new InvalidOperationException("SMAPI 官方 Release 中没有找到 Windows 安装包。");
            var assetName = selected.GetProperty("name").GetString() ?? "SMAPI-installer.zip";
            var assetSize = selected.TryGetProperty("size", out var sizeElement) ? sizeElement.GetInt64() : 0;
            var downloadUrl = selected.GetProperty("browser_download_url").GetString() ?? string.Empty;
            var digest = selected.TryGetProperty("digest", out var digestElement)
                ? digestElement.GetString() ?? string.Empty : string.Empty;
            if (!downloadUrl.StartsWith("https://github.com/Pathoschild/SMAPI/releases/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("SMAPI 下载地址不是官方 GitHub Release，已停止安装。");
            if (!digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("SMAPI 官方资产没有提供 SHA-256 摘要，已停止安装。");

            Update(10, "smapi-download", "正在下载 SMAPI " + tag);
            var zipPath = Path.Combine(stagingRoot, assetName);
            var expectedDigest = digest.Substring("sha256:".Length);
            var cacheDirectory = Path.Combine(dataDirectory, "download-cache");
            var cachePath = Path.Combine(cacheDirectory, assetName);
            var cacheValid = File.Exists(cachePath) && string.Equals(
                await ComputeSha256Async(cachePath, token), expectedDigest, StringComparison.OrdinalIgnoreCase);
            if (cacheValid)
            {
                File.Copy(cachePath, zipPath);
                ReportDownloadProgress(assetSize, assetSize);
                AddLog("使用已验证的 SMAPI 下载缓存。");
            }
            else
            {
                await DownloadInParallelAsync(client, downloadUrl, zipPath, assetSize, token);
            }
            var actualDigest = await ComputeSha256Async(zipPath, token);
            if (!string.Equals(actualDigest, expectedDigest, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("SMAPI 安装包 SHA-256 校验失败，文件未执行。");
            if (!cacheValid)
            {
                Directory.CreateDirectory(cacheDirectory);
                File.Copy(zipPath, cachePath, true);
            }
            AddLog("SMAPI " + tag + " 下载完成，SHA-256 已验证。");

            Update(18, "smapi-extract", "正在解压 SMAPI 安装器");
            var extractPath = Path.Combine(stagingRoot, "smapi");
            ZipFile.ExtractToDirectory(zipPath, extractPath);
            var packagePath = extractPath;
            string installer = null;
            for (var depth = 0; depth < 3 && string.IsNullOrWhiteSpace(installer); depth++)
            {
                installer = Directory.EnumerateFiles(packagePath, "SMAPI.Installer.exe", SearchOption.AllDirectories)
                    .FirstOrDefault(path => path.Replace('/', '\\').IndexOf("\\internal\\windows\\",
                        StringComparison.OrdinalIgnoreCase) >= 0);
                if (!string.IsNullOrWhiteSpace(installer)) break;
                var nestedZip = Directory.EnumerateFiles(packagePath, "*.zip", SearchOption.AllDirectories)
                    .OrderBy(path => path.Length).FirstOrDefault();
                if (string.IsNullOrWhiteSpace(nestedZip)) break;
                var nestedPath = Path.Combine(extractPath, "nested-" + depth);
                ZipFile.ExtractToDirectory(nestedZip, nestedPath);
                packagePath = nestedPath;
                AddLog("已展开 SMAPI 发布包的内层压缩文件：" + Path.GetFileName(nestedZip));
            }
            if (string.IsNullOrWhiteSpace(installer))
                throw new InvalidOperationException("SMAPI 压缩包内没有找到 Windows 安装器。");
            Update(22, "smapi-install", "正在静默安装 SMAPI");
            await RunProcessAsync(installer, new[] { "--install", "--game-path", gamePath, "--no-prompt" },
                Path.GetDirectoryName(installer), null, TimeSpan.FromMinutes(5), token, true);
            if (!File.Exists(Path.Combine(gamePath, "StardewModdingAPI.exe")))
                throw new InvalidOperationException("SMAPI 安装器已退出，但没有生成 StardewModdingAPI.exe。");
            Update(30, "smapi", "SMAPI 安装完成");
        }

        private async Task DownloadInParallelAsync(HttpClient client, string url, string targetPath,
            long totalBytes, CancellationToken token)
        {
            if (totalBytes <= 0)
                throw new InvalidOperationException("SMAPI 官方资产没有提供文件大小，已停止安装。");
            const int segmentCount = 8;
            var partsPath = targetPath + ".parts";
            Directory.CreateDirectory(partsPath);
            long downloadedBytes = 0;
            var tasks = new List<Task>();
            for (var index = 0; index < segmentCount; index++)
            {
                var start = totalBytes * index / segmentCount;
                var end = totalBytes * (index + 1) / segmentCount - 1;
                var partPath = Path.Combine(partsPath, index.ToString("D2") + ".part");
                var existing = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
                var expected = end - start + 1;
                if (existing > expected)
                {
                    File.Delete(partPath);
                    existing = 0;
                }
                Interlocked.Add(ref downloadedBytes, existing);
                tasks.Add(DownloadSegmentAsync(client, url, partPath, start, end, existing,
                    totalBytes, () => Interlocked.Read(ref downloadedBytes), bytes =>
                    {
                        Interlocked.Add(ref downloadedBytes, bytes);
                        ReportDownloadProgress(Interlocked.Read(ref downloadedBytes), totalBytes);
                    }, token));
            }
            ReportDownloadProgress(Interlocked.Read(ref downloadedBytes), totalBytes);
            await Task.WhenAll(tasks);

            await using (var output = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                for (var index = 0; index < segmentCount; index++)
                {
                    var partPath = Path.Combine(partsPath, index.ToString("D2") + ".part");
                    await using var input = new FileStream(partPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    await input.CopyToAsync(output, token);
                }
            }
            Directory.Delete(partsPath, true);
        }

        private async Task DownloadSegmentAsync(HttpClient client, string url, string partPath,
            long rangeStart, long rangeEnd, long existingBytes, long totalBytes,
            Func<long> readDownloaded, Action<long> addDownloaded, CancellationToken token)
        {
            var expectedBytes = rangeEnd - rangeStart + 1;
            var written = existingBytes;
            var attempts = 0;
            while (written < expectedBytes)
            {
                token.ThrowIfCancellationRequested();
                attempts++;
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Range = new RangeHeaderValue(rangeStart + written, rangeEnd);
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                    if (response.StatusCode != System.Net.HttpStatusCode.PartialContent)
                        throw new InvalidOperationException("下载服务器没有接受分段请求：HTTP " + (int)response.StatusCode);
                    await using var input = await response.Content.ReadAsStreamAsync(token);
                    await using var output = new FileStream(partPath, FileMode.Append, FileAccess.Write, FileShare.Read);
                    var buffer = new byte[64 * 1024];
                    while (written < expectedBytes)
                    {
                        var count = await input.ReadAsync(buffer.AsMemory(0,
                            (int)Math.Min(buffer.Length, expectedBytes - written)), token);
                        if (count == 0) break;
                        await output.WriteAsync(buffer.AsMemory(0, count), token);
                        written += count;
                        addDownloaded(count);
                    }
                    if (written < expectedBytes)
                        throw new IOException("下载连接提前结束。");
                }
                catch (Exception) when (attempts < 4 && !token.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(attempts * 2), token);
                    written = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
                    ReportDownloadProgress(readDownloaded(), totalBytes);
                }
            }
        }

        private void ReportDownloadProgress(long downloadedBytes, long totalBytes)
        {
            var ratio = totalBytes <= 0 ? 0d : Math.Clamp((double)downloadedBytes / totalBytes, 0d, 1d);
            lock (gate)
            {
                progress = 10 + (int)Math.Floor(ratio * 7);
                stage = "smapi-download";
                message = "正在下载 SMAPI · " + (downloadedBytes / 1024d / 1024d).ToString("0.0") +
                          " / " + (totalBytes / 1024d / 1024d).ToString("0.0") + " MB";
            }
        }

        private async Task InstallMcpSourceAsync(CancellationToken token)
        {
            Update(34, "mcp-source", "正在准备 Stardew MCP 源码");
            if (Directory.Exists(Path.Combine(mcpRoot, ".git")))
            {
                AddLog("Stardew MCP 源码已存在，保留当前版本。");
                ApplySingleCompanionPatch();
                ApplyNativeAppearancePatch();
                ApplyCustomAssets();
                return;
            }
            if (Directory.Exists(mcpRoot) && Directory.EnumerateFileSystemEntries(mcpRoot).Any())
                throw new InvalidOperationException("MCP 安装目录已存在但不是 Git 仓库：" + mcpRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(mcpRoot));
            await RunProcessAsync("git.exe", new[] { "clone", "--depth", "1", McpRepository, mcpRoot },
                dataDirectory, null, TimeSpan.FromMinutes(8), token);
            if (!Directory.Exists(Path.Combine(mcpRoot, ".git")))
                throw new InvalidOperationException("Stardew MCP 仓库下载失败。");
            ApplySingleCompanionPatch();
            ApplyNativeAppearancePatch();
            ApplyCustomAssets();
            Update(48, "mcp-source", "Stardew MCP 源码下载完成");
        }

        private void ApplySingleCompanionPatch()
        {
            var serverPath = Path.Combine(mcpRoot, "mcp-server", "src", "index.ts");
            var botPath = Path.Combine(mcpRoot, "smapi-mod", "BotManager.cs");
            if (!File.Exists(serverPath) || !File.Exists(botPath))
                throw new InvalidOperationException("Stardew MCP 源码结构与 v0.3 不一致，无法应用单同伴补丁。");

            var server = File.ReadAllText(serverPath).Replace("\r\n", "\n");
            if (server.IndexOf("TraceSoul single-companion extension", StringComparison.Ordinal) < 0)
            {
                var beforeTool = string.Join("\n", new[]
                {
                    "name: \"stardew_spawn\",",
                    "                    description: \"Spawn companions into the game world near the player.\",",
                    "                    inputSchema: { type: \"object\", properties: {} },"
                });
                var afterTool = string.Join("\n", new[]
                {
                    "name: \"stardew_spawn\",",
                    "                    description: \"Spawn one selected companion into the game world near the player.\",",
                    "                    // TraceSoul single-companion extension",
                    "                    inputSchema: {",
                    "                        type: \"object\",",
                    "                        properties: {",
                    "                            target: { type: \"string\", enum: COMPANION_ENUM },",
                    "                            displayName: { type: \"string\" },",
                    "                        },",
                    "                    },"
                });
                var beforeCall = "return ok(sendAction({ actionType: \"spawn\" }));";
                var afterCall = "return ok(sendAction({ actionType: \"spawn\", ...(a.target ? { target: a.target } : {}), ...(a.displayName ? { displayName: a.displayName } : {}) }));";
                var changed = server.Replace(beforeTool, afterTool).Replace(beforeCall, afterCall);
                if (string.Equals(changed, server, StringComparison.Ordinal) ||
                    changed.IndexOf("TraceSoul single-companion extension", StringComparison.Ordinal) < 0 ||
                    changed.IndexOf(beforeCall, StringComparison.Ordinal) >= 0)
                    throw new InvalidOperationException("Stardew MCP Server 与 v0.3 不一致，单同伴补丁没有找到目标代码。");
                File.WriteAllText(serverPath, changed, new UTF8Encoding(false));
            }

            var bot = File.ReadAllText(botPath).Replace("\r\n", "\n");
            if (bot.IndexOf("TraceSoul single-companion extension", StringComparison.Ordinal) < 0)
            {
                var changed = bot.Replace(
                    "public void SpawnBot(string name, string type)",
                    "public void SpawnBot(string name, string type, string displayName = null)")
                    .Replace("botNpc.displayName = name;",
                        "botNpc.displayName = string.IsNullOrWhiteSpace(displayName) ? name : displayName;")
                    .Replace("[\"name\"] = kvp.Key,",
                        "[\"name\"] = kvp.Key,\n                    [\"displayName\"] = companion.Visual.displayName,")
                    .Replace(string.Join("\n", new[]
                    {
                        "case \"spawn\":",
                        "                    this.SpawnBot(\"Companion1\", \"Guard\");",
                        "                    this.SpawnBot(\"Companion2\", \"Anchor\");",
                        "                    this.SetAllMode(CompanionMode.Follow);",
                        "                    this.monitor.Log(\"Spawned and following\", LogLevel.Info);",
                        "                    break;"
                    }), string.Join("\n", new[]
                    {
                        "case \"spawn\":",
                        "                    // TraceSoul single-companion extension",
                        "                    if (root.TryGetProperty(\"target\", out var spawnTarget))",
                        "                    {",
                        "                        string targetName = spawnTarget.GetString();",
                        "                        string displayName = root.TryGetProperty(\"displayName\", out var display)",
                        "                            ? display.GetString() : targetName;",
                        "                        string type = targetName == \"Companion2\" ? \"Anchor\" : \"Guard\";",
                        "                        this.SpawnBot(targetName, type, displayName);",
                        "                        if (this.companions.TryGetValue(targetName, out var spawned))",
                        "                            spawned.Mode = CompanionMode.Follow;",
                        "                        this.monitor.Log($\"Spawned {displayName} ({targetName}) and following\", LogLevel.Info);",
                        "                    }",
                        "                    else",
                        "                    {",
                        "                        this.SpawnBot(\"Companion1\", \"Guard\");",
                        "                        this.SpawnBot(\"Companion2\", \"Anchor\");",
                        "                        this.SetAllMode(CompanionMode.Follow);",
                        "                        this.monitor.Log(\"Spawned and following\", LogLevel.Info);",
                        "                    }",
                        "                    break;"
                    }));
                if (string.Equals(changed, bot, StringComparison.Ordinal) ||
                    changed.IndexOf("TraceSoul single-companion extension", StringComparison.Ordinal) < 0)
                    throw new InvalidOperationException("SMAPI Mod 与 v0.3 不一致，单同伴补丁没有找到目标代码。");
                File.WriteAllText(botPath, changed, new UTF8Encoding(false));
            }
            AddLog("已应用 TraceSoul 单同伴与显示名兼容补丁。");
        }

        private bool IsSingleCompanionPatched()
        {
            var path = Path.Combine(mcpRoot, "smapi-mod", "BotManager.cs");
            return File.Exists(path) && File.ReadAllText(path).IndexOf(
                "TraceSoul single-companion extension", StringComparison.Ordinal) >= 0;
        }

        private void ApplyNativeAppearancePatch()
        {
            var serverPath = Path.Combine(mcpRoot, "mcp-server", "src", "index.ts");
            var botManagerPath = Path.Combine(mcpRoot, "smapi-mod", "BotManager.cs");
            var botFarmerPath = Path.Combine(mcpRoot, "smapi-mod", "BotFarmer.cs");
            var companionNpcPath = Path.Combine(mcpRoot, "smapi-mod", "CompanionNPC.cs");
            var companionFarmerPath = Path.Combine(mcpRoot, "smapi-mod", "CompanionFarmer.cs");
            var paths = new[] { serverPath, botManagerPath, botFarmerPath, companionNpcPath, companionFarmerPath };
            if (paths.Any(path => !File.Exists(path)))
                throw new InvalidOperationException("Stardew MCP 源码缺少原生角色外观补丁所需文件。");
            if (IsNativeAppearancePatched())
            {
                AddLog("TraceSoul 原生 Farmer 外观补丁已存在。");
                return;
            }

            var server = File.ReadAllText(serverPath).Replace("\r\n", "\n");
            var botManager = File.ReadAllText(botManagerPath).Replace("\r\n", "\n");
            var botFarmer = File.ReadAllText(botFarmerPath).Replace("\r\n", "\n");
            var companionNpc = File.ReadAllText(companionNpcPath).Replace("\r\n", "\n");
            var companionFarmer = File.ReadAllText(companionFarmerPath).Replace("\r\n", "\n");
            if (new[] { server, botManager, botFarmer, companionNpc, companionFarmer }.Any(value =>
                    value.IndexOf("TraceSoul native-farmer appearance extension", StringComparison.Ordinal) >= 0))
                throw new InvalidOperationException("检测到不完整的原生角色外观补丁，请重新下载 MCP 源码后再修复。");

            var serverChanged = server.Replace(
                JoinLines(
                    "displayName: { type: \"string\" },",
                    "                        },"),
                JoinLines(
                    "displayName: { type: \"string\" },",
                    "                            // TraceSoul native-farmer appearance extension",
                    "                            appearance: {",
                    "                                type: \"object\",",
                    "                                properties: {",
                    "                                    native: { type: \"boolean\" },",
                    "                                    gender: { type: \"string\", enum: [\"male\", \"female\"] },",
                    "                                    skin: { type: \"number\" },",
                    "                                    hair: { type: \"number\" },",
                    "                                    shirt: { type: \"number\" },",
                    "                                    pants: { type: \"number\" },",
                    "                                    accessory: { type: \"number\" },",
                    "                                },",
                    "                            },",
                    "                        },"))
                .Replace(
                    "return ok(sendAction({ actionType: \"spawn\", ...(a.target ? { target: a.target } : {}), ...(a.displayName ? { displayName: a.displayName } : {}) }));",
                    "return ok(sendAction({ actionType: \"spawn\", ...(a.target ? { target: a.target } : {}), ...(a.displayName ? { displayName: a.displayName } : {}), ...(a.appearance ? { appearance: a.appearance } : {}) }));");

            var botManagerChanged = botManager
                .Replace(
                    "public void SpawnBot(string name, string type, string displayName = null)",
                    JoinLines(
                        "// TraceSoul native-farmer appearance extension",
                        "        public void SpawnBot(string name, string type, string displayName = null,",
                        "            bool nativeAppearance = false, int gender = 1, int skin = 0, int hair = 0,",
                        "            int shirt = 0, int pants = 0, int accessory = -1)"))
                .Replace(
                    "var companionFarmer = new CompanionFarmer(botNpc, name, this.monitor, this.helper);",
                    JoinLines(
                        "var companionFarmer = new CompanionFarmer(botNpc, name, this.monitor, this.helper,",
                        "                    nativeAppearance, gender, skin, hair, shirt, pants, accessory);"))
                .Replace(
                    JoinLines(
                        "string type = targetName == \"Companion2\" ? \"Anchor\" : \"Guard\";",
                        "                        this.SpawnBot(targetName, type, displayName);"),
                    JoinLines(
                        "string type = targetName == \"Companion2\" ? \"Anchor\" : \"Guard\";",
                        "                        bool nativeAppearance = false;",
                        "                        int gender = 1, skin = 0, hair = 0, shirt = 0, pants = 0, accessory = -1;",
                        "                        if (root.TryGetProperty(\"appearance\", out var appearance))",
                        "                        {",
                        "                            nativeAppearance = !appearance.TryGetProperty(\"native\", out var native) || native.GetBoolean();",
                        "                            if (appearance.TryGetProperty(\"gender\", out var value)) gender = value.GetString() == \"male\" ? 0 : 1;",
                        "                            if (appearance.TryGetProperty(\"skin\", out value)) skin = value.GetInt32();",
                        "                            if (appearance.TryGetProperty(\"hair\", out value)) hair = value.GetInt32();",
                        "                            if (appearance.TryGetProperty(\"shirt\", out value)) shirt = value.GetInt32();",
                        "                            if (appearance.TryGetProperty(\"pants\", out value)) pants = value.GetInt32();",
                        "                            if (appearance.TryGetProperty(\"accessory\", out value)) accessory = value.GetInt32();",
                        "                        }",
                        "                        this.SpawnBot(targetName, type, displayName, nativeAppearance,",
                        "                            gender, skin, hair, shirt, pants, accessory);"));

            var botFarmerChanged = botFarmer.Replace(
                JoinLines(
                    "public override void draw(SpriteBatch b)",
                    "        {",
                    "            // No-op: companion NPC handles all rendering",
                    "        }"),
                JoinLines(
                    "public override void draw(SpriteBatch b)",
                    "        {",
                    "            // No-op: companion NPC handles all rendering",
                    "        }",
                    "",
                    "        // TraceSoul native-farmer appearance extension",
                    "        public void DrawVisible(SpriteBatch b)",
                    "        {",
                    "            base.draw(b);",
                    "        }",
                    "",
                    "        public void ConfigureAppearance(int gender, int skin, int hair, int shirt, int pants, int accessory)",
                    "        {",
                    "            this.changeGender(gender != 1);",
                    "            this.changeSkinColor(skin);",
                    "            this.changeHairStyle(hair);",
                    "            this.changeShirt(shirt.ToString());",
                    "            this.changePantStyle(pants.ToString());",
                    "            this.changeAccessory(accessory);",
                    "        }"));

            var companionNpcChanged = companionNpc
                .Replace(
                    "public float WidthScale { get; set; } = 1.3f;",
                    JoinLines(
                        "public float WidthScale { get; set; } = 1.3f;",
                        "        // TraceSoul native-farmer appearance extension",
                        "        public BotFarmer FarmerAppearance { get; set; }"))
                .Replace(
                    JoinLines(
                        "public override void draw(SpriteBatch b, float alpha = 1f)",
                        "        {",
                        "            if (Sprite?.Texture == null || IsInvisible) return;"),
                    JoinLines(
                        "public override void draw(SpriteBatch b, float alpha = 1f)",
                        "        {",
                        "            if (FarmerAppearance != null)",
                        "            {",
                        "                FarmerAppearance.Position = this.Position;",
                        "                FarmerAppearance.currentLocation = this.currentLocation;",
                        "                FarmerAppearance.FacingDirection = this.FacingDirection;",
                        "                FarmerAppearance.FarmerSprite.faceDirectionStandard(this.FacingDirection);",
                        "                FarmerAppearance.DrawVisible(b);",
                        "                return;",
                        "            }",
                        "            if (Sprite?.Texture == null || IsInvisible) return;"));

            var companionFarmerChanged = companionFarmer
                .Replace(
                    "public CompanionFarmer(NPC visualNpc, string name, IMonitor monitor, IModHelper helper)",
                    JoinLines(
                        "// TraceSoul native-farmer appearance extension",
                        "        public CompanionFarmer(NPC visualNpc, string name, IMonitor monitor, IModHelper helper,",
                        "            bool nativeAppearance = false, int gender = 1, int skin = 0, int hair = 0,",
                        "            int shirt = 0, int pants = 0, int accessory = -1)"))
                .Replace(
                    "this.Shadow.MaxItems = 36;",
                    JoinLines(
                        "this.Shadow.MaxItems = 36;",
                        "            if (nativeAppearance)",
                        "            {",
                        "                this.Shadow.ConfigureAppearance(gender, skin, hair, shirt, pants, accessory);",
                        "                if (this.Visual is CompanionNPC companionNpc)",
                        "                    companionNpc.FarmerAppearance = this.Shadow;",
                        "            }"));

            var changedFiles = new[] { serverChanged, botManagerChanged, botFarmerChanged,
                companionNpcChanged, companionFarmerChanged };
            if (changedFiles.Any(value => value.IndexOf(
                    "TraceSoul native-farmer appearance extension", StringComparison.Ordinal) < 0))
                throw new InvalidOperationException("Stardew MCP v0.3 结构已变化，无法应用原生角色外观补丁。");

            File.WriteAllText(serverPath, serverChanged, new UTF8Encoding(false));
            File.WriteAllText(botManagerPath, botManagerChanged, new UTF8Encoding(false));
            File.WriteAllText(botFarmerPath, botFarmerChanged, new UTF8Encoding(false));
            File.WriteAllText(companionNpcPath, companionNpcChanged, new UTF8Encoding(false));
            File.WriteAllText(companionFarmerPath, companionFarmerChanged, new UTF8Encoding(false));
            AddLog("已应用星露谷原生 Farmer 外观与部件 ID 补丁。");
        }

        private bool IsNativeAppearancePatched()
        {
            var files = new[]
            {
                Path.Combine(mcpRoot, "mcp-server", "src", "index.ts"),
                Path.Combine(mcpRoot, "smapi-mod", "BotManager.cs"),
                Path.Combine(mcpRoot, "smapi-mod", "BotFarmer.cs"),
                Path.Combine(mcpRoot, "smapi-mod", "CompanionNPC.cs"),
                Path.Combine(mcpRoot, "smapi-mod", "CompanionFarmer.cs")
            };
            return files.All(path => File.Exists(path) && File.ReadAllText(path).IndexOf(
                "TraceSoul native-farmer appearance extension", StringComparison.Ordinal) >= 0);
        }

        private static string JoinLines(params string[] lines) { return string.Join("\n", lines); }

        private void ApplyCustomAssets()
        {
            var customRoot = Path.Combine(dataDirectory, "stardew", "custom");
            var assetRoot = Path.Combine(mcpRoot, "smapi-mod", "assets");
            if (!Directory.Exists(customRoot) || !Directory.Exists(assetRoot)) return;
            foreach (var source in Directory.EnumerateFiles(customRoot, "Companion*_*.png"))
                File.Copy(source, Path.Combine(assetRoot, Path.GetFileName(source)), true);
        }

        private void WriteAsset(string customRoot, string gamePath, string fileName, byte[] bytes)
        {
            var customPath = Path.Combine(customRoot, fileName);
            File.WriteAllBytes(customPath, bytes);
            var sourceAssets = Path.Combine(mcpRoot, "smapi-mod", "assets");
            if (Directory.Exists(sourceAssets)) File.Copy(customPath, Path.Combine(sourceAssets, fileName), true);
            var modPath = FindBridgeMod(Path.Combine(gamePath, "Mods"));
            if (!string.IsNullOrWhiteSpace(modPath))
            {
                var liveAssets = Path.Combine(modPath, "assets");
                Directory.CreateDirectory(liveAssets);
                File.Copy(customPath, Path.Combine(liveAssets, fileName), true);
            }
        }

        private static byte[] DecodePng(string value, int maxBytes, string label)
        {
            value = (value ?? string.Empty).Trim();
            var comma = value.IndexOf(',');
            if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma >= 0)
                value = value.Substring(comma + 1);
            byte[] bytes;
            try { bytes = Convert.FromBase64String(value); }
            catch (FormatException) { throw new InvalidOperationException(label + "不是有效的 Base64 PNG。"); }
            if (bytes.Length > maxBytes) throw new InvalidOperationException(label + "不能超过 6 MB。");
            ReadPngSize(bytes);
            return bytes;
        }

        private static Tuple<int, int> ReadPngSize(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 24 || bytes[0] != 137 || bytes[1] != 80 ||
                bytes[2] != 78 || bytes[3] != 71 || bytes[12] != 73 || bytes[13] != 72 ||
                bytes[14] != 68 || bytes[15] != 82)
                throw new InvalidOperationException("文件不是有效的 PNG。");
            var width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
            var height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
            if (width <= 0 || height <= 0) throw new InvalidOperationException("PNG 尺寸无效。");
            return Tuple.Create(width, height);
        }

        private async Task BuildMcpServerAsync(CancellationToken token)
        {
            var serverRoot = Path.Combine(mcpRoot, "mcp-server");
            if (!File.Exists(Path.Combine(serverRoot, "package.json")))
                throw new InvalidOperationException("MCP 仓库缺少 mcp-server/package.json。");
            Update(52, "mcp-dependencies", "正在安装 MCP Server 依赖");
            var npmInstall = File.Exists(Path.Combine(serverRoot, "package-lock.json"))
                ? new[] { "ci", "--no-audit", "--no-fund" }
                : new[] { "install", "--no-audit", "--no-fund" };
            await RunProcessAsync("npm.cmd", npmInstall, serverRoot, null, TimeSpan.FromMinutes(10), token);
            Update(68, "mcp-build", "正在编译 MCP Server");
            await RunProcessAsync("npm.cmd", new[] { "run", "build" }, serverRoot, null,
                TimeSpan.FromMinutes(5), token);
            if (!File.Exists(Path.Combine(serverRoot, "build", "index.js")))
                throw new InvalidOperationException("MCP Server 编译后没有生成 build/index.js。");
            Update(76, "mcp-build", "MCP Server 编译完成");
        }

        private async Task BuildSmapiModAsync(string gamePath, CancellationToken token)
        {
            var projectRoot = Path.Combine(mcpRoot, "smapi-mod");
            var project = Path.Combine(projectRoot, "StardewMCPBridge.csproj");
            if (!File.Exists(project))
                throw new InvalidOperationException("MCP 仓库缺少 StardewMCPBridge.csproj。");
            Update(80, "mod-build", "正在编译并部署 SMAPI 桥接 Mod");
            var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["GAME_PATH"] = gamePath
            };
            await RunProcessAsync("dotnet.exe", new[] { "build", project, "-c", "Release", "--nologo",
                    "-p:NuGetAudit=false", "-p:RestoreIgnoreFailedSources=true" },
                projectRoot, environment, TimeSpan.FromMinutes(10), token);
            var modPath = FindBridgeMod(Path.Combine(gamePath, "Mods"));
            if (string.IsNullOrWhiteSpace(modPath))
                throw new InvalidOperationException("桥接 Mod 编译成功，但没有部署到游戏 Mods 目录。");
            Directory.CreateDirectory(Path.Combine(modPath, "actions"));
            File.WriteAllText(Path.Combine(modPath, "tracesoul-patch-v2.json"),
                JsonSerializer.Serialize(new
                {
                    patch = "single-companion-native-v2",
                    installed_unix_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                }, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            Update(94, "mod-build", "SMAPI 桥接 Mod 已部署");
            AddLog("桥接 Mod：" + modPath);
        }

        private void WriteConnectionConfig(string gamePath)
        {
            var status = GetStatus(gamePath);
            var configPath = Path.Combine(dataDirectory, "stardew", "mcp-connection.json");
            Directory.CreateDirectory(Path.GetDirectoryName(configPath));
            var content = JsonSerializer.Serialize(new
            {
                command = "node",
                args = new[] { status.mcp_server_entry },
                env = new
                {
                    STARDEW_BRIDGE_PATH = status.bridge_path,
                    STARDEW_ACTION_DIR = status.action_dir
                },
                source = McpRepository,
                tracesoul_patch = "single-companion-native-v2",
                game_path = gamePath
            }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(configPath, content, new UTF8Encoding(false));
            AddLog("已生成连接配置：" + configPath);
        }

        private async Task RunProcessAsync(string fileName, IEnumerable<string> arguments, string workingDirectory,
            IDictionary<string, string> environment, TimeSpan timeout, CancellationToken token,
            bool useHiddenConsole = false)
        {
            var resolvedFileName = ResolveCommand(fileName);
            if (!string.IsNullOrWhiteSpace(resolvedFileName)) fileName = resolvedFileName;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(timeout);
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = workingDirectory,
                UseShellExecute = useHiddenConsole,
                RedirectStandardOutput = !useHiddenConsole,
                RedirectStandardError = !useHiddenConsole,
                CreateNoWindow = !useHiddenConsole,
                WindowStyle = useHiddenConsole ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal
            };
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            if (environment != null)
                foreach (var pair in environment) process.StartInfo.Environment[pair.Key] = pair.Value;
            if (!useHiddenConsole)
            {
                process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) AddLog(e.Data); };
                process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) AddLog(e.Data); };
            }
            AddLog("运行：" + Path.GetFileName(fileName) + " " + string.Join(" ", arguments));
            if (!process.Start()) throw new InvalidOperationException("无法启动：" + fileName);
            if (!useHiddenConsole)
            {
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(true); } catch { }
                if (!token.IsCancellationRequested)
                    throw new TimeoutException(Path.GetFileName(fileName) + " 执行超时。");
                throw;
            }
            if (process.ExitCode != 0)
                throw new InvalidOperationException(Path.GetFileName(fileName) + " 执行失败，退出码 " + process.ExitCode + "。");
        }

        private string ResolveGamePath(string requested)
        {
            var explicitPath = NormalizeGamePath(requested);
            if (IsGamePath(explicitPath)) return explicitPath;
            lock (gate)
            {
                var selected = NormalizeGamePath(selectedGamePath);
                if (IsGamePath(selected)) return selected;
            }
            if (OperatingSystem.IsWindows())
                foreach (var candidate in SteamCandidates())
                    if (IsGamePath(candidate)) return candidate;
            return explicitPath;
        }

        [SupportedOSPlatform("windows")]
        private static IEnumerable<string> SteamCandidates()
        {
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    using var key = baseKey.OpenSubKey(@"SOFTWARE\Valve\Steam");
                    var path = key?.GetValue("InstallPath") as string;
                    if (!string.IsNullOrWhiteSpace(path)) roots.Add(path);
                }
                catch { }
            }
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                var path = key?.GetValue("SteamPath") as string;
                if (!string.IsNullOrWhiteSpace(path)) roots.Add(path);
            }
            catch { }
            foreach (var root in roots.ToArray())
            {
                var libraries = Path.Combine(root, "steamapps", "libraryfolders.vdf");
                if (File.Exists(libraries))
                {
                    foreach (var line in File.ReadLines(libraries))
                    {
                        var marker = "\"path\"";
                        var index = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                        if (index < 0) continue;
                        var rest = line.Substring(index + marker.Length).Trim();
                        if (!rest.StartsWith("\"", StringComparison.Ordinal)) continue;
                        var end = rest.IndexOf('"', 1);
                        if (end > 1) roots.Add(rest.Substring(1, end - 1).Replace("\\\\", "\\"));
                    }
                }
            }
            foreach (var root in roots)
                yield return Path.Combine(root, "steamapps", "common", "Stardew Valley");
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
                yield return Path.Combine(drive.RootDirectory.FullName, "Game", "Steam", "steamapps", "common", "Stardew Valley");
        }

        private static string NormalizeGamePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            try { return Path.GetFullPath(path.Trim().Trim('"')); }
            catch { return string.Empty; }
        }

        private static bool IsGamePath(string path)
        {
            return !string.IsNullOrWhiteSpace(path) && File.Exists(Path.Combine(path, "Stardew Valley.exe"));
        }

        private static string FindBridgeMod(string modsPath)
        {
            if (string.IsNullOrWhiteSpace(modsPath) || !Directory.Exists(modsPath)) return string.Empty;
            foreach (var manifest in Directory.EnumerateFiles(modsPath, "manifest.json", SearchOption.AllDirectories))
            {
                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(manifest));
                    if (document.RootElement.TryGetProperty("UniqueID", out var id) &&
                        string.Equals(id.GetString(), "Antigravity.StardewMCPBridge", StringComparison.OrdinalIgnoreCase))
                        return Path.GetDirectoryName(manifest) ?? string.Empty;
                }
                catch { }
            }
            return string.Empty;
        }

        private static bool IsGameRunning()
        {
            return Process.GetProcessesByName("Stardew Valley").Length > 0 ||
                   Process.GetProcessesByName("StardewModdingAPI").Length > 0;
        }

        private static bool CommandExists(string command)
        {
            return !string.IsNullOrWhiteSpace(ResolveCommand(command));
        }

        private static string ResolveCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return string.Empty;
            if (Path.IsPathRooted(command)) return File.Exists(command) ? Path.GetFullPath(command) : string.Empty;
            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var folder in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(folder.Trim().Trim('"'), command);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
            return string.Empty;
        }

        private static void RequireCommand(string command, string displayName)
        {
            if (!CommandExists(command))
                throw new InvalidOperationException("缺少 " + displayName + "，请先安装后再重试。");
        }

        private static async Task<string> ComputeSha256Async(string path, CancellationToken token)
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var hash = SHA256.Create();
            var bytes = await hash.ComputeHashAsync(stream, token);
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        private void Update(int newProgress, string newStage, string newMessage)
        {
            lock (gate)
            {
                progress = newProgress;
                stage = newStage;
                message = newMessage;
            }
            AddLog(newMessage);
        }

        private void Fail(string failure)
        {
            lock (gate)
            {
                stage = "failed";
                message = "安装失败";
                error = failure ?? "未知错误";
            }
        }

        private void AddLog(string line)
        {
            lock (gate) AddLogUnsafe(line);
        }

        private void AddLogUnsafe(string line)
        {
            var value = (line ?? string.Empty).Trim();
            if (value.Length == 0) return;
            installLog.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + value);
            if (installLog.Count > 120) installLog.RemoveRange(0, installLog.Count - 120);
        }

        private void TryDeleteStaging(string stagingRoot)
        {
            try
            {
                var parent = Path.GetFullPath(Path.Combine(dataDirectory, "install-staging"));
                var target = Path.GetFullPath(stagingRoot);
                if (target.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                    Directory.Exists(target)) Directory.Delete(target, true);
            }
            catch { }
        }

        public void Dispose()
        {
            shutdown.Cancel();
            shutdown.Dispose();
        }
    }
}
