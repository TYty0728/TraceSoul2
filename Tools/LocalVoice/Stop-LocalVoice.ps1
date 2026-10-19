param([Parameter(Mandatory=$true)][string]$RuntimeDirectory)
$ErrorActionPreference = 'Stop'
$voiceRoot = (Resolve-Path -LiteralPath $RuntimeDirectory).Path
$voiceServer = Join-Path $voiceRoot 'app/server.py'
$voiceProcesses = Get-CimInstance Win32_Process | Where-Object {
    $_.Name -eq 'python.exe' -and $_.CommandLine -and
    $_.CommandLine.Contains($voiceServer) -and $_.CommandLine.Contains($voiceRoot)
}
foreach ($voiceProcess in $voiceProcesses) { Stop-Process -Id $voiceProcess.ProcessId }
Write-Output 'Local voice stopped. GPU model memory is released after the process exits.'
