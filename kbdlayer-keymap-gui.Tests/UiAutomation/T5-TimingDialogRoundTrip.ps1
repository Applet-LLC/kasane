# GUI-T5: UI Automation round-trip test for the "タイミング" (Timing) dialog -- feature/tap-hold-modes
# added the judgment mode (TapHoldModeBox), Quick Tap (QuickTapTermBox/QuickTapEnabled), and TT count
# (TappingToggleBox) controls. Sets non-default values, OK, reopen, confirm they stuck. OK only
# marks the in-memory project dirty (MainWindow.BtnTiming_Click) -- it does not Apply to a live
# device, so this is safe to run without a connected device.
. "$PSScriptRoot\UiaLib.ps1"
$ErrorActionPreference = "Stop"
$EXE = "C:\Users\applet\Documents\GitHub\LayerDriver\kbdlayer-keymap-gui\bin\Debug\net8.0-windows\win-x64\kbdlayer-keymap-gui.exe"
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

    $btnTiming = Find-ChildByAutomationId $main "BtnTiming"
    $before = Get-AllTopWindowHandles $proc.Id
    Invoke-Element $btnTiming
    $dlg1 = Find-NewTopWindow $proc.Id $before -timeoutMs 6000
    "  timing dialog opened: $($dlg1.Current.Name)"

    Set-ElementValue (Find-ChildByAutomationId $dlg1 "TapTermBox") "150"
    Select-ComboItemByIndex (Find-ChildByAutomationId $dlg1 "TapHoldModeBox") 1  # Permissive Hold
    Set-ToggleState (Find-ChildByAutomationId $dlg1 "QuickTapEnabled") $true
    Set-ElementValue (Find-ChildByAutomationId $dlg1 "QuickTapTermBox") "123"
    Set-ElementValue (Find-ChildByAutomationId $dlg1 "TappingToggleBox") "7"
    Start-Sleep -Milliseconds 300

    $dlg1Handle = $dlg1.Current.NativeWindowHandle.ToString()
    Invoke-Element (Find-ChildByAutomationId $dlg1 "OkButton")
    Start-Sleep -Milliseconds 600
    Chk "timing dialog closed after OK" ((Get-AllTopWindowHandles $proc.Id) -notcontains $dlg1Handle)

    $before2 = Get-AllTopWindowHandles $proc.Id
    Invoke-Element $btnTiming
    $dlg2 = Find-NewTopWindow $proc.Id $before2 -timeoutMs 6000
    Start-Sleep -Milliseconds 300

    $tapTerm2 = Get-ElementValue (Find-ChildByAutomationId $dlg2 "TapTermBox")
    $modeCombo2 = Find-ChildByAutomationId $dlg2 "TapHoldModeBox"
    $modeSel2 = $modeCombo2.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()
    $quickEnabled2 = Get-ToggleState (Find-ChildByAutomationId $dlg2 "QuickTapEnabled")
    $quickTerm2 = Get-ElementValue (Find-ChildByAutomationId $dlg2 "QuickTapTermBox")
    $tt2 = Get-ElementValue (Find-ChildByAutomationId $dlg2 "TappingToggleBox")
    "  reopened: TapTerm=$tapTerm2 mode='$($modeSel2[0].Current.Name)' quickEnabled=$quickEnabled2 quickTerm=$quickTerm2 tt=$tt2"

    Chk "TapTerm round-trips (150)" ($tapTerm2 -eq "150")
    Chk "TapHoldMode round-trips (Permissive Hold, index 1)" ($modeSel2[0].Current.Name -match "Permissive Hold|離したら長押し")
    Chk "QuickTapEnabled round-trips (checked)" ($quickEnabled2 -eq [System.Windows.Automation.ToggleState]::On)
    Chk "QuickTapTerm round-trips (123)" ($quickTerm2 -eq "123")
    Chk "TappingToggle round-trips (7)" ($tt2 -eq "7")

    # Now test Quick Tap "off" (unchecked) round-trips too.
    Set-ToggleState (Find-ChildByAutomationId $dlg2 "QuickTapEnabled") $false
    Start-Sleep -Milliseconds 200
    $dlg2Handle = $dlg2.Current.NativeWindowHandle.ToString()
    Invoke-Element (Find-ChildByAutomationId $dlg2 "OkButton")
    Start-Sleep -Milliseconds 600
    Chk "dialog closed after second OK" ((Get-AllTopWindowHandles $proc.Id) -notcontains $dlg2Handle)

    $before3 = Get-AllTopWindowHandles $proc.Id
    Invoke-Element $btnTiming
    $dlg3 = Find-NewTopWindow $proc.Id $before3 -timeoutMs 6000
    Start-Sleep -Milliseconds 300
    $quickEnabled3 = Get-ToggleState (Find-ChildByAutomationId $dlg3 "QuickTapEnabled")
    "  reopened again: quickEnabled=$quickEnabled3"
    Chk "QuickTapEnabled=off round-trips (unchecked)" ($quickEnabled3 -eq [System.Windows.Automation.ToggleState]::Off)

    Invoke-Element (Find-ChildByAutomationId $dlg3 "CancelButton")
    Start-Sleep -Milliseconds 300

    "=== FAIL COUNT: $fail ==="
}
catch { "EXCEPTION: $_"; $fail++ }
finally {
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
}
