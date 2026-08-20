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
New-Item -ItemType Directory -Path $packageRoot,$updaterRoot,$artifactRoot -Force | Out-Null

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
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Start-TraceSoul2.cmd') -Destination $packageRoot -Force

$installManifest = [ordered]@{
    product = 'TraceSoul2'
    version = $version
    runtime = $Runtime
    createdUtc = [DateTimeOffset]::UtcNow.ToString('O')
}
$installManifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $packageRoot 'tracesoul2.install.json') -Encoding utf8NoBOM

$zipName = "tracesoul2-$Runtime-v$version.zip"
$zipPath = Join-Path $artifactRoot $zipName
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
Compress-Archive -Path (Join-Path $packageRoot '*') -DestinationPath $zipPath -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
($hash + '  ' + $zipName) | Set-Content -LiteralPath ($zipPath + '.sha256') -Encoding ascii

dotnet pack (Join-Path $repoRoot 'Tools\PluginApi\TraceSoul2.PluginApi.csproj') `
    -c Release -o $artifactRoot --no-build
if ($LASTEXITCODE -ne 0) { throw 'PluginApi SDK 打包失败。' }

Write-Host "发布包已生成：$zipPath"
Write-Host "SHA-256：$hash"
