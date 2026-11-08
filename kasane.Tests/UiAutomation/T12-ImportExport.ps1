# GUI-T12: UI Automation test for the import/export window (P5, 2026-10-08).
# Opens the window from the main window and checks the export scope radios, the import tab's controls, and that
# "読み込む" stays disabled until a file is chosen. The file dialogs are not driven (nothing is written or read).
. "$PSScriptRoot\UiaLib.ps1"
$ErrorActionPreference = "Stop"
$EXE = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\..\kasane\bin\Debug\net8.0-windows\win-x64\kasane.exe"))
$fail = 0
function Chk([string]$label, [bool]$cond) {
    $v = if ($cond) { 'PASS' } else { 'FAIL'; $script:fail++ }
    "  [$v] $label"
}
function Select-Radio([System.Windows.Automation.AutomationElement]$el) {
    $p = $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    $p.Select()
}
$proc = $null
try {
    $proc = Start-Process $EXE -PassThru
    Start-Sleep -Milliseconds 1500
    $main = Find-TopWindowByProcess $proc.Id -timeoutMs 8000
    Set-ForegroundWindowFor $main

    $before = Get-AllTopWindowHandles $proc.Id
    Invoke-Element (Find-ChildByAutomationId $main "BtnImportExport")
    $dlg = Find-NewTopWindow $proc.Id $before -timeoutMs 6000
    Start-Sleep -Milliseconds 300

    Chk "both tabs exist" ((Find-ChildByAutomationId $dlg "TabExport") -and (Find-ChildByAutomationId $dlg "TabImport"))
    Chk "export has the three scope radios" ((Find-ChildByAutomationId $dlg "ExportScopeAll") -and (Find-ChildByAutomationId $dlg "ExportScopeKeyboard") -and (Find-ChildByAutomationId $dlg "ExportScopeLayer"))
    Select-Radio (Find-ChildByAutomationId $dlg "ExportScopeLayer")
    Chk "layer combo is present" ([bool](Find-ChildByAutomationId $dlg "ExportLayerCombo"))
    Chk "export button is present" ([bool](Find-ChildByAutomationId $dlg "ExportDoButton"))

    Select-TabById $dlg "TabImport"
    Start-Sleep -Milliseconds 300
    Chk "import has the pick button" ([bool](Find-ChildByAutomationId $dlg "ImportPickButton"))
    $do = Find-ChildByAutomationId $dlg "ImportDoButton"
    Chk "import button is disabled before a file is chosen" (-not $do.Current.IsEnabled)
    # 2026-11-08: the items and the replace/add choice are disabled until a file is loaded (the file decides which items are available).
    Chk "import items are disabled before a file is chosen" (-not (Find-ChildByAutomationId $dlg "ImportLayersCheck").Current.IsEnabled)
    Chk "replace/add is disabled before a file is chosen" (-not (Find-ChildByAutomationId $dlg "ImportModeReplace").Current.IsEnabled)
    Chk "the Emacs example button is present" ([bool](Find-ChildByAutomationId $dlg "ImportEmacsButton"))
    Chk "the help button is present" ([bool](Find-ChildByAutomationId $dlg "HelpButton"))
    # Loading a file (the bundled Emacs example) enables them. Nothing is imported; the dialog is closed below.
    Invoke-Element (Find-ChildByAutomationId $dlg "ImportEmacsButton")
    Start-Sleep -Milliseconds 800
    Chk "import items are enabled after a file is loaded" ((Find-ChildByAutomationId $dlg "ImportLayersCheck").Current.IsEnabled)
    Chk "replace/add is enabled after a file is loaded" ((Find-ChildByAutomationId $dlg "ImportModeReplace").Current.IsEnabled)

    Invoke-Element (Find-ChildByAutomationId $dlg "CloseButton")
    Start-Sleep -Milliseconds 500
    Chk "window closes" ((Get-AllTopWindowHandles $proc.Id).Count -le $before.Count)
    "=== FAIL COUNT: $fail ==="
}
catch { "EXCEPTION: $_"; $fail++ }
finally {
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
}
