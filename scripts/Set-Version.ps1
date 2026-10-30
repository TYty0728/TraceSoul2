param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Version
)

$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw '版本号必须是稳定版 SemVer，例如 0.2.0。日常提交不要运行此脚本。'
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$propsPath = Join-Path $repoRoot 'Tools\Directory.Build.props'
$text = [IO.File]::ReadAllText($propsPath)
$updated = [regex]::Replace(
    $text,
    '<TraceSoul2Version>[^<]+</TraceSoul2Version>',
    '<TraceSoul2Version>' + $Version + '</TraceSoul2Version>',
    1)
if ($updated -eq $text) {
    throw '没有找到 TraceSoul2Version，未修改任何文件。'
}
[IO.File]::WriteAllText($propsPath, $updated, [Text.UTF8Encoding]::new($false))
Write-Host "产品版本已改为 $Version。请完成本次改动相关验证，再提交并创建标签 v$Version；完整回归由发布 CI 执行，已通过的本地检查无需重复。"
