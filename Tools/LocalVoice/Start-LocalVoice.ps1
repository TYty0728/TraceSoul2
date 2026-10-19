param(
    [Parameter(Mandatory=$true)][string]$RuntimeDirectory,
    [int]$Port = 5088,
    [switch]$NoPredictorGraph
)
$ErrorActionPreference = 'Stop'
$voiceRoot = (Resolve-Path -LiteralPath $RuntimeDirectory).Path
$voicePython = Join-Path $voiceRoot 'venv/Scripts/python.exe'
$voiceServer = Join-Path $voiceRoot 'app/server.py'
if (!(Test-Path -LiteralPath $voicePython) -or !(Test-Path -LiteralPath $voiceServer)) {
    throw 'Local voice runtime is not installed.'
}
$occupied = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
if ($occupied) {
    $existing = Get-CimInstance Win32_Process -Filter "ProcessId=$($occupied[0].OwningProcess)"
    if ($existing.CommandLine -and $existing.CommandLine.Contains($voiceServer) -and $existing.CommandLine.Contains($voiceRoot)) {
        Write-Output "Local voice is already running: http://127.0.0.1:$Port/"
        exit 0
    }
    throw "Port $Port is occupied by another service."
}
$voiceArguments = @('-u', ('"'+$voiceServer+'"'), '--runtime', ('"'+$voiceRoot+'"'), '--port', $Port)
if ($NoPredictorGraph) { $voiceArguments += '--no-predictor-graph' }
$voiceProcess = Start-Process -FilePath $voicePython -ArgumentList $voiceArguments `
    -WorkingDirectory $voiceRoot -WindowStyle Hidden -PassThru `
    -RedirectStandardOutput (Join-Path $voiceRoot 'stdout.log') -RedirectStandardError (Join-Path $voiceRoot 'stderr.log')
Write-Output "Local voice started (PID $($voiceProcess.Id)); loading model. Open http://127.0.0.1:$Port/ when ready."
