# GUI-T4: UI Automation test -- feature/layer-to-tt added "no action" tap-dance slots (blank or
# KC_NO). Leaving SingleHold and TripleTap blank must validate OK, and the typed blank must survive
# a reopen of the same in-memory (not-yet-applied) edit unchanged.
#
# Note: the editor's "redisplays as KC_NO" behavior the dev handoff describes happens specifically
# on the Apply-to-driver -> ImportFromDriver path (ProjectService.ImportFromDriver calls
# KeycodeTable.ToToken on the driver's returned KBDLAYER_TAPDANCE_ENTRY, and ToToken's Key(0)
# fallback renders scancode-0 as "KC_NO") -- NOT on a plain reopen of the unsaved in-memory dialog,
# which just keeps whatever string the user typed. That Apply+reimport path is intentionally not
# exercised here (this session's GUI automation has consistently avoided live Apply/Save to the
# real device/project.json); it's covered instead by: (a) the passing xUnit tests for
# ProjectService.TapDanceToEntry's blank/KC_NO handling, (b) OIB section C-6, which set
# SingleTap=KC_NO via the CLI and confirmed via --get-tapdance that the driver stores/returns it as
# Type=2 Param2=0x0000, and (c) reading ImportFromDriver's source, confirmed to call ToToken.
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
    $td1 = Find-NewTopWindow $proc.Id $before -timeoutMs 6000

    $idList = Find-ChildByAutomationId $td1 "IdList"
    Select-ListItemByIndex $idList 2  # a different slot than T1 (0) and T2 (1), to avoid interference
    Start-Sleep -Milliseconds 300

    Set-ElementValue (Find-ChildByAutomationId $td1 "SingleTap") "KC_ESC"
    Set-ElementValue (Find-ChildByAutomationId $td1 "SingleHold") ""
    Set-ElementValue (Find-ChildByAutomationId $td1 "DoubleTap") "KC_A"
    Set-ElementValue (Find-ChildByAutomationId $td1 "TripleTap") ""
    Start-Sleep -Milliseconds 300

    $validationText = (Find-ChildByAutomationId $td1 "ValidationText").Current.Name
    "  validation text: '$validationText'"
    Chk "blank SingleHold/TripleTap validates OK (not an error)" ($validationText -eq "OK")

    $td1Handle = $td1.Current.NativeWindowHandle.ToString()
    Invoke-Element (Find-ChildByAutomationId $td1 "OkButton")
    Start-Sleep -Milliseconds 600
    Chk "tap-dance editor closed after OK" ((Get-AllTopWindowHandles $proc.Id) -notcontains $td1Handle)

    $before2 = Get-AllTopWindowHandles $proc.Id
    Invoke-Element $btnTapDances
    $td2 = Find-NewTopWindow $proc.Id $before2 -timeoutMs 6000
    Select-ListItemByIndex (Find-ChildByAutomationId $td2 "IdList") 2
    Start-Sleep -Milliseconds 300

    $hold2 = Get-ElementValue (Find-ChildByAutomationId $td2 "SingleHold")
    $triple2 = Get-ElementValue (Find-ChildByAutomationId $td2 "TripleTap")
    "  reopened values: SingleHold='$hold2' TripleTap='$triple2'"
    Chk "SingleHold stays blank on reopen of the unsaved edit (not corrupted/defaulted)" ($hold2 -eq "")
    Chk "TripleTap stays blank on reopen of the unsaved edit (not corrupted/defaulted)" ($triple2 -eq "")

    Invoke-Element (Find-ChildByAutomationId $td2 "CancelButton")
    Start-Sleep -Milliseconds 300

    "=== FAIL COUNT: $fail ==="
}
catch { "EXCEPTION: $_"; $fail++ }
finally {
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
}
