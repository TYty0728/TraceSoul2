param(
    [Parameter(Mandatory=$true)][string]$Python,
    [Parameter(Mandatory=$true)][string]$ConnectionConfig,
    [Parameter(Mandatory=$true)][string]$RelayConfig,
    [string]$Port = 'COM3'
)
$ErrorActionPreference = 'Stop'
# Stop the existing panel with Stop-Panel before provisioning. Wi-Fi credentials
# remain on the board; this operation writes only its separate relay NVS entry.
& $Python (Join-Path $PSScriptRoot 'provision_mobile.py') --connection-config $ConnectionConfig --relay-config $RelayConfig --port $Port
if ($LASTEXITCODE -ne 0) { throw 'Mobile provisioning failed; retain the private backup and USB recovery connection.' }
