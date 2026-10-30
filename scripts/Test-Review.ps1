param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
Push-Location (Join-Path $PSScriptRoot '..')
try {
    function Run-Dotnet([string[]]$Arguments) {
        & dotnet @Arguments
        if ($LASTEXITCODE -ne 0) { throw "Review regression failed (exit $LASTEXITCODE): $Arguments" }
    }
    Run-Dotnet @('build', 'Tools/ChatCheck/ChatCheck.csproj', '-c', $Configuration, '--nologo')
    Run-Dotnet @('build', 'Tools/Migration/TraceSoul2.Migrate.csproj', '-c', $Configuration, '--nologo')
    $check = "Tools/ChatCheck/bin/$Configuration/net8.0/ChatCheck.dll"
    foreach ($suite in @('--review-reliability', '--review-selection', '--day-overview', '--failure-protection', '--identity-evolution')) {
        Run-Dotnet @($check, $suite)
    }
    Run-Dotnet @($check, '--cognition-migration', "Tools/Migration/bin/$Configuration/net8.0/TraceSoul2.Migrate.dll")
} finally { Pop-Location }
