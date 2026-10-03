# GUI-T1: UI Automation round-trip test for the TapDance editor -- type LCTL(KC_C)/MACRO(0) into
# DoubleTap/TripleTap (the new modded-key/macro tap-side support), click OK, reopen, and confirm
# the same text is displayed (proves the GUI's field wiring, not just ProjectService's logic).
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
    "  launching GUI..."
    $proc = Start-Process $EXE -PassThru
    Start-Sleep -Milliseconds 1500

    $main = Find-TopWindowByProcess $proc.Id -timeoutMs 8000
    "  main window: $($main.Current.Name)"

    $cmbDevice = Find-ChildByAutomationId $main "CmbDevice"
    # select the first device in the list (index 0).
    Select-ComboItemByIndex $cmbDevice 0
    Start-Sleep -Milliseconds 800
    "  device selected"

    $btnTapDances = Find-ChildByAutomationId $main "BtnTapDances"
    $beforeHandles = Get-AllTopWindowHandles $proc.Id
    Invoke-Element $btnTapDances
    $td1 = Find-NewTopWindow $proc.Id $beforeHandles -timeoutMs 6000
    "  tap-dance editor opened: $($td1.Current.Name)"

    $idList = Find-ChildByAutomationId $td1 "IdList"
    Select-ListItemByIndex $idList 0
    Start-Sleep -Milliseconds 300

    $single = Find-ChildByAutomationId $td1 "SingleTap"
    $hold = Find-ChildByAutomationId $td1 "SingleHold"
    $dbl = Find-ChildByAutomationId $td1 "DoubleTap"
    $triple = Find-ChildByAutomationId $td1 "TripleTap"

    Set-ElementValue $single "KC_ESC"
    Set-ElementValue $hold "MT(MOD_LCTL,KC_NO)"
    Set-ElementValue $dbl "LCTL(KC_C)"
    Set-ElementValue $triple "MACRO(0)"
    Start-Sleep -Milliseconds 300

    $validation = Find-ChildByAutomationId $td1 "ValidationText"
    $validationText = $validation.Current.Name
    "  validation text: '$validationText'"
    Chk "validation shows OK after entering valid values (incl. LCTL(KC_C) and MACRO(0))" ($validationText -eq "OK")

    $td1Handle = $td1.Current.NativeWindowHandle.ToString()
    $ok1 = Find-ChildByAutomationId $td1 "OkButton"
    Invoke-Element $ok1
    Start-Sleep -Milliseconds 800

    $afterCloseHandles = Get-AllTopWindowHandles $proc.Id
    Chk "tap-dance editor window closed after OK" ($afterCloseHandles -notcontains $td1Handle)

    "  reopening tap-dance editor to verify round-trip..."
    $beforeHandles2 = Get-AllTopWindowHandles $proc.Id
    Invoke-Element $btnTapDances
    $td2 = Find-NewTopWindow $proc.Id $beforeHandles2 -timeoutMs 6000

    $idList2 = Find-ChildByAutomationId $td2 "IdList"
    Select-ListItemByIndex $idList2 0
    Start-Sleep -Milliseconds 300

    $single2 = Get-ElementValue (Find-ChildByAutomationId $td2 "SingleTap")
    $hold2 = Get-ElementValue (Find-ChildByAutomationId $td2 "SingleHold")
    $dbl2 = Get-ElementValue (Find-ChildByAutomationId $td2 "DoubleTap")
    $triple2 = Get-ElementValue (Find-ChildByAutomationId $td2 "TripleTap")
    "  reopened values: SingleTap='$single2' SingleHold='$hold2' DoubleTap='$dbl2' TripleTap='$triple2'"

    Chk "SingleTap round-trips through the actual GUI" ($single2 -eq "KC_ESC")
    Chk "SingleHold round-trips through the actual GUI" ($hold2 -eq "MT(MOD_LCTL,KC_NO)")
    Chk "DoubleTap (modded key LCTL(KC_C)) round-trips through the actual GUI" ($dbl2 -eq "LCTL(KC_C)")
    Chk "TripleTap (MACRO(0)) round-trips through the actual GUI" ($triple2 -eq "MACRO(0)")

    # cancel out (don't want to leave a project change queued for Apply).
    $cancel2 = Find-ChildByAutomationId $td2 "CancelButton"
    Invoke-Element $cancel2
    Start-Sleep -Milliseconds 300

    "=== FAIL COUNT: $fail ==="
}
catch {
    "EXCEPTION: $_"
    $fail++
}
finally {
    if ($proc -and -not $proc.HasExited) {
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    }
}
