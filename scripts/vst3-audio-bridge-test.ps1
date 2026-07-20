param(
    [Parameter(Mandatory = $true)]
    [string] $ModulePath,

    [Parameter(Mandatory = $true)]
    [string] $ClassId,

    [Parameter(Mandatory = $true)]
    [string] $Name,

    [string] $Vendor = "Unknown",
    [string] $Version = "",
    [string] $SdkVersion = "",
    [string] $SubCategories = "Fx",
    [int] $Frames = 256,
    [int] $Blocks = 1,
    [switch] $OpenEditor,
    [ValidateSet("Debug", "Release")]
    [string] $BuildConfiguration = "Debug"
)

$ErrorActionPreference = "Stop"

$executable = Resolve-Path (
    Join-Path $PSScriptRoot (
        "..\bin\$BuildConfiguration\net10.0-windows10.0.19041.0\DrumPracticeStudio.exe"
    )
)
$root = Join-Path ([System.IO.Path]::GetTempPath()) (
    "DrumPracticeStudio.Vst3BridgeTest." + [Guid]::NewGuid().ToString("N")
)
$null = New-Item -ItemType Directory -Path $root
$configurationPath = Join-Path $root "configuration.json"
$readyPath = Join-Path $root "ready.json"
$diagnosticPath = Join-Path $root "diagnostic.log"
$clientDiagnosticPath = Join-Path $root "client.log"
$pipeName = "DrumPracticeStudio.Vst3BridgeTest." + [Guid]::NewGuid().ToString("N")
$pipe = [System.IO.Pipes.NamedPipeServerStream]::new(
    $pipeName,
    [System.IO.Pipes.PipeDirection]::InOut,
    1,
    [System.IO.Pipes.PipeTransmissionMode]::Byte,
    [System.IO.Pipes.PipeOptions]::Asynchronous
)
$process = $null

try {
    function Write-ClientStage([string] $message) {
        Add-Content -LiteralPath $clientDiagnosticPath -Value (
            "{0:O} {1}" -f [DateTimeOffset]::Now, $message
        )
    }

    Write-ClientStage "Preparando configuración."
    $configuration = [ordered]@{
        Effect = [ordered]@{
            ModulePath = $ModulePath
            ModuleName = [System.IO.Path]::GetFileNameWithoutExtension($ModulePath)
            ClassId = $ClassId
            Category = "Audio Module Class"
            Name = $Name
            Vendor = $Vendor
            Version = $Version
            SdkVersion = $SdkVersion
            SubCategories = $SubCategories
            PresetPath = $null
        }
        SampleRate = 48000
        MaximumBlockFrames = 512
        ReadyPath = $readyPath
        DiagnosticPath = $diagnosticPath
        StatePath = $null
        PipeName = $pipeName
    }
    $configuration |
        ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath $configurationPath -Encoding UTF8

    $connectTask = $pipe.WaitForConnectionAsync()
    Write-ClientStage "Tubería a la espera."
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $executable
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.Arguments = '--vst3-effect-runtime "{0}"' -f $configurationPath
    $process = [System.Diagnostics.Process]::Start($startInfo)
    Write-ClientStage "Proceso iniciado."

    $deadline = [DateTime]::UtcNow.AddSeconds(35)
    while (-not (Test-Path -LiteralPath $readyPath)) {
        if ($process.HasExited) {
            throw "El proceso VST3 terminó durante el arranque con código $($process.ExitCode)."
        }
        if ([DateTime]::UtcNow -ge $deadline) {
            throw "El proceso VST3 no publicó su estado de arranque."
        }
        Start-Sleep -Milliseconds 40
    }

    $ready = Get-Content -LiteralPath $readyPath -Raw | ConvertFrom-Json
    if (-not $ready.Ready) {
        throw $ready.Message
    }
    Write-ClientStage "Plugin preparado."
    if (-not $connectTask.Wait(10000)) {
        throw "El proceso VST3 no conectó la tubería de audio."
    }
    Write-ClientStage "Tubería conectada."

    $encoding = [System.Text.UTF8Encoding]::new($false)
    $writer = [System.IO.BinaryWriter]::new($pipe, $encoding, $true)
    $reader = [System.IO.BinaryReader]::new($pipe, $encoding, $true)
    Write-ClientStage "Lectores binarios creados."

    if ($OpenEditor) {
        $writer.Write([int]-1)
        $writer.Flush()
        $editorOpened = $reader.ReadBoolean()
        $editorMessage = $reader.ReadString()
        if (-not $editorOpened) {
            throw "No se pudo abrir el editor: $editorMessage"
        }
    }

    [double] $phase = 0
    [double] $peak = 0
    for ($block = 0; $block -lt $Blocks; $block++) {
        if ($process.HasExited) {
            throw "El proceso VST3 terminó antes del bloque $block con código $($process.ExitCode)."
        }

        $inputSamples = [single[]]::new($Frames * 2)
        for ($frame = 0; $frame -lt $Frames; $frame++) {
            # The physical guitar input is mono. The bridge receives the same sample in L/R
            # only so a stereo-only effect can consume it without involving another input.
            $sample = [single](0.01 * [Math]::Sin($phase))
            $phase += 2 * [Math]::PI * 220 / 48000
            $inputSamples[$frame * 2] = $sample
            $inputSamples[($frame * 2) + 1] = $sample
        }
        $inputBytes = [byte[]]::new($inputSamples.Length * 4)
        [System.Buffer]::BlockCopy($inputSamples, 0, $inputBytes, 0, $inputBytes.Length)
        $writer.Write([int]$Frames)
        $writer.Write($inputBytes)
        $writer.Flush()

        $outputFrames = $reader.ReadInt32()
        if ($outputFrames -ne $Frames) {
            throw "El efecto devolvió $outputFrames frames; se esperaban $Frames."
        }
        $outputBytes = $reader.ReadBytes($outputFrames * 2 * 4)
        if ($outputBytes.Length -ne ($outputFrames * 2 * 4)) {
            throw "La respuesta de audio llegó incompleta en el bloque $block."
        }
        $outputSamples = [single[]]::new($outputFrames * 2)
        [System.Buffer]::BlockCopy($outputBytes, 0, $outputSamples, 0, $outputBytes.Length)
        foreach ($sample in $outputSamples) {
            if ([single]::IsNaN($sample) -or [single]::IsInfinity($sample)) {
                throw "El efecto produjo una muestra no finita en el bloque $block."
            }
            $peak = [Math]::Max($peak, [Math]::Abs($sample))
        }
    }
    if ($peak -gt 8) {
        throw "El efecto produjo un pico inseguro: $peak."
    }

    $writer.Write([int]0)
    $writer.Flush()
    Write-Output (
        "VST3_OK Name='{0}' Frames={1} Blocks={2} Peak={3:N6} Editor={4} Latency={5}" -f
        $Name,
        $Frames,
        $Blocks,
        $peak,
        $ready.HasEditor,
        $ready.LatencySamples
    )
}
finally {
    if ($process -and -not $process.HasExited) {
        if (-not $process.WaitForExit(3000)) {
            $process.Kill($true)
        }
    }
    if ($process) {
        $process.Dispose()
    }
    $pipe.Dispose()
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}
