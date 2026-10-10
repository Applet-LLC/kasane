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

    "=== FAIL COUNT: $fail ==="
}
catch { "EXCEPTION: $_"; $fail++ }
finally {
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
}
