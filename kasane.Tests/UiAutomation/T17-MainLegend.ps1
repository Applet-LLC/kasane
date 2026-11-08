# GUI-T17: the legend popup of the main window (2026-11-08, UI text trim).
# The long legend paragraph under the keyboard view was replaced with a "凡例" (Legend) button that opens a popup
# with color/mark samples and a "?" button (opens chapter 5, "keyboard-view"). The help button is not pressed
# (it would open a browser).
. "$PSScriptRoot\UiaLib.ps1"
$ErrorActionPreference = "Stop"
$EXE = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\..\kasane\bin\Debug\net8.0-windows\win-x64\kasane.exe"))
$fail = 0
function Chk([string]$label, [bool]$cond) {
    $v = if ($cond) { 'PASS' } else { 'FAIL'; $script:fail++ }
    "  [$v] $label"
}
$proc = $null
try {
    $proc = Start-Process $EXE -PassThru
    Start-Sleep -Milliseconds 1500
    $main = Find-TopWindowByProcess $proc.Id -timeoutMs 8000
    Set-ForegroundWindowFor $main

    $legendText = $null; try { $legendText = Find-ChildByAutomationId $main "TxtLegend" -timeoutMs 500 } catch { }
    Chk "the legend paragraph is gone" ($null -eq $legendText)
    Chk "OFF button reads just 'OFF'" ((Find-ChildByAutomationId $main "BtnOff").Current.Name -eq "OFF")

    $before = Get-AllTopWindowHandles $proc.Id
    Invoke-Element (Find-ChildByAutomationId $main "BtnLegend")
    $popup = Find-NewTopWindow $proc.Id $before -timeoutMs 4000
    Start-Sleep -Milliseconds 300
    Chk "the legend popup has a help button" ([bool](Find-ChildByAutomationId $popup "HelpButton"))

    # Pressing the button again closes it.
    Invoke-Element (Find-ChildByAutomationId $main "BtnLegend")
    Start-Sleep -Milliseconds 400
    Chk "pressing Legend again closes the popup" ((Get-AllTopWindowHandles $proc.Id).Count -le $before.Count)
    "=== FAIL COUNT: $fail ==="
}
catch { "EXCEPTION: $_"; $fail++ }
finally {
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
}
