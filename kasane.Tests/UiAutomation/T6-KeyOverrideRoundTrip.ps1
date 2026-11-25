# GUI-T6: UI Automation round-trip + validation test for the "キー置換" (Key Override) editor --
# feature/key-override. Sets Mods/Key/Replacement -> OK -> reopen -> confirm the same values, then
# confirms invalid values (a layer action for Key, a macro for Replacement) show a validation error
# and block OK.
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

    $btn = Find-ChildByAutomationId $main "BtnKeyOverrides"
    $before = Get-AllTopWindowHandles $proc.Id
    Invoke-Element $btn
    $dlg1 = Find-NewTopWindow $proc.Id $before -timeoutMs 6000
    "  key override editor opened: $($dlg1.Current.Name)"

    $idList = Find-ChildByAutomationId $dlg1 "IdList"
    Select-ListItemByIndex $idList 0
    Start-Sleep -Milliseconds 300

    Set-ElementValue (Find-ChildByAutomationId $dlg1 "ModsBox") "MOD_LSFT"
    Set-ElementValue (Find-ChildByAutomationId $dlg1 "KeyBox") "KC_2"
    Set-ElementValue (Find-ChildByAutomationId $dlg1 "ReplacementBox") "KC_LBRC"
    Start-Sleep -Milliseconds 300

    $validationText = (Find-ChildByAutomationId $dlg1 "ValidationText").Current.Name
    "  validation text: '$validationText'"
    Chk "valid Key Override (MOD_LSFT, KC_2, KC_LBRC) validates OK" ($validationText -eq "OK")

    $dlg1Handle = $dlg1.Current.NativeWindowHandle.ToString()
    Invoke-Element (Find-ChildByAutomationId $dlg1 "OkButton")
    Start-Sleep -Milliseconds 600
    Chk "editor closed after OK" ((Get-AllTopWindowHandles $proc.Id) -notcontains $dlg1Handle)

    $before2 = Get-AllTopWindowHandles $proc.Id
    Invoke-Element $btn
    $dlg2 = Find-NewTopWindow $proc.Id $before2 -timeoutMs 6000
    Select-ListItemByIndex (Find-ChildByAutomationId $dlg2 "IdList") 0
    Start-Sleep -Milliseconds 300

    $mods2 = Get-ElementValue (Find-ChildByAutomationId $dlg2 "ModsBox")
    $key2 = Get-ElementValue (Find-ChildByAutomationId $dlg2 "KeyBox")
    $repl2 = Get-ElementValue (Find-ChildByAutomationId $dlg2 "ReplacementBox")
    "  reopened: Mods='$mods2' Key='$key2' Replacement='$repl2'"
    Chk "Mods round-trips (MOD_LSFT)" ($mods2 -eq "MOD_LSFT")
    Chk "Key round-trips (KC_2)" ($key2 -eq "KC_2")
    Chk "Replacement round-trips (KC_LBRC)" ($repl2 -eq "KC_LBRC")

    # Now test invalid values on a different slot (index 1) -- Key must be a plain key, not a layer action.
    Select-ListItemByIndex (Find-ChildByAutomationId $dlg2 "IdList") 1
    Start-Sleep -Milliseconds 300
    Set-ElementValue (Find-ChildByAutomationId $dlg2 "ModsBox") "MOD_LSFT"
    Set-ElementValue (Find-ChildByAutomationId $dlg2 "KeyBox") "MO(1)"
    Set-ElementValue (Find-ChildByAutomationId $dlg2 "ReplacementBox") "MACRO(0)"
    Start-Sleep -Milliseconds 300
    $validationText2 = (Find-ChildByAutomationId $dlg2 "ValidationText").Current.Name
    "  validation text (invalid): '$validationText2'"
    Chk "invalid Key (MO(1)) / Replacement (MACRO(0)) shows a non-OK validation message" ($validationText2 -ne "OK" -and $validationText2.Length -gt 0)

    $dlg2Handle = $dlg2.Current.NativeWindowHandle.ToString()
    Invoke-Element (Find-ChildByAutomationId $dlg2 "OkButton")
    Start-Sleep -Milliseconds 600
    Chk "dialog stays open when validation fails" ((Get-AllTopWindowHandles $proc.Id) -contains $dlg2Handle)

    Invoke-Element (Find-ChildByAutomationId $dlg2 "CancelButton")
    Start-Sleep -Milliseconds 300

    # 2026-11-25: "効くレイヤー" (Active layers). One check per layer of the project (OverrideLayerCheck<n>); none checked = all layers.
    # The project of this test has one layer, so layer 0's check is used: check it -> OK -> reopen -> still checked -> clear it -> OK -> reopen -> cleared.
    $before3 = Get-AllTopWindowHandles $proc.Id
    Invoke-Element $btn
    $dlg3 = Find-NewTopWindow $proc.Id $before3 -timeoutMs 6000
    Select-ListItemByIndex (Find-ChildByAutomationId $dlg3 "IdList") 2
    Start-Sleep -Milliseconds 300
    Chk "the layer checks exist (layer 0)" ([bool](Find-ChildByAutomationId $dlg3 "OverrideLayerCheck0"))
    Chk "no layer is checked at first (works on all layers)" ((Get-ToggleState (Find-ChildByAutomationId $dlg3 "OverrideLayerCheck0")) -eq [System.Windows.Automation.ToggleState]::Off)
    Set-ElementValue (Find-ChildByAutomationId $dlg3 "ModsBox") "MOD_LCTL"
    Set-ElementValue (Find-ChildByAutomationId $dlg3 "KeyBox") "KC_L"
    Set-ElementValue (Find-ChildByAutomationId $dlg3 "ReplacementBox") "LCTL(KC_PGUP)"
    Set-ToggleState (Find-ChildByAutomationId $dlg3 "OverrideLayerCheck0") $true
    Start-Sleep -Milliseconds 300
    Chk "a key override limited to layer 0 validates OK" ((Find-ChildByAutomationId $dlg3 "ValidationText").Current.Name -eq "OK")
    $dlg3Handle = $dlg3.Current.NativeWindowHandle.ToString()
    Invoke-Element (Find-ChildByAutomationId $dlg3 "OkButton")
    Start-Sleep -Milliseconds 600
    Chk "editor closed after OK (layer check)" ((Get-AllTopWindowHandles $proc.Id) -notcontains $dlg3Handle)

    $before4 = Get-AllTopWindowHandles $proc.Id
    Invoke-Element $btn
    $dlg4 = Find-NewTopWindow $proc.Id $before4 -timeoutMs 6000
    Select-ListItemByIndex (Find-ChildByAutomationId $dlg4 "IdList") 2
    Start-Sleep -Milliseconds 300
    Chk "the layer check round-trips (layer 0 stays checked)" ((Get-ToggleState (Find-ChildByAutomationId $dlg4 "OverrideLayerCheck0")) -eq [System.Windows.Automation.ToggleState]::On)
    Chk "Replacement round-trips with the layer check (LCTL(KC_PGUP))" ((Get-ElementValue (Find-ChildByAutomationId $dlg4 "ReplacementBox")) -eq "LCTL(KC_PGUP)")
    Set-ToggleState (Find-ChildByAutomationId $dlg4 "OverrideLayerCheck0") $false
    Start-Sleep -Milliseconds 300
    $dlg4Handle = $dlg4.Current.NativeWindowHandle.ToString()
    Invoke-Element (Find-ChildByAutomationId $dlg4 "OkButton")
    Start-Sleep -Milliseconds 600

    $before5 = Get-AllTopWindowHandles $proc.Id
    Invoke-Element $btn
    $dlg5 = Find-NewTopWindow $proc.Id $before5 -timeoutMs 6000
    Select-ListItemByIndex (Find-ChildByAutomationId $dlg5 "IdList") 2
    Start-Sleep -Milliseconds 300
    Chk "clearing the layer check round-trips (works on all layers again)" ((Get-ToggleState (Find-ChildByAutomationId $dlg5 "OverrideLayerCheck0")) -eq [System.Windows.Automation.ToggleState]::Off)

    # 2026-11-26: the modifier checks (Shift / Alt / Ctrl / Win) also write the modifiers box when toggled without a click
    # (UI Automation's Toggle pattern), and the box drives the checks back.
    Select-ListItemByIndex (Find-ChildByAutomationId $dlg5 "IdList") 3
    Start-Sleep -Milliseconds 300
    Set-ToggleState (Find-ChildByAutomationId $dlg5 "ModCtrlCheck") $true
    Start-Sleep -Milliseconds 200
    Chk "toggling the Ctrl check writes MOD_LCTL into the modifiers box" ((Get-ElementValue (Find-ChildByAutomationId $dlg5 "ModsBox")) -eq "MOD_LCTL")
    Set-ToggleState (Find-ChildByAutomationId $dlg5 "ModShiftCheck") $true
    Start-Sleep -Milliseconds 200
    $modsText = Get-ElementValue (Find-ChildByAutomationId $dlg5 "ModsBox")
    Chk "toggling Shift as well keeps both (MOD_LCTL and MOD_LSFT)" (($modsText -match "MOD_LCTL") -and ($modsText -match "MOD_LSFT"))
    Set-ToggleState (Find-ChildByAutomationId $dlg5 "ModCtrlCheck") $false
    Start-Sleep -Milliseconds 200
    Chk "clearing the Ctrl check removes it from the box" ((Get-ElementValue (Find-ChildByAutomationId $dlg5 "ModsBox")) -eq "MOD_LSFT")
    Invoke-Element (Find-ChildByAutomationId $dlg5 "CancelButton")
    Start-Sleep -Milliseconds 300

    "=== FAIL COUNT: $fail ==="
}
catch { "EXCEPTION: $_"; $fail++ }
finally {
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
}
