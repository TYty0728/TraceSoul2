param([Parameter(Mandatory=$true)][string]$RuntimeDirectory)
$ErrorActionPreference = 'Stop'
$pidFile = Join-Path $RuntimeDirectory 'panel.pid'
if (!(Test-Path -LiteralPath $pidFile)) { return }
$panelPid = [int](Get-Content -LiteralPath $pidFile)
$process = Get-CimInstance Win32_Process -Filter "ProcessId=$panelPid" -ErrorAction SilentlyContinue
$scriptPath = Join-Path $PSScriptRoot 'bridge.py'
if ($process) {
    if ($process.Name -notmatch '^python(w)?\.exe$' -or !$process.CommandLine.Contains($scriptPath)) {
        throw 'PID belongs to another process; refusing to stop it'
    }
    Stop-Process -Id $panelPid
}
Remove-Item -LiteralPath $pidFile
