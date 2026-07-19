param(
    [Parameter(Mandatory = $true)]
    [int] $ProcessId
)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$process = Get-Process -Id $ProcessId -ErrorAction Stop
$main = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)

function Find-ById(
    [System.Windows.Automation.AutomationElement] $root,
    [string] $automationId
) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $automationId
    )
    return $root.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        $condition
    )
}

function Find-ByName(
    [System.Windows.Automation.AutomationElement] $root,
    [string] $name
) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        $name
    )
    return $root.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        $condition
    )
}

function Find-ButtonContaining(
    [System.Windows.Automation.AutomationElement] $root,
    [string] $text
) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button
    )
    return $root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        $condition
    ) | Where-Object { $_.Current.Name -like "*$text*" } | Select-Object -First 1
}

function Invoke-Control([System.Windows.Automation.AutomationElement] $element) {
    if (-not $element) {
        throw "No se encontró el control que debía invocarse."
    }
    $element.GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern
    ).Invoke()
    Start-Sleep -Milliseconds 350
}

Invoke-Control (Find-ButtonContaining $main "Pistas locales")
$library = Find-ById $main "TrackLibraryList"
if (-not $library) {
    throw "No apareció la biblioteca local."
}
$itemCondition = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::ListItem
)
$track = $library.FindFirst(
    [System.Windows.Automation.TreeScope]::Children,
    $itemCondition
)
if (-not $track) {
    throw "La biblioteca temporal no contiene la pista de prueba."
}
$track.GetCurrentPattern(
    [System.Windows.Automation.SelectionItemPattern]::Pattern
).Select()
Invoke-Control (Find-ById $main "LoadLibrarySelectionButton")
Start-Sleep -Seconds 1

Invoke-Control (Find-ButtonContaining $main "Practicar")
$open = Find-ById $main "OpenChordSheetButton"
if (-not $open -or -not $open.Current.IsEnabled) {
    throw "El botón de letra y acordes no se habilitó al cargar la pista."
}
Invoke-Control $open

$desktop = [System.Windows.Automation.AutomationElement]::RootElement
$windowCondition = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::Window
)
$deadline = [DateTime]::UtcNow.AddSeconds(8)
$chordWindow = $null
do {
    $windows = $desktop.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        $windowCondition
    )
    $chordWindow = $windows |
        Where-Object { $_.Current.Name -like "Letra y acordes*" } |
        Select-Object -First 1
    if (-not $chordWindow) {
        Start-Sleep -Milliseconds 200
    }
} while (-not $chordWindow -and [DateTime]::UtcNow -lt $deadline)

if (-not $chordWindow) {
    throw "No se abrió la ventana flotante de letra y acordes."
}

$required = @(
    "ChordSheetAddressBox",
    "ExtractChordSheetButton",
    "ChordSheetWebView",
    "ChordSheetSourceList",
    "ProcessChordSheetCandidateButton"
)
foreach ($automationId in $required) {
    if (-not (Find-ById $chordWindow $automationId)) {
        throw "La ventana de letra y acordes no expuso $automationId."
    }
}

$editTab = Find-ByName $chordWindow "EDITAR TEXTO"
if (-not $editTab) {
    throw "No apareció la pestaña para editar texto."
}
$editTab.GetCurrentPattern(
    [System.Windows.Automation.SelectionItemPattern]::Pattern
).Select()
Start-Sleep -Milliseconds 250
$editor = Find-ById $chordWindow "ChordSheetEditor"
if (-not $editor) {
    throw "No apareció el editor de letra y acordes."
}
$editor.GetCurrentPattern(
    [System.Windows.Automation.ValuePattern]::Pattern
).SetValue("[Verse]`r`nC G`r`nTexto de prueba")
Invoke-Control (Find-ByName $chordWindow "Procesar y guardar")

$followTab = Find-ByName $chordWindow "SEGUIR"
$followTab.GetCurrentPattern(
    [System.Windows.Automation.SelectionItemPattern]::Pattern
).Select()
Start-Sleep -Milliseconds 250
$lineList = Find-ById $chordWindow "ChordSheetLineList"
$lines = $lineList.FindAll(
    [System.Windows.Automation.TreeScope]::Children,
    $itemCondition
)
if ($lines.Count -lt 3) {
    throw "El lector no mostró las líneas procesadas."
}

[pscustomobject]@{
    MainWindow = $main.Current.Name
    ChordWindow = $chordWindow.Current.Name
    ControlsVerified = $required.Count
    ParsedLines = $lines.Count
}
