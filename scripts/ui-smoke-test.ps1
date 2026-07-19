param(
    [string] $ProcessName = "DrumPracticeStudio",
    [int] $ProcessId = 0,
    [switch] $VerifyVstScanIndicator,
    [switch] $SkipAudioTrigger,
    [int] $ExpectedVstCatalogMinimum = 0
)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System.Runtime.InteropServices;
public static class NativeMouse {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(
        uint flags, uint dx, uint dy, uint data, System.UIntPtr extraInfo);
}
"@

$process = if ($ProcessId -gt 0) {
    Get-Process -Id $ProcessId -ErrorAction Stop
} else {
    Get-Process -Name $ProcessName -ErrorAction Stop |
        Where-Object { $_.MainWindowHandle -ne 0 } |
        Select-Object -First 1
}

if (-not $process) {
    throw "No se encontró la ventana de $ProcessName."
}

$root = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)

function Find-ElementByName([string] $name) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        $name
    )
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Find-ElementByAutomationId([string] $automationId) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $automationId
    )
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Invoke-Element([System.Windows.Automation.AutomationElement] $element) {
    if (-not $element) {
        throw "No se encontró el control solicitado."
    }

    $pattern = $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
    Start-Sleep -Milliseconds 250
}

$buttonsCondition = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::Button
)
$buttons = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $buttonsCondition)
$names = @($buttons | ForEach-Object { $_.Current.Name })
Write-Output ("BOTONES: " + ($names -join " | "))

$practice = $buttons | Where-Object { $_.Current.Name -like "*Practicar" } | Select-Object -First 1
Invoke-Element $practice

$requiredPracticeControls = @(
    "StartOutputRecordingButton",
    "StopOutputRecordingButton",
    "PlayLastRecordingButton",
    "AnalyzeTempoButton",
    "OpenChordSheetButton",
    "TempoBpmTextBox",
    "TempoFirstBeatTextBox",
    "MetronomeEnabledCheckBox",
    "MetronomeVolumeSlider",
    "PerformanceLatencyTextBox",
    "StartPerformanceButton",
    "FinishPerformanceButton",
    "ClearAnalysisDataButton",
    "PerformanceHistoryText",
    "TrackVolumeFader"
)
foreach ($automationId in $requiredPracticeControls) {
    if (-not (Find-ElementByAutomationId $automationId)) {
        throw "La página de práctica no expuso el control $automationId."
    }
}

$trackFader = Find-ElementByAutomationId "TrackVolumeFader"
$trackRange = $trackFader.GetCurrentPattern(
    [System.Windows.Automation.RangeValuePattern]::Pattern
)
$trackRange.SetValue(0.5)
Start-Sleep -Milliseconds 150
$faderBounds = $trackFader.Current.BoundingRectangle
$faderX = [int]($faderBounds.Left + ($faderBounds.Width / 2))
$railClickY = [int]($faderBounds.Top + ($faderBounds.Height * 0.25))
[NativeMouse]::SetCursorPos($faderX, $railClickY) | Out-Null
[NativeMouse]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
[NativeMouse]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 200
$railClickValue = $trackRange.Current.Value
if ($railClickValue -le 0.6 -or $railClickValue -ge 0.95) {
    throw "El clic en el rail produjo un valor incorrecto: $railClickValue."
}

$trackRange.SetValue(0.5)
Start-Sleep -Milliseconds 150
$dragStartY = [int]($faderBounds.Top + ($faderBounds.Height / 2))
$dragEndY = [int]($faderBounds.Top + ($faderBounds.Height * 0.25))
[NativeMouse]::SetCursorPos($faderX, $dragStartY) | Out-Null
[NativeMouse]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 100
[NativeMouse]::SetCursorPos($faderX, $dragEndY) | Out-Null
Start-Sleep -Milliseconds 150
[NativeMouse]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 200
$draggedFaderValue = $trackRange.Current.Value
if ($draggedFaderValue -le 0.6 -or $draggedFaderValue -ge 0.95) {
    throw "El tirador del volumen de pista no respondió al arrastre: $draggedFaderValue."
}

$buttons = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $buttonsCondition)
Write-Output ("TRAS PRACTICAR: " + (@($buttons | ForEach-Object { $_.Current.Name }) -join " | "))
$kick = $buttons | Where-Object { $_.Current.Name -eq "Bombo" } | Select-Object -First 1
if (-not $SkipAudioTrigger) {
    Invoke-Element $kick
}

$buttons = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $buttonsCondition)
$libraries = $buttons | Where-Object { $_.Current.Name -like "*Librer*" } | Select-Object -First 1
Invoke-Element $libraries

