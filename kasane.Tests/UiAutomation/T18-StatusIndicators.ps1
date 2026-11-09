# GUI-T18: status indicators of the main window (2026-11-09, UI status indicators).
# The title bar keeps only the name, version, and "*". The driver state, "not applied", "read-only", and "editing"
# are chips in the status bar (AutomationId ChipDriver / ChipUnapplied / ChipReadOnly / ChipEditing).
# Message kinds and validation icons are checked by eye (see the handoff).
. "$PSScriptRoot\UiaLib.ps1"
$ErrorActionPreference = "Stop"
$EXE = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\..\kasane\bin\Debug\net8.0-windows\win-x64\kasane.exe"))
$fail = 0
function Chk([string]$label, [bool]$cond) {
    $v = if ($cond) { 'PASS' } else { 'FAIL'; $script:fail++ }
    "  [$v] $label"
}
function Has($w, [string]$id) { try { [void](Find-ChildByAutomationId $w $id -timeoutMs 700); return $true } catch { return $false } }
$proc = $null
try {
    $proc = Start-Process $EXE -PassThru
    Start-Sleep -Milliseconds 1800
    $main = Find-TopWindowByProcess $proc.Id -timeoutMs 8000
    Set-ForegroundWindowFor $main

    Chk "the title has no driver state" ($main.Current.Name -notmatch "ドライバ|Driver")
    Chk "the driver chip is present" (Has $main "ChipDriver")
    $driver = (Find-ChildByAutomationId $main "ChipDriver").Current.Name
    "  driver chip: $driver"
    Chk "the driver chip names a state (ON / OFF / settings / not connected)" ($driver -match "^(ON|OFF|設定なし|未接続|このキーボードは未接続|No settings|Not connected|Keyboard not connected)$")
    # Not elevated: read-only chip. (If run elevated, the chip is absent; skip then.)
    "  read-only chip: $(Has $main 'ChipReadOnly')"
    Chk "no 'editing' chip while no editor is open" (-not (Has $main "ChipEditing"))

    # Opening an editor shows the 'editing' chip; closing it removes the chip.
    $before = Get-AllTopWindowHandles $proc.Id
    Invoke-Element (Find-ChildByAutomationId $main "BtnMacros")
    $ed = Find-NewTopWindow $proc.Id $before -timeoutMs 6000
    Start-Sleep -Milliseconds 500
    Chk "the 'editing' chip appears while an editor is open" (Has $main "ChipEditing")
    Invoke-Element (Find-ChildByAutomationId $ed "CancelButton")
    Start-Sleep -Milliseconds 600
    Chk "the 'editing' chip goes away after the editor closes" (-not (Has $main "ChipEditing"))

    "=== FAIL COUNT: $fail ==="
}
catch { "EXCEPTION: $_"; $fail++ }
finally { if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } }
