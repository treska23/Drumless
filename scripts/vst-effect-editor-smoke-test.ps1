param(
    [Parameter(Mandatory = $true)]
    [string] $ExecutablePath,
    [Parameter(Mandatory = $true)]
    [string] $ModulePath
)

$ErrorActionPreference = "Stop"
$executable = (Resolve-Path -LiteralPath $ExecutablePath).Path
$module = (Resolve-Path -LiteralPath $ModulePath).Path
$testRoot = Join-Path ([IO.Path]::GetTempPath()) (
    "DrumPracticeStudio.EffectEditorTest." + [Guid]::NewGuid().ToString("N")
)
[IO.Directory]::CreateDirectory($testRoot) | Out-Null

$runtime = $null
try {
    $probePath = Join-Path $testRoot "probe.json"
    $probeStart = [Diagnostics.ProcessStartInfo]::new($executable)
    $probeStart.UseShellExecute = $false
    $probeStart.CreateNoWindow = $true
    $probeStart.Arguments = "--vst3-probe `"$module`" `"$probePath`""
    $probe = [Diagnostics.Process]::Start($probeStart)
    if (-not $probe.WaitForExit(30000) -or $probe.ExitCode -ne 0) {
        throw "El sondeo aislado del módulo VST3 falló."
    }

    $effect = @(Get-Content -LiteralPath $probePath -Raw | ConvertFrom-Json) |
        Where-Object { -not $_.IsInstrument } |
        Select-Object -First 1
    if (-not $effect) {
        throw "El módulo no expone ningún efecto de audio."
    }

    $readyPath = Join-Path $testRoot "ready.json"
    $diagnosticPath = Join-Path $testRoot "effect.log"
    $statePath = Join-Path $testRoot "state.vstpreset"
    $configurationPath = Join-Path $testRoot "configuration.json"
    $configuration = @{
        Effect = @{
            ModulePath = $module
            ModuleName = [IO.Path]::GetFileNameWithoutExtension($module)
            ClassId = [string] $effect.ClassId
            Category = [string] $effect.Category
            Name = [string] $effect.Name
            Vendor = [string] $effect.Vendor
            Version = [string] $effect.Version
            SdkVersion = [string] $effect.SdkVersion
            SubCategories = [string] $effect.SubCategories
            PresetPath = $null
        }
        SampleRate = 48000
        MaximumBlockFrames = 64
        ReadyPath = $readyPath
        DiagnosticPath = $diagnosticPath
        StatePath = $statePath
    }
    [IO.File]::WriteAllText(
        $configurationPath,
        ($configuration | ConvertTo-Json -Depth 5)
    )

    $runtimeStart = [Diagnostics.ProcessStartInfo]::new($executable)
    $runtimeStart.UseShellExecute = $false
    $runtimeStart.CreateNoWindow = $true
    $runtimeStart.RedirectStandardInput = $true
    $runtimeStart.RedirectStandardOutput = $true
    $runtimeStart.RedirectStandardError = $true
    $runtimeStart.Arguments = "--vst3-effect-runtime `"$configurationPath`""
    $runtime = [Diagnostics.Process]::Start($runtimeStart)

    $deadline = [DateTime]::UtcNow.AddSeconds(35)
    while (-not (Test-Path -LiteralPath $readyPath)) {
        if ($runtime.HasExited) {
            throw "El runtime del efecto terminó antes de quedar preparado."
        }
        if ([DateTime]::UtcNow -ge $deadline) {
            throw "El runtime del efecto no respondió durante el arranque."
        }
        Start-Sleep -Milliseconds 40
    }

    $ready = Get-Content -LiteralPath $readyPath -Raw | ConvertFrom-Json
    if (-not $ready.Ready) {
        throw "El efecto no se pudo cargar: $($ready.Message)"
    }
    if (-not $ready.HasEditor) {
        throw "El efecto $($effect.Name) no expone una interfaz compatible."
    }

    $writer = [IO.BinaryWriter]::new($runtime.StandardInput.BaseStream)
    $reader = [IO.BinaryReader]::new($runtime.StandardOutput.BaseStream)
    $writer.Write([int] -1)
    $writer.Flush()
    $opened = $reader.ReadBoolean()
    $openMessage = $reader.ReadString()
    if (-not $opened) {
        throw $openMessage
    }

    $writer.Write([int] -2)
    $writer.Flush()
    $closed = $reader.ReadBoolean()
    $closeMessage = $reader.ReadString()
    if (-not $closed) {
        throw $closeMessage
    }

    $writer.Write([int] 0)
    $writer.Flush()
    $writer.Dispose()
    $reader.Dispose()
    if (-not $runtime.WaitForExit(10000)) {
        throw "El runtime no se cerró después de cerrar el editor."
    }
    if (-not (Test-Path -LiteralPath $statePath)) {
        throw "El runtime no guardó el estado del efecto."
    }

    [pscustomobject]@{
        Effect = [string] $effect.Name
        IsInstrument = [bool] $effect.IsInstrument
        HasEditor = [bool] $ready.HasEditor
        OpenResult = $openMessage
        CloseResult = $closeMessage
        StateSaved = $true
        ExitCode = $runtime.ExitCode
    }
}
finally {
    if ($runtime -and -not $runtime.HasExited) {
        $runtime.Kill($true)
        $runtime.WaitForExit(3000) | Out-Null
    }
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
    if ($resolvedTestRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolvedTestRoot).StartsWith(
            "DrumPracticeStudio.EffectEditorTest.",
            [StringComparison]::Ordinal)) {
        [IO.Directory]::Delete($resolvedTestRoot, $true)
    }
}