$factory = Find-ElementByName "Factory Sessions"
if (-not $factory) {
    throw "La página de librerías no mostró Factory Sessions."
}

$buttons = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $buttonsCondition)
$tracksPage = $buttons | Where-Object { $_.Current.Name -like "*Pistas locales" } | Select-Object -First 1
Invoke-Element $tracksPage

$requiredTrackControls = @(
    "OutputFolderPathText",
    "ChooseOutputFolderButton",
    "RescanLibraryButton",
    "LibrarySearchTextBox",
    "LibrarySortComboBox",
    "TrackLibraryList",
    "LoadLibrarySelectionButton",
    "AddLibrarySelectionButton",
    "RemoveLibrarySelectionButton",
    "PlaylistSelector",
    "PlaylistSearchTextBox",
    "PlaylistItemSearchTextBox",
    "PlaylistNameTextBox",
    "NewPlaylistButton",
    "RenamePlaylistButton",
    "DeletePlaylistButton",
    "PlayPlaylistSelectionButton",
    "MovePlaylistSelectionUpButton",
    "MovePlaylistSelectionDownButton",
    "RemovePlaylistSelectionButton",
    "OpenPlaylistWindowButton",
    "PlaylistMixList",
    "ClearPlaylistMixButton",
    "PlayPlaylistQueueButton",
    "PlaybackModeCombo",
    "CreateStemMixButton",
    "KeepDrumsCheckBox",
    "KeepBassCheckBox",
    "KeepVocalsCheckBox",
    "KeepGuitarCheckBox",
    "KeepPianoCheckBox",
    "KeepOtherCheckBox"
)
foreach ($automationId in $requiredTrackControls) {
    if (-not (Find-ElementByAutomationId $automationId)) {
        throw "La página de pistas no expuso el control $automationId."
    }
}

$libraryList = Find-ElementByAutomationId "TrackLibraryList"
$librarySearch = Find-ElementByAutomationId "LibrarySearchTextBox"
$listItemCondition = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::ListItem
)
$initialLibraryItems = $libraryList.FindAll(
    [System.Windows.Automation.TreeScope]::Children,
    $listItemCondition
).Count
$searchValue = $librarySearch.GetCurrentPattern(
    [System.Windows.Automation.ValuePattern]::Pattern
)
$searchValue.SetValue("__codex_sin_resultados_9f731__")
Start-Sleep -Milliseconds 450
$filteredLibraryItems = $libraryList.FindAll(
    [System.Windows.Automation.TreeScope]::Children,
    $listItemCondition
).Count
if ($filteredLibraryItems -ne 0) {
    throw "La búsqueda de la biblioteca no filtró una consulta sin coincidencias."
}
$searchValue.SetValue("")
Start-Sleep -Milliseconds 300

$buttons = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $buttonsCondition)
$youtubePage = $buttons | Where-Object { $_.Current.Name -like "*YouTube" } | Select-Object -First 1
Invoke-Element $youtubePage

$requiredYouTubeControls = @(
    "YouTubeSearchTextBox",
    "YouTubeSearchButton",
    "YouTubeBackButton",
    "AddYouTubeToPlaylistButton",
    "ImportYouTubePlaylistButton",
    "YouTubeWebView"
)
foreach ($automationId in $requiredYouTubeControls) {
    if (-not (Find-ElementByAutomationId $automationId)) {
        throw "La página de YouTube no expuso el control $automationId."
    }
}

$buttons = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $buttonsCondition)
$devices = $buttons | Where-Object { $_.Current.Name -like "*Dispositivos" } | Select-Object -First 1
Invoke-Element $devices

if (-not (Find-ElementByAutomationId "AudioInputMonitorList")) {
    throw "La página de dispositivos no expuso la mezcla multientrada."
}
if (-not (Find-ElementByAutomationId "ScanVstEffectsButton") -or
    -not (Find-ElementByAutomationId "ChooseVstEffectFolderButton")) {
    throw "La página de dispositivos no expuso la búsqueda e importación de plugins VST3."
}
if (-not (Find-ElementByAutomationId "Vst3EffectCatalogPicker") -or
    -not (Find-ElementByAutomationId "RemoveVstEffectFromCatalogButton")) {
    throw "La página de dispositivos no expuso el catálogo VST3 persistente."
}
$catalogPicker = Find-ElementByAutomationId "Vst3EffectCatalogPicker"
$catalogItems = @()
if ($ExpectedVstCatalogMinimum -gt 0) {
    $expandPattern = $catalogPicker.GetCurrentPattern(
        [System.Windows.Automation.ExpandCollapsePattern]::Pattern
    )
    $expandPattern.Expand()
    Start-Sleep -Milliseconds 300
    $catalogItems = @($catalogPicker.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        $listItemCondition
    ) | ForEach-Object { $_.Current.Name })
    $expandPattern.Collapse()
    if ($catalogItems.Count -lt $ExpectedVstCatalogMinimum) {
        throw "El catálogo VST3 restauró $($catalogItems.Count) plugins; se esperaban al menos $ExpectedVstCatalogMinimum."
    }
}
if (-not (Find-ElementByAutomationId "AudioInputStatusText")) {
    throw "La página de dispositivos no expuso el estado de búsqueda de plugins VST3."
}

