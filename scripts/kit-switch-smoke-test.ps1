param(
    [string] $ProcessName = "DrumPracticeStudio"
)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$process = Get-Process -Name $ProcessName -ErrorAction Stop |
    Where-Object { $_.MainWindowHandle -ne 0 } |
    Select-Object -First 1
$root = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)

function Get-AllElements {
    return $root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition
    )
}

function Find-ByNamePattern([string] $pattern) {
    return Get-AllElements |
        Where-Object { $_.Current.Name -like $pattern } |
        Select-Object -First 1
}

function Invoke-Element([System.Windows.Automation.AutomationElement] $element) {
    if (-not $element) { throw "No se encontró el botón solicitado." }
    $invoke = $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $invoke.Invoke()
    Start-Sleep -Milliseconds 350
}

function Select-Library([string] $name) {
    $label = Find-ByNamePattern $name
    if (-not $label) { throw "No se encontró la librería $name." }

    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $candidate = $label
    while ($candidate -and $candidate.Current.ControlType -ne [System.Windows.Automation.ControlType]::ListItem) {
        $candidate = $walker.GetParent($candidate)
    }
    if (-not $candidate) { throw "No se encontró el elemento seleccionable de $name." }

    $selection = $candidate.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    $selection.Select()
    Start-Sleep -Seconds 2
}

Invoke-Element (Find-ByNamePattern "*Librer*")
Select-Library "Electronic Lab"
$electronicStatus = Find-ByNamePattern "Kit activo: Neon Pulse*"
if (-not $electronicStatus) { throw "Electronic Lab se seleccionó, pero Neon Pulse no quedó activo." }
$electronicStatusText = $electronicStatus.Current.Name

Invoke-Element (Find-ByNamePattern "*Practicar")
Invoke-Element (Find-ByNamePattern "Bombo")
Invoke-Element (Find-ByNamePattern "*Librer*")
Select-Library "Factory Sessions"
$acousticStatus = Find-ByNamePattern "Kit activo: Natural Studio*"
if (-not $acousticStatus) { throw "Factory Sessions se seleccionó, pero Natural Studio no quedó activo." }
$acousticStatusText = $acousticStatus.Current.Name

[pscustomobject]@{
    ElectronicKit = $electronicStatusText
    AcousticKit = $acousticStatusText
    PadTriggered = "Bombo"
    Result = "PASS"
}
