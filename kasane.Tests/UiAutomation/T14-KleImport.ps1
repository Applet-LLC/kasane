# GUI-T14: UI Automation test for the KLE import window (2026-10-12).
# Opens the main window, "KLEから追加...", loads a small raw KLE file (typed into the path box), checks the summary, the key list and the
# save button (enabled only with a valid name), then cancels. Nothing is saved.
. "$PSScriptRoot\UiaLib.ps1"
$ErrorActionPreference = "Stop"
$EXE = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\..\kasane\bin\Debug\net8.0-windows\win-x64\kasane.exe"))
$fail = 0
function Chk([string]$label, [bool]$cond) {
    $v = if ($cond) { 'PASS' } else { 'FAIL'; $script:fail++ }
    "  [$v] $label"
}
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("kasane-t14-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tmp | Out-Null
$kle = Join-Path $tmp "t14-raw.json"
# Esc, F1, Q (confirmed), a Shift in the bottom-left and one at the right (inferred), a wide unlabeled key (space), and "Fn" (unassigned).
'[[{"c":"#96938e"},"Esc",{"x":1},"F1","Q"],[{"w":2},"Shift",{"w":6.25},"",{"w":2},"Shift","Fn"]]' | Set-Content -Path $kle -Encoding UTF8
# A rotated cluster (r / rx / ry): a left half and a right half tilted by 15 degrees. It must be importable (save enabled) and keep its slots.
$kleRot = Join-Path $tmp "t14-rotated.json"
'[["Esc","Q"],[{"r":15,"rx":8,"ry":1},"Shift","W"],["A","S"]]' | Set-Content -Path $kleRot -Encoding UTF8
$proc = $null
try {
    $proc = Start-Process $EXE -PassThru
    Start-Sleep -Milliseconds 1500
    $main = Find-TopWindowByProcess $proc.Id -timeoutMs 8000
    Set-ForegroundWindowFor $main

    $before = Get-AllTopWindowHandles $proc.Id
    Invoke-Element (Find-ChildByAutomationId $main "BtnAddKle")
    $w = Find-NewTopWindow $proc.Id $before -timeoutMs 6000
    Start-Sleep -Milliseconds 400
    Chk "the KLE window has its controls" ((Find-ChildByAutomationId $w "KleFileBox") -and (Find-ChildByAutomationId $w "KleNameBox") -and (Find-ChildByAutomationId $w "KleLoadButton") -and (Find-ChildByAutomationId $w "KleKeyGrid"))
    Chk "save is disabled before loading" (-not (Find-ChildByAutomationId $w "KleSaveButton").Current.IsEnabled)

    Set-ElementValue (Find-ChildByAutomationId $w "KleFileBox") $kle
    Invoke-Element (Find-ChildByAutomationId $w "KleLoadButton")
    Start-Sleep -Milliseconds 700
    $summary = (Find-ChildByAutomationId $w "KleSummary").Current.Name
    "  summary: $summary"
    Chk "the summary counts 7 keys (3 confirmed, 3 inferred, 1 unassigned)" ($summary -match "7 キー" -and $summary -match "確定 3" -and $summary -match "推定 3" -and $summary -match "未割り当て 1")
    Chk "the diagram has a box for each key" ((Find-ChildByAutomationId $w "KleKey1") -and (Find-ChildByAutomationId $w "KleKey7"))
    Chk "save is enabled with the suggested name" ((Find-ChildByAutomationId $w "KleSaveButton").Current.IsEnabled)

    Set-ElementValue (Find-ChildByAutomationId $w "KleNameBox") "bad name!"
    Start-Sleep -Milliseconds 300
    Chk "save is disabled for an invalid name" (-not (Find-ChildByAutomationId $w "KleSaveButton").Current.IsEnabled)
    Set-ElementValue (Find-ChildByAutomationId $w "KleNameBox") "us104"
    Start-Sleep -Milliseconds 300
    Chk "a bundled name is accepted by the box (the window refuses it when saving)" ((Find-ChildByAutomationId $w "KleSaveButton").Current.IsEnabled)

    # Rotated layout: 6 keys, still saveable, and the rotated keys are clickable boxes on the diagram.
    Set-ElementValue (Find-ChildByAutomationId $w "KleNameBox") "rot-test"
    Set-ElementValue (Find-ChildByAutomationId $w "KleFileBox") $kleRot
    Invoke-Element (Find-ChildByAutomationId $w "KleLoadButton")
    Start-Sleep -Milliseconds 700
    $summary = (Find-ChildByAutomationId $w "KleSummary").Current.Name
    "  summary (rotated): $summary"
    Chk "the rotated layout shows 6 keys" ($summary -match "6 キー")
    Chk "the rotated layout can be saved" ((Find-ChildByAutomationId $w "KleSaveButton").Current.IsEnabled)
    Chk "the rotated keys have boxes on the diagram (Key3 and Key6)" ((Find-ChildByAutomationId $w "KleKey3") -and (Find-ChildByAutomationId $w "KleKey6"))

    Invoke-Element (Find-ChildByAutomationId $w "KleCancelButton")
    Start-Sleep -Milliseconds 400
    "=== FAIL COUNT: $fail ==="
}
catch { "EXCEPTION: $_"; $fail++ }
finally {
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}
