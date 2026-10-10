# GUI-T8: UI Automation test -- feature/combo-td-extend's new combo/tap-dance output forms round-trip
# correctly in the GUI editors (no spurious validation error), and MO(1) is still rejected as a combo
# output (layer actions were never allowed as combo output, before or after this branch).
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

    # --- TapDance SingleHold: MODDED_KEY / LAYER_MOD / MACRO must all validate as OK ---
    $btnTapDances = Find-ChildByAutomationId $main "BtnTapDances"
    $before = Get-AllTopWindowHandles $proc.Id
    Invoke-Element $btnTapDances
    $td = Find-NewTopWindow $proc.Id $before -timeoutMs 6000

    Select-ListItemByIndex (Find-ChildByAutomationId $td "IdList") 2  # avoid T1/T2's slots
    Start-Sleep -Milliseconds 300
    Set-ElementValue (Find-ChildByAutomationId $td "SingleTap") "KC_ESC"
    Set-ElementValue (Find-ChildByAutomationId $td "DoubleTap") "KC_NO"
    Set-ElementValue (Find-ChildByAutomationId $td "TripleTap") "KC_NO"

    foreach ($case in @(
        @{ hold = "LCTL(KC_C)"; label = "MODDED_KEY (LCTL(KC_C))" },
        @{ hold = "LM(1,MOD_LSFT)"; label = "LAYER_MOD (LM(1,MOD_LSFT))" },
        @{ hold = "MACRO(0)"; label = "MACRO (MACRO(0))" }
    )) {
        Set-ElementValue (Find-ChildByAutomationId $td "SingleHold") $case.hold
        Start-Sleep -Milliseconds 300
        $v = (Find-ChildByAutomationId $td "ValidationText").Current.Name
        "  SingleHold='$($case.hold)' -> validation: '$v'"
        Chk "TapDance SingleHold accepts $($case.label)" ($v -eq "OK")
    }

    # regression: MO(1) must still be rejected.
    Set-ElementValue (Find-ChildByAutomationId $td "SingleHold") "MO(1)"
    Start-Sleep -Milliseconds 300
    $vMo = (Find-ChildByAutomationId $td "ValidationText").Current.Name
    Chk "TapDance SingleHold still rejects MO(1)" ($vMo -ne "OK" -and $vMo.Length -gt 0)

    # leave this tap-dance slot clean (no hold) before closing, so it doesn't linger as a stray definition.
    Set-ElementValue (Find-ChildByAutomationId $td "SingleHold") ""
    Set-ElementValue (Find-ChildByAutomationId $td "SingleTap") ""
    Start-Sleep -Milliseconds 200
    Invoke-Element (Find-ChildByAutomationId $td "CancelButton")
    Start-Sleep -Milliseconds 400

    # --- Combo output: MODDED_KEY / MACRO must validate as OK; MO(1) must still be rejected ---
    $btnCombos = Find-ChildByAutomationId $main "BtnCombos"
    $before2 = Get-AllTopWindowHandles $proc.Id
    Invoke-Element $btnCombos
    $combo = Find-NewTopWindow $proc.Id $before2 -timeoutMs 6000

    Select-ListItemByIndex (Find-ChildByAutomationId $combo "IdList") 2
    Start-Sleep -Milliseconds 300
    $m1 = Find-ChildByAutomationId $combo "Member1Combo"
    $m2 = Find-ChildByAutomationId $combo "Member2Combo"
    Select-ComboItemByIndex $m1 1
    Start-Sleep -Milliseconds 150
    Select-ComboItemByIndex $m2 2
    Start-Sleep -Milliseconds 150

    foreach ($case in @(
        @{ output = "LCTL(KC_Z)"; label = "MODDED_KEY (LCTL(KC_Z))" },
        @{ output = "MACRO(0)"; label = "MACRO (MACRO(0))" }
    )) {
        Set-ElementValue (Find-ChildByAutomationId $combo "OutputBox") $case.output
        Start-Sleep -Milliseconds 300
        $v = (Find-ChildByAutomationId $combo "ValidationText").Current.Name
        "  Combo output='$($case.output)' -> validation: '$v'"
        Chk "Combo output accepts $($case.label)" ($v -eq "OK")
    }

    Set-ElementValue (Find-ChildByAutomationId $combo "OutputBox") "MO(1)"
    Start-Sleep -Milliseconds 300
    $vMoCombo = (Find-ChildByAutomationId $combo "ValidationText").Current.Name
    "  Combo output='MO(1)' -> validation: '$vMoCombo'"
    Chk "Combo output still rejects MO(1)" ($vMoCombo -ne "OK" -and $vMoCombo.Length -gt 0)
    Chk "Combo rejection message mentions output (出力)" ($vMoCombo -match "出力")

    Invoke-Element (Find-ChildByAutomationId $combo "CancelButton")
    Start-Sleep -Milliseconds 400

    "=== FAIL COUNT: $fail ==="
}
catch { "EXCEPTION: $_"; $fail++ }
finally {
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
}