$inputFader = Find-ElementByAutomationId "AudioInputGainFader"
$inputFaderRailClickValue = $null
$inputFaderDraggedValue = $null
if ($inputFader) {
    $inputRange = $inputFader.GetCurrentPattern(
        [System.Windows.Automation.RangeValuePattern]::Pattern
    )
    $inputRange.SetValue(0.5)
    Start-Sleep -Milliseconds 150
    $inputBounds = $inputFader.Current.BoundingRectangle
    $inputX = [int]($inputBounds.Left + ($inputBounds.Width / 2))
    $inputRailY = [int]($inputBounds.Top + ($inputBounds.Height * 0.25))
    [NativeMouse]::SetCursorPos($inputX, $inputRailY) | Out-Null
    [NativeMouse]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    [NativeMouse]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 200
    $inputFaderRailClickValue = $inputRange.Current.Value
    if ($inputFaderRailClickValue -le 0.6 -or $inputFaderRailClickValue -ge 0.95) {
        throw "El clic en el rail del fader de entrada produjo un valor incorrecto: $inputFaderRailClickValue."
    }

    $inputRange.SetValue(0.5)
    Start-Sleep -Milliseconds 150
    $inputDragStartY = [int]($inputBounds.Top + ($inputBounds.Height / 2))
    [NativeMouse]::SetCursorPos($inputX, $inputDragStartY) | Out-Null
    [NativeMouse]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 100
    [NativeMouse]::SetCursorPos($inputX, $inputRailY) | Out-Null
    Start-Sleep -Milliseconds 150
    [NativeMouse]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 200
    $inputFaderDraggedValue = $inputRange.Current.Value
    if ($inputFaderDraggedValue -le 0.6 -or $inputFaderDraggedValue -ge 0.95) {
        throw "El tirador del fader de entrada no respondió al arrastre: $inputFaderDraggedValue."
    }
}

$vstScanIndicatorVerified = $false
if ($VerifyVstScanIndicator) {
    Invoke-Element (Find-ElementByAutomationId "ScanVstEffectsButton")
    $deadline = [DateTime]::UtcNow.AddSeconds(3)
    do {
        $scanStatus = Find-ElementByAutomationId "VstEffectSearchStatusText"
        $scanProgress = Find-ElementByAutomationId "VstEffectSearchProgress"
        if ($scanStatus -and $scanProgress) {
            $vstScanIndicatorVerified = $true
            break
        }
        Start-Sleep -Milliseconds 80
    } while ([DateTime]::UtcNow -lt $deadline)

    if (-not $vstScanIndicatorVerified) {
        throw "La búsqueda VST3 no mostró su indicador de progreso."
    }
}

$allElements = $root.FindAll(
    [System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.Condition]::TrueCondition
)
$mapping = $allElements | Where-Object { $_.Current.Name -like "Perfil General Drums*" } | Select-Object -First 1
if (-not $mapping) {
    throw "La página de dispositivos no mostró el perfil MIDI."
}

$padResult = if ($SkipAudioTrigger) { "Omitido" } else { $kick.Current.Name }
[pscustomobject]@{
    Window = $root.Current.Name
    ButtonsFound = $names.Count
    PadTriggered = $padResult
    PracticeControlsVerified = $requiredPracticeControls.Count
    TrackFaderRailClickValue = $railClickValue
    TrackFaderDraggedValue = $draggedFaderValue
    LibrariesVerified = $factory.Current.Name
    TrackControlsVerified = $requiredTrackControls.Count
    LibraryRowsBeforeSearch = $initialLibraryItems
    LibraryRowsWithoutMatches = $filteredLibraryItems
    YouTubeControlsVerified = $requiredYouTubeControls.Count
    InputFaderRailClickValue = $inputFaderRailClickValue
    InputFaderDraggedValue = $inputFaderDraggedValue
    VstCatalogItems = $catalogItems -join " | "
    VstScanIndicatorVerified = $vstScanIndicatorVerified
    MidiProfileVerified = $mapping.Current.Name
}
