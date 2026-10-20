param(
    [Parameter(Mandatory=$true)][string]$IdfPath,
    [Parameter(Mandatory=$true)][string]$ToolsPath,
    [Parameter(Mandatory=$true)][string]$PythonEnvironment,
    [Parameter(Mandatory=$true)][string]$BuildDirectory
)
$ErrorActionPreference = 'Stop'
$env:IDF_PATH = (Resolve-Path -LiteralPath $IdfPath).Path
$env:ESP_IDF_VERSION = '5.5.1'
$env:IDF_TOOLS_PATH = (Resolve-Path -LiteralPath $ToolsPath).Path
$env:IDF_PYTHON_ENV_PATH = (Resolve-Path -LiteralPath $PythonEnvironment).Path
$python = Join-Path $env:IDF_PYTHON_ENV_PATH 'Scripts/python.exe'
$toolBins = Get-ChildItem -LiteralPath (Join-Path $env:IDF_TOOLS_PATH 'tools') -Filter '*.exe' -Recurse |
    Where-Object { $_.Name -in @('xtensa-esp32s3-elf-gcc.exe', 'cmake.exe', 'ninja.exe') } |
    Select-Object -ExpandProperty DirectoryName -Unique
$env:PATH = "$env:IDF_PYTHON_ENV_PATH/Scripts;" + ($toolBins -join ';') + ';' + $env:PATH
Push-Location (Join-Path $PSScriptRoot 'firmware')
try {
    & $python (Join-Path $env:IDF_PATH 'tools/idf.py') -B $BuildDirectory -D "SDKCONFIG=$BuildDirectory/sdkconfig" build
    if ($LASTEXITCODE -ne 0) { throw 'Firmware build failed' }
} finally { Pop-Location }
