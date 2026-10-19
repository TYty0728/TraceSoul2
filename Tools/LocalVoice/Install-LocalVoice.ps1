param([Parameter(Mandatory=$true)][string]$RuntimeDirectory)
$ErrorActionPreference = 'Stop'
$voiceRoot = [IO.Path]::GetFullPath($RuntimeDirectory)
New-Item -ItemType Directory -Path $voiceRoot -Force | Out-Null
$bootstrap = Join-Path $voiceRoot 'bootstrap'
python -m pip install --disable-pip-version-check --target $bootstrap uv
if ($LASTEXITCODE -ne 0) { throw 'uv installation failed' }
$env:PYTHONPATH = $bootstrap
$env:UV_CACHE_DIR = Join-Path $voiceRoot 'cache/uv'
$env:UV_PYTHON_INSTALL_DIR = Join-Path $voiceRoot 'python'
python -m uv python install 3.12
if ($LASTEXITCODE -ne 0) { throw 'Python installation failed' }
$voicePython = Join-Path $voiceRoot 'venv/Scripts/python.exe'
if (!(Test-Path -LiteralPath $voicePython)) {
    python -m uv venv --python 3.12 (Join-Path $voiceRoot 'venv')
    if ($LASTEXITCODE -ne 0) { throw 'Python environment creation failed' }
}
python -m uv pip install --python $voicePython torch==2.8.0 torchaudio==2.8.0 --index-url https://download.pytorch.org/whl/cu128
if ($LASTEXITCODE -ne 0) { throw 'GPU runtime installation failed' }
python -m uv pip install --python $voicePython -r (Join-Path $PSScriptRoot 'requirements.txt')
if ($LASTEXITCODE -ne 0) { throw 'Voice dependencies installation failed' }
$env:HF_HUB_DISABLE_XET = '1'
& $voicePython (Join-Path $PSScriptRoot 'download_model.py') --runtime $voiceRoot
if ($LASTEXITCODE -ne 0) { throw 'Model download failed' }
$voiceApp = Join-Path $voiceRoot 'app'
New-Item -ItemType Directory -Path $voiceApp -Force | Out-Null
foreach ($name in @('server.py','wake_cache.py','voice_lab.py','predictor_graph.py','access.py','configure_access.py','launch.py','index.html','Start-LocalVoice.ps1','Stop-LocalVoice.ps1','benchmark.py','README.md')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $voiceApp $name) -Force
}
python -m uv pip freeze --python $voicePython | Set-Content -LiteralPath (Join-Path $voiceRoot 'requirements-lock.txt') -Encoding utf8
Write-Output 'Installed. Start app/Start-LocalVoice.ps1 with the same RuntimeDirectory.'
