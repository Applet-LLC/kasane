# GUI-T5: UI Automation round-trip test for the "タイミング" (Timing) tab of the settings dialog
# (BtnSettings -> TabTiming). Until feature/gui-settings this was a separate dialog opened by BtnTiming;
# the controls kept their names (TapTermBox, TapHoldModeBox, QuickTapTermBox/QuickTapEnabled,
# TappingToggleBox, OkButton/CancelButton). Sets non-default values, OK, reopen, confirm they stuck.
# OK only marks the in-memory project dirty (MainWindow.BtnSettings_Click) -- it does not Apply to a
# live device, so this is safe to run with or without a connected device.
. "$PSScriptRoot\UiaLib.ps1"
$ErrorActionPreference = "Stop"
$EXE = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\..\kasane\bin\Debug\net8.0-windows\win-x64\kasane.exe"))
$fail = 0
function Chk([string]$label, [bool]$cond) {
    $v = if ($cond) { 'PASS' } else { 'FAIL'; $script:fail++ }
    "  [$v] $label"
}

function Open-TimingTab($main, $procId) {
    $before = Get-AllTopWindowHandles $procId
    Invoke-Element (Find-ChildByAutomationId $main "BtnSettings")
    $dlg = Find-NewTopWindow $procId $before -timeoutMs 6000
    Select-TabById $dlg "TabTiming"
    return $dlg
}

$proc = $null
try {
    $proc = Start-Process $EXE -PassThru
    Start-Sleep -Milliseconds 1500
    $main = Find-TopWindowByProcess $proc.Id -timeoutMs 8000
    Set-ForegroundWindowFor $main

    $dlg1 = Open-TimingTab $main $proc.Id
    "  settings dialog opened: $($dlg1.Current.Name)"

    Set-ElementValue (Find-ChildByAutomationId $dlg1 "TapTermBox") "150"
    Select-ComboItemByIndex (Find-ChildByAutomationId $dlg1 "TapHoldModeBox") 1  # Permissive Hold
    Set-ToggleState (Find-ChildByAutomationId $dlg1 "QuickTapEnabled") $true
    Set-ElementValue (Find-ChildByAutomationId $dlg1 "QuickTapTermBox") "123"
    Set-ElementValue (Find-ChildByAutomationId $dlg1 "TappingToggleBox") "7"
    Start-Sleep -Milliseconds 300

    $dlg1Handle = $dlg1.Current.NativeWindowHandle.ToString()
    Invoke-Element (Find-ChildByAutomationId $dlg1 "OkButton")
    Start-Sleep -Milliseconds 600
    Chk "settings dialog closed after OK" ((Get-AllTopWindowHandles $proc.Id) -notcontains $dlg1Handle)

    $dlg2 = Open-TimingTab $main $proc.Id
    $tapTerm2 = Get-ElementValue (Find-ChildByAutomationId $dlg2 "TapTermBox")
    $mode2 = Get-ComboSelectedName (Find-ChildByAutomationId $dlg2 "TapHoldModeBox")
    $quickEnabled2 = Get-ToggleState (Find-ChildByAutomationId $dlg2 "QuickTapEnabled")
    $quickTerm2 = Get-ElementValue (Find-ChildByAutomationId $dlg2 "QuickTapTermBox")
    $tt2 = Get-ElementValue (Find-ChildByAutomationId $dlg2 "TappingToggleBox")
    "  reopened: TapTerm=$tapTerm2 mode='$mode2' quickEnabled=$quickEnabled2 quickTerm=$quickTerm2 tt=$tt2"

    Chk "TapTerm round-trips (150)" ($tapTerm2 -eq "150")
    Chk "TapHoldMode round-trips (Permissive Hold, index 1)" ($mode2 -match "Permissive Hold|離したら長押し")
    Chk "QuickTapEnabled round-trips (checked)" ($quickEnabled2 -eq [System.Windows.Automation.ToggleState]::On)
    Chk "QuickTapTerm round-trips (123)" ($quickTerm2 -eq "123")
    Chk "TappingToggle round-trips (7)" ($tt2 -eq "7")

    # Quick Tap "off" (unchecked) round-trips too.
    Set-ToggleState (Find-ChildByAutomationId $dlg2 "QuickTapEnabled") $false
    Start-Sleep -Milliseconds 200
    $dlg2Handle = $dlg2.Current.NativeWindowHandle.ToString()
    Invoke-Element (Find-ChildByAutomationId $dlg2 "OkButton")
    Start-Sleep -Milliseconds 600
    Chk "dialog closed after second OK" ((Get-AllTopWindowHandles $proc.Id) -notcontains $dlg2Handle)

    $dlg3 = Open-TimingTab $main $proc.Id
    $quickEnabled3 = Get-ToggleState (Find-ChildByAutomationId $dlg3 "QuickTapEnabled")
    "  reopened again: quickEnabled=$quickEnabled3"
    Chk "QuickTapEnabled=off round-trips (unchecked)" ($quickEnabled3 -eq [System.Windows.Automation.ToggleState]::Off)

    # An invalid value keeps the dialog open, switches to the Timing tab and shows the error.
    Set-ElementValue (Find-ChildByAutomationId $dlg3 "TapTermBox") "abc"
    Select-TabById $dlg3 "TabFeatures"
    $dlg3Handle = $dlg3.Current.NativeWindowHandle.ToString()
    Invoke-Element (Find-ChildByAutomationId $dlg3 "OkButton")
    Start-Sleep -Milliseconds 600
    Chk "dialog stays open when TapTerm is invalid" ((Get-AllTopWindowHandles $proc.Id) -contains $dlg3Handle)
    $err = (Find-ChildByAutomationId $dlg3 "SettingsError").Current.Name
    "  error text: '$err'"
    Chk "error message mentions TapTerm" ($err -match "TapTerm")
    $tapBoxVisible = $true
    try { Find-ChildByAutomationId $dlg3 "TapTermBox" -timeoutMs 1500 | Out-Null } catch { $tapBoxVisible = $false }
    Chk "the Timing tab is selected again (its controls are in the tree)" $tapBoxVisible

    Invoke-Element (Find-ChildByAutomationId $dlg3 "CancelButton")
    Start-Sleep -Milliseconds 300

    "=== FAIL COUNT: $fail ==="
}
catch { "EXCEPTION: $_"; $fail++ }
finally {
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
}
