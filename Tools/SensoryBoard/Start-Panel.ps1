param(
    [Parameter(Mandatory=$true)][string]$RuntimeDirectory,
    [string]$Python = 'python',
    [string]$Port = 'COM3',
    [int]$HttpPort = 5089,
    [switch]$OpenBrowser
)
$ErrorActionPreference = 'Stop'
if ($Port -notmatch '^COM[1-9][0-9]*$') { throw 'Specify a Windows COM port' }
if ($HttpPort -lt 1024 -or $HttpPort -gt 65535) { throw 'Invalid HTTP port' }
$address = "http://127.0.0.1:$HttpPort"
$existing = $null
try { $existing = Invoke-RestMethod "$address/api/health" -TimeoutSec 2 } catch {}
if ($existing -and $existing.service -eq 'tracesoul-sensory-usb') {
    Write-Output "Control panel already running: $address"
    if ($OpenBrowser) { Start-Process $address }
    return
}
if (Get-NetTCPConnection -LocalPort $HttpPort -State Listen -ErrorAction SilentlyContinue) {
    throw "Port $HttpPort is already in use by another service"
}
$pythonPath = (Get-Command $Python -ErrorAction Stop).Source
& $pythonPath -c 'import serial, cryptography; from PIL import Image'
if ($LASTEXITCODE -ne 0) { throw 'Install requirements.txt into the selected Python environment first' }
New-Item -ItemType Directory -Path $RuntimeDirectory -Force | Out-Null
$runtimePath = (Resolve-Path -LiteralPath $RuntimeDirectory).Path
$scriptPath = Join-Path $PSScriptRoot 'bridge.py'
$bridgeArguments = @('-u', ('"{0}"' -f $scriptPath), '--port', $Port, '--http-port', $HttpPort)
$bridgeArguments += @('--connection-config', ('"{0}"' -f (Join-Path $runtimePath 'wifi-client.json')))
$asrDirectory = Join-Path $runtimePath 'asr/sensevoice-int8'
if (Test-Path -LiteralPath (Join-Path $asrDirectory 'ready.json')) {
    $bridgeArguments += @('--asr-model-directory', ('"{0}"' -f $asrDirectory))
}
$process = Start-Process -FilePath $pythonPath -ArgumentList $bridgeArguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $runtimePath 'panel.log') -RedirectStandardError (Join-Path $runtimePath 'panel-error.log')
$process.Id | Set-Content -LiteralPath (Join-Path $runtimePath 'panel.pid')
for ($attempt = 0; $attempt -lt 30; $attempt++) {
    Start-Sleep -Milliseconds 200
    try {
        $health = Invoke-RestMethod "$address/api/health" -TimeoutSec 1
        if ($health.service -eq 'tracesoul-sensory-usb') {
            Write-Output "Control panel ready: $address"
            if ($OpenBrowser) { Start-Process $address }
            return
        }
    } catch {}
    if ($process.HasExited) { break }
}
throw "Control panel did not start. See $runtimePath/panel-error.log"
