param(
    [switch]$IncludeUntracked
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$selfPath = [IO.Path]::GetFullPath($PSCommandPath)

$files = @()
if ((Test-Path -LiteralPath (Join-Path $repoRoot '.git')) -and -not $IncludeUntracked) {
    $files = @(git -C $repoRoot ls-files | ForEach-Object { Join-Path $repoRoot $_ })
} else {
    $files = @(Get-ChildItem -LiteralPath $repoRoot -Recurse -Force -File |
        Where-Object { $_.FullName -notmatch '\\(bin|obj|\.git|artifacts|\.release)\\' } |
        Select-Object -ExpandProperty FullName)
}

$forbiddenNames = @(
    'llm-providers.json', 'onebot.json', 'home.json', 'debug-mode.json',
    'bodies.json', 'memory-nerve.json'
)
$forbiddenExtensions = @('.sqlite', '.sqlite3', '.db', '.pem', '.pfx', '.p12', '.key')
$credentialPatterns = [ordered]@{
    'OpenAI-style key' = '(?i)\bsk-[A-Za-z0-9_-]{16,}'
    'GitHub token' = '\bgh[pousr]_[A-Za-z0-9]{20,}'
    'Google API key' = '\bAIza[0-9A-Za-z_-]{20,}'
    'JWT' = '\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}'
    'private key' = '-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----'
    'credential URL' = '(?i)https?://[^\s/:]+:[^\s/@]+@'
}

$findings = [Collections.Generic.List[object]]::new()
foreach ($filePath in $files) {
    if (-not (Test-Path -LiteralPath $filePath) -or [IO.Path]::GetFullPath($filePath) -eq $selfPath) { continue }
    $item = Get-Item -LiteralPath $filePath
    if ($forbiddenNames -contains $item.Name.ToLowerInvariant() -and
        $item.Name -ne 'llm-providers.example.json') {
        $findings.Add([pscustomobject]@{ File=$filePath; Line=0; Kind='runtime configuration' })
    }
    if ($forbiddenExtensions -contains $item.Extension.ToLowerInvariant()) {
        $findings.Add([pscustomobject]@{ File=$filePath; Line=0; Kind='database or private key file' })
    }
    if ($item.Length -gt 5MB -or $item.Extension -notmatch '^\.(cs|csproj|props|targets|json|md|txt|html|js|css|ps1|bat|yml|yaml|xml|config|toml)$') {
        continue
    }
    $lineNumber = 0
    foreach ($line in [IO.File]::ReadLines($filePath)) {
        $lineNumber++
        foreach ($entry in $credentialPatterns.GetEnumerator()) {
            if ([regex]::IsMatch($line, $entry.Value)) {
                $findings.Add([pscustomobject]@{ File=$filePath; Line=$lineNumber; Kind=$entry.Key })
            }
        }
    }
}

if ($findings.Count -gt 0) {
    $findings | Sort-Object File,Line,Kind | Format-Table -AutoSize
    throw "发布安全检查失败：发现 $($findings.Count) 个疑似敏感项。输出已隐藏具体值。"
}

Write-Host "发布安全检查通过：扫描 $($files.Count) 个文件，没有数据库、私钥或已知凭据格式。"
