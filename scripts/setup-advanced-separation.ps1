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
$ffmpegRoot = Join-Path $InstallRoot "ffmpeg"
$ffmpegExe = Join-Path $ffmpegRoot "ffmpeg.exe"

New-Item -ItemType Directory -Path $InstallRoot -Force | Out-Null
New-Item -ItemType Directory -Path $uvRoot -Force | Out-Null
New-Item -ItemType Directory -Path $ffmpegRoot -Force | Out-Null

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
    Write-Output "Creando un Python 3.11 aislado para separación avanzada"
    & $uvExe venv $venvRoot --python 3.11 --managed-python
    if ($LASTEXITCODE -ne 0) { throw "uv no pudo crear el entorno Python avanzado." }
}

Write-Output "Instalando Audio Separator 0.44.5, FFmpeg local y analizadores de guitarra"
& $uvExe pip install --python $pythonExe `
    "audio-separator[cpu]==0.44.5" `
    "imageio-ffmpeg==0.6.0" `
    "librosa==0.11.0" `
    "soundfile==0.13.1" `
    "scipy>=1.13,<2"
if ($LASTEXITCODE -ne 0) { throw "No se pudo instalar el motor de separación avanzada." }

Write-Output "Preparando FFmpeg privado"
$ffmpegSource = (& $pythonExe -c "import imageio_ffmpeg; print(imageio_ffmpeg.get_ffmpeg_exe())" | Select-Object -Last 1).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($ffmpegSource) -or -not (Test-Path -LiteralPath $ffmpegSource)) {
    throw "imageio-ffmpeg no devolvió un ejecutable FFmpeg válido."
}
Copy-Item -LiteralPath $ffmpegSource -Destination $ffmpegExe -Force
& $ffmpegExe -version | Select-Object -First 1 | Write-Output
if ($LASTEXITCODE -ne 0) { throw "El FFmpeg privado no pudo ejecutarse." }

Write-Output "Verificando el motor avanzado"
$env:PATH = "$ffmpegRoot;$env:PATH"
& $pythonExe -c "import audio_separator, imageio_ffmpeg, librosa, soundfile, scipy; import subprocess; subprocess.run(['ffmpeg','-version'], check=True, stdout=subprocess.DEVNULL); print('Motor avanzado listo')"
if ($LASTEXITCODE -ne 0) { throw "La verificación del motor avanzado falló." }

[pscustomobject]@{
    installed = $true
    python = $pythonExe
    audioSeparator = "0.44.5"
    ffmpeg = $ffmpegExe
    vocalMode = "UVR karaoke ensemble"
    guitarMode = "melody-mask experimental"
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $InstallRoot "engine.json") -Encoding UTF8

Write-Output "Motor de separación avanzada instalado correctamente"