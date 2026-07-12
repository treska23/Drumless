param(
    [Parameter(Mandatory = $true)]
    [string] $InstallRoot
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$uvVersion = "0.11.28"
$uvRoot = Join-Path $InstallRoot "uv"
$uvExe = Join-Path $uvRoot "uv.exe"
$venvRoot = Join-Path $InstallRoot ".venv"
$pythonExe = Join-Path $venvRoot "Scripts\python.exe"

New-Item -ItemType Directory -Path $InstallRoot -Force | Out-Null
New-Item -ItemType Directory -Path $uvRoot -Force | Out-Null

if (-not (Test-Path -LiteralPath $uvExe)) {
    Write-Output "Descargando el gestor de entorno uv $uvVersion"
    $archive = Join-Path $InstallRoot "uv.zip"
    $checksumFile = Join-Path $InstallRoot "uv.zip.sha256"
    $baseUrl = "https://github.com/astral-sh/uv/releases/download/$uvVersion/uv-x86_64-pc-windows-msvc.zip"
    Invoke-WebRequest -Uri $baseUrl -OutFile $archive -UseBasicParsing
    Invoke-WebRequest -Uri "$baseUrl.sha256" -OutFile $checksumFile -UseBasicParsing

    $expected = ((Get-Content -LiteralPath $checksumFile -Raw).Trim() -split "\s+")[0].ToLowerInvariant()
    $actual = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($expected -ne $actual) {
        throw "La suma SHA-256 de uv no coincide."
    }

    Expand-Archive -LiteralPath $archive -DestinationPath $uvRoot -Force
    Remove-Item -LiteralPath $archive, $checksumFile -Force
}

$env:UV_PYTHON_INSTALL_DIR = Join-Path $InstallRoot "python"
$env:UV_CACHE_DIR = Join-Path $InstallRoot "uv-cache"
$env:UV_PYTHON_NO_REGISTRY = "1"

if (-not (Test-Path -LiteralPath $pythonExe)) {
    Write-Output "Creando un Python 3.11 aislado"
    & $uvExe venv $venvRoot --python 3.11 --managed-python
    if ($LASTEXITCODE -ne 0) { throw "uv no pudo crear el entorno Python." }
}

Write-Output "Instalando PyTorch CPU"
& $uvExe pip install --python $pythonExe --index-url https://download.pytorch.org/whl/cpu `
    torch==2.0.1 torchaudio==2.0.2
if ($LASTEXITCODE -ne 0) { throw "No se pudo instalar PyTorch CPU." }

Write-Output "Instalando Demucs 4.0.1"
& $uvExe pip install --python $pythonExe "numpy<2" "soundfile==0.13.1" "demucs==4.0.1"
if ($LASTEXITCODE -ne 0) { throw "No se pudo instalar Demucs." }

Write-Output "Verificando el motor"
& $pythonExe -c "import demucs, torch, torchaudio; backends=torchaudio.list_audio_backends(); assert backends, 'Torchaudio no tiene backend'; print('Demucs listo; torch=' + torch.__version__ + '; backends=' + ','.join(backends))"
if ($LASTEXITCODE -ne 0) { throw "La verificación de Demucs falló." }

[pscustomobject]@{
    installed = $true
    python = $pythonExe
    demucs = "4.0.1"
    torch = "2.0.1+cpu"
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $InstallRoot "engine.json") -Encoding UTF8

Write-Output "Motor Demucs instalado correctamente"
