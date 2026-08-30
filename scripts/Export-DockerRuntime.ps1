param(
    [Parameter(Mandatory=$true)][string]$SourceHome,
    [Parameter(Mandatory=$true)][string]$SourcePlugins,
    [Parameter(Mandatory=$true)][string]$SourcePluginsData,
    [string]$Destination = (Join-Path $PSScriptRoot '..\runtime')
)

$ErrorActionPreference = 'Stop'
$destinationRoot = [IO.Path]::GetFullPath($Destination)
$sourceHomeRoot = [IO.Path]::GetFullPath($SourceHome)
$sourcePluginsRoot = [IO.Path]::GetFullPath($SourcePlugins)
$sourcePluginsDataRoot = [IO.Path]::GetFullPath($SourcePluginsData)
foreach ($required in @($sourceHomeRoot,$sourcePluginsRoot,$sourcePluginsDataRoot)) {
    if (-not (Test-Path -LiteralPath $required -PathType Container)) {
        throw "源目录不存在：$required"
    }
}

$appRoot = Join-Path $destinationRoot 'App'
$dataRoot = Join-Path $destinationRoot 'Data'
$pluginsRoot = Join-Path $destinationRoot 'Plugins'
$pluginsDataRoot = Join-Path $destinationRoot 'plugins_data'
New-Item -ItemType Directory -Force -Path $appRoot,$dataRoot,$pluginsRoot,$pluginsDataRoot | Out-Null

Get-ChildItem -LiteralPath $sourceHomeRoot -Force |
    Where-Object { $_.Name -notin @('plugins','plugins_data','updates') } |
    Copy-Item -Destination $dataRoot -Recurse -Force
Get-ChildItem -LiteralPath $sourcePluginsRoot -Force |
    Copy-Item -Destination $pluginsRoot -Recurse -Force
Get-ChildItem -LiteralPath $sourcePluginsDataRoot -Force |
    Copy-Item -Destination $pluginsDataRoot -Recurse -Force

# NapCat 的 Windows 启动路径不可能在 Ubuntu 容器内生效；只清理导出副本。
Get-ChildItem -LiteralPath $dataRoot -Recurse -File -Filter 'onebot.json' | ForEach-Object {
    try {
        $oneBotConfig = Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
        if ($oneBotConfig.PSObject.Properties.Name -contains 'napcat_path') {
            $oneBotConfig.napcat_path = ''
            $oneBotConfig | ConvertTo-Json -Depth 20 |
                Set-Content -LiteralPath $_.FullName -Encoding utf8NoBOM
        }
    } catch {
        Write-Warning "无法清理 $($_.FullName) 的 NapCat 路径：$($_.Exception.Message)"
    }
}

$homePath = Join-Path $dataRoot 'home.json'
$homeConfig = if (Test-Path -LiteralPath $homePath) {
    Get-Content -LiteralPath $homePath -Raw | ConvertFrom-Json
} else {
    [pscustomobject]@{ activeSoul=''; urls='http://127.0.0.1:5080' }
}
$homeConfig | Add-Member -NotePropertyName pluginsDirectory -NotePropertyValue '../Plugins' -Force
$homeConfig | Add-Member -NotePropertyName pluginsDataDirectory -NotePropertyValue '../plugins_data' -Force
$homeConfig | Add-Member -NotePropertyName updateRepository -NotePropertyValue 'TYty0728/TraceSoul2' -Force
$homeConfig | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $homePath -Encoding utf8NoBOM

Write-Host "Docker 运行目录已导出：$destinationRoot"
Write-Host '请检查 onebot.json、LLM 与插件配置中的本机绝对路径，再上传整个 runtime 目录。'
