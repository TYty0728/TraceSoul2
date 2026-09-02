param(
    [string]$ExpectedVersion = '',
    [string]$Runtime = 'win-x64',
    [switch]$SelfContained
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$propsPath = Join-Path $repoRoot 'Tools\Directory.Build.props'
[xml]$props = Get-Content -LiteralPath $propsPath -Raw
$version = [string]$props.Project.PropertyGroup.TraceSoul2Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "无效的 TraceSoul2Version：$version" }
if ($ExpectedVersion -and $ExpectedVersion.TrimStart('v') -ne $version) {
    throw "标签/参数版本 $ExpectedVersion 与源码版本 $version 不一致。"
}

& (Join-Path $PSScriptRoot 'Test-PublishSafety.ps1')

$modelPath = Join-Path $repoRoot 'models\BgeSmallZh\bge-small-zh-v1.5.onnx'
if (-not (Test-Path -LiteralPath $modelPath) -or (Get-Item -LiteralPath $modelPath).Length -lt 50MB) {
    throw 'BGE ONNX 模型不存在或还是 Git LFS 指针；请先执行 git lfs pull。'
}

$runId = [guid]::NewGuid().ToString('N')
$releaseRoot = Join-Path $repoRoot ('.release\' + $runId)
$packageRoot = Join-Path $releaseRoot 'package'
$updaterRoot = Join-Path $releaseRoot 'updater'
$artifactRoot = Join-Path $repoRoot ('artifacts\' + $version)
$bundledPluginsRoot = Join-Path $packageRoot 'BundledPlugins'
New-Item -ItemType Directory -Path $packageRoot,$updaterRoot,$artifactRoot,$bundledPluginsRoot -Force | Out-Null

$selfContainedValue = if ($SelfContained) { 'true' } else { 'false' }
dotnet publish (Join-Path $repoRoot 'Tools\Host\TraceSoul2.Host.csproj') `
    -c Release -r $Runtime --self-contained $selfContainedValue -o $packageRoot
if ($LASTEXITCODE -ne 0) { throw 'Host 发布失败。' }

dotnet publish (Join-Path $repoRoot 'Tools\Migration\TraceSoul2.Migrate.csproj') `
    -c Release -r $Runtime --self-contained $selfContainedValue -o $packageRoot
if ($LASTEXITCODE -ne 0) { throw 'Migration 发布失败。' }

dotnet publish (Join-Path $repoRoot 'Tools\Updater\TraceSoul2.Updater.csproj') `
    -c Release -r $Runtime --self-contained $selfContainedValue -o $updaterRoot
if ($LASTEXITCODE -ne 0) { throw 'Updater 发布失败。' }

Get-ChildItem -LiteralPath $updaterRoot -File | Where-Object {
    $_.Name -like 'TraceSoul2.Updater*'
} | Copy-Item -Destination $packageRoot -Force
$launcher = if ($Runtime.StartsWith('win-', [StringComparison]::OrdinalIgnoreCase)) {
    'Start-TraceSoul2.cmd'
} elseif ($Runtime.StartsWith('linux-', [StringComparison]::OrdinalIgnoreCase)) {
    'Start-TraceSoul2.sh'
} else {
    throw "没有为 $Runtime 配置启动脚本。"
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot $launcher) -Destination $packageRoot -Force

# 仓库内随产品维护的官方插件与 Host 一起发布。插件代码包可以替换，用户配置与生成文件
# 始终位于外部 plugins_data/，更新器不会触碰。
$bundledPlugins = @(
    @{ Name = 'qq-tts'; Project = 'ExternalPlugins\QqTts\TraceSoul2.Plugin.QqTts.csproj' },
    @{ Name = 'qq-imagegen'; Project = 'ExternalPlugins\QqImageGen\TraceSoul2.Plugin.QqImageGen.csproj' },
    @{ Name = 'qq-qzone'; Project = 'ExternalPlugins\QqQzone\TraceSoul2.Plugin.QqQzone.csproj' },
    @{ Name = 'qq-status'; Project = 'ExternalPlugins\QqStatus\TraceSoul2.Plugin.QqStatus.csproj' },
    @{ Name = 'game-session'; Project = 'ExternalPlugins\GameSession\TraceSoul2.Plugin.GameSession.csproj' }
)
foreach ($plugin in $bundledPlugins) {
    $projectPath = Join-Path $repoRoot $plugin.Project
    $projectDirectory = Split-Path -Parent $projectPath
    $pluginTarget = Join-Path $bundledPluginsRoot $plugin.Name
    New-Item -ItemType Directory -Path $pluginTarget -Force | Out-Null
    dotnet publish $projectPath -c Release -r $Runtime --self-contained $selfContainedValue -o $pluginTarget
    if ($LASTEXITCODE -ne 0) { throw "官方插件发布失败：$($plugin.Name)" }
    foreach ($requiredFile in @('plugin.json', 'config_schema.json')) {
        $sourceFile = Join-Path $projectDirectory $requiredFile
        if (Test-Path -LiteralPath $sourceFile) {
            Copy-Item -LiteralPath $sourceFile -Destination $pluginTarget -Force
        }
    }
    $protocol = Join-Path $projectDirectory 'protocol'
    if (Test-Path -LiteralPath $protocol) {
        Copy-Item -LiteralPath $protocol -Destination $pluginTarget -Recurse -Force
    }
    $manifest = Join-Path $pluginTarget 'plugin.json'
    if (-not (Test-Path -LiteralPath $manifest)) {
        throw "官方插件包缺少 plugin.json：$($plugin.Name)"
    }
    $dllName = (Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json).dll
    if (-not $dllName -or -not (Test-Path -LiteralPath (Join-Path $pluginTarget $dllName))) {
        throw "官方插件包缺少清单指定的 DLL：$($plugin.Name)"
    }
}

$installManifest = [ordered]@{
    product = 'TraceSoul2'
    version = $version
    runtime = $Runtime
    createdUtc = [DateTimeOffset]::UtcNow.ToString('O')
    bundledPlugins = @($bundledPlugins | ForEach-Object { $_.Name })
}
$installManifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $packageRoot 'tracesoul2.install.json') -Encoding utf8NoBOM

$zipName = "tracesoul2-$Runtime-v$version.zip"
$zipPath = Join-Path $artifactRoot $zipName
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
Compress-Archive -Path (Join-Path $packageRoot '*') -DestinationPath $zipPath -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($zipPath + '.sha256', $hash + '  ' + $zipName + "`n", [Text.Encoding]::ASCII)

dotnet pack (Join-Path $repoRoot 'Tools\PluginApi\TraceSoul2.PluginApi.csproj') `
    -c Release -o $artifactRoot --no-build
if ($LASTEXITCODE -ne 0) { throw 'PluginApi SDK 打包失败。' }

Write-Host "发布包已生成：$zipPath"
Write-Host "SHA-256：$hash"
