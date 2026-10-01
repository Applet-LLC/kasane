# GUI-T2: UI Automation test -- an invalid SingleHold value shows a validation error in the GUI and
# blocks OK from closing the dialog. (Originally this used "KC_NO", the value that was mistakenly used
# in the OIB session; since feature/layer-to-tt, KC_NO/blank in SingleHold means "no hold action" and is
# valid, so an actually invalid token -- a layer key, which SingleHold does not accept -- is used instead.)
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

    $btnTapDances = Find-ChildByAutomationId $main "BtnTapDances"
    $before = Get-AllTopWindowHandles $proc.Id
    Invoke-Element $btnTapDances
    $td = Find-NewTopWindow $proc.Id $before -timeoutMs 6000

    $idList = Find-ChildByAutomationId $td "IdList"
    Select-ListItemByIndex $idList 1  # a different slot than GUI-T1 used, to avoid interference
    Start-Sleep -Milliseconds 300

    Set-ElementValue (Find-ChildByAutomationId $td "SingleTap") "KC_ESC"
    Set-ElementValue (Find-ChildByAutomationId $td "SingleHold") "MO(1)"   # invalid: SingleHold takes KC_*, MT, LT, modded key, LM, MACRO, or blank/KC_NO (not MO)
    Set-ElementValue (Find-ChildByAutomationId $td "DoubleTap") "KC_A"
    Set-ElementValue (Find-ChildByAutomationId $td "TripleTap") "KC_B"
    Start-Sleep -Milliseconds 300

    $validationText = (Find-ChildByAutomationId $td "ValidationText").Current.Name
    "  validation text: '$validationText'"
    Chk "invalid SingleHold (MO(1)) shows a non-OK validation message" ($validationText -ne "OK" -and $validationText.Length -gt 0)
    Chk "validation message mentions the hold field" ($validationText -match "ホールド")

    $tdHandle = $td.Current.NativeWindowHandle.ToString()
    $ok = Find-ChildByAutomationId $td "OkButton"
    Invoke-Element $ok
    Start-Sleep -Milliseconds 600
    $afterOk = Get-AllTopWindowHandles $proc.Id
    Chk "dialog stays open after OK when validation fails (does not silently accept bad input)" ($afterOk -contains $tdHandle)

    # fix it and confirm OK now closes the dialog cleanly.
    Set-ElementValue (Find-ChildByAutomationId $td "SingleHold") "KC_ESC"
    Start-Sleep -Milliseconds 300
    Invoke-Element (Find-ChildByAutomationId $td "OkButton")
    Start-Sleep -Milliseconds 600
    $afterFix = Get-AllTopWindowHandles $proc.Id
    Chk "dialog closes once the validation error is fixed" ($afterFix -notcontains $tdHandle)

    "=== FAIL COUNT: $fail ==="
}
catch { "EXCEPTION: $_"; $fail++ }
finally {
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
}
