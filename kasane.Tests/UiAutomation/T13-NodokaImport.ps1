# GUI-T13: UI Automation test for the nodoka import window (P6, 2026-10-09).
# Opens the import/export window, switches to the import tab, opens "のどかの設定を取り込む..." and checks its controls.
# A small .nodoka file is written to a temp folder and typed into the path box, then converted; nothing is imported or saved.
. "$PSScriptRoot\UiaLib.ps1"
$ErrorActionPreference = "Stop"
$EXE = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\..\kasane\bin\Debug\net8.0-windows\win-x64\kasane.exe"))
$fail = 0
function Chk([string]$label, [bool]$cond) {
    $v = if ($cond) { 'PASS' } else { 'FAIL'; $script:fail++ }
    "  [$v] $label"
}
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("kasane-t13-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tmp | Out-Null
$nodoka = Join-Path $tmp "t13.nodoka"
@"
def key A = 0x1e
def key B = 0x30
def key F1 = 0x3b
keymap Global
key *A = *B
key F1 = &WindowMaximize
"@ | Set-Content -Path $nodoka -Encoding UTF8
$proc = $null
try {
    $proc = Start-Process $EXE -PassThru
    Start-Sleep -Milliseconds 1500
    $main = Find-TopWindowByProcess $proc.Id -timeoutMs 8000
    Set-ForegroundWindowFor $main

    $before = Get-AllTopWindowHandles $proc.Id
    Invoke-Element (Find-ChildByAutomationId $main "BtnImportExport")
    $dlg = Find-NewTopWindow $proc.Id $before -timeoutMs 6000
    Select-TabById $dlg "TabImport"
    Start-Sleep -Milliseconds 300

    $before2 = Get-AllTopWindowHandles $proc.Id
    Invoke-Element (Find-ChildByAutomationId $dlg "ImportNodokaButton")
    $nd = Find-NewTopWindow $proc.Id $before2 -timeoutMs 6000
    Start-Sleep -Milliseconds 400
    Chk "the nodoka window has its controls" ((Find-ChildByAutomationId $nd "NodokaPath") -and (Find-ChildByAutomationId $nd "NodokaSymbols") -and (Find-ChildByAutomationId $nd "NodokaConvertButton"))
    Chk "import is disabled before converting" (-not (Find-ChildByAutomationId $nd "NodokaAcceptButton").Current.IsEnabled)

    Set-ElementValue (Find-ChildByAutomationId $nd "NodokaPath") $nodoka
    Invoke-Element (Find-ChildByAutomationId $nd "NodokaConvertButton")
    Start-Sleep -Milliseconds 600
    $summary = (Find-ChildByAutomationId $nd "NodokaSummary").Current.Name
    Chk "the summary counts the unsupported function" ($summary -match "変換できない 1 件")
    Chk "import is enabled after converting" ((Find-ChildByAutomationId $nd "NodokaAcceptButton").Current.IsEnabled)
    $lines = Find-ChildByAutomationId $nd "NodokaLines"
    Chk "the list shows the file and line of the unsupported function" ($lines.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition).Count -ge 1)

    Invoke-Element (Find-ChildByAutomationId $nd "NodokaCancelButton")
    Start-Sleep -Milliseconds 400
    Invoke-Element (Find-ChildByAutomationId $dlg "CloseButton")
    "=== FAIL COUNT: $fail ==="
}
catch { "EXCEPTION: $_"; $fail++ }
finally {
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}
