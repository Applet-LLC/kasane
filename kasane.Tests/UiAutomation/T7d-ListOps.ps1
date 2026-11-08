# GUI-T7d: list operations in the editors (GUI plan 1-3, feature/gui-list-ops). In memory only; the app is killed at the end.
#  1. Key overrides: fill 0 and 1, select 1, "Up" -> 1's content moves to 0. "Duplicate" -> copied to a free number.
#     "Delete" (no references) -> confirm window without a reference list -> the following numbers move up.
#  2. Macro 0 = KC_A, OK. Assign MACRO(0) to Key_0. Macro editor: delete 0 -> the confirm window lists 1 place and
#     "UnassignCheck" is on -> Delete -> OK. Key_0 is back to KC_TRNS (on layer 0 it works as its printed key).
#  3. The "×" button next to a keycode field clears it.
. "$PSScriptRoot\UiaLib.ps1"
$ErrorActionPreference = "Stop"
$EXE = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\..\kasane\bin\Debug\net8.0-windows\win-x64\kasane.exe"))
$fail = 0
function Chk([string]$label, [bool]$cond) {
    $v = if ($cond) { 'PASS' } else { 'FAIL'; $script:fail++ }
    Write-Host "  [$v] $label"
}
function ListItemName($list, [int]$i) {
    $items = $list.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
    return $items[$i].Current.Name
}
function Open-Window($main, [string]$buttonId) {
    $before = Get-AllTopWindowHandles $proc.Id
    Invoke-Element (Find-ChildByAutomationId $main $buttonId)
    $w = Find-NewTopWindow $proc.Id $before -timeoutMs 6000
    Start-Sleep -Milliseconds 600
    return $w
}

$proc = $null
try {
    $proc = Start-Process $EXE -PassThru
    Start-Sleep -Milliseconds 1500
    $main = Find-TopWindowByProcess $proc.Id -timeoutMs 8000
    Set-ForegroundWindowFor $main

    # 1. Key overrides
    $ko = Open-Window $main "BtnKeyOverrides"
    $list = Find-ChildByAutomationId $ko "IdList"
    # 2026-11-08: the long explanation at the top was removed; the editors have a "?" button (opens chapter 9).
    Chk "the help button is present" ([bool](Find-ChildByAutomationId $ko "HelpButton"))
    $free = @()
    $items = $list.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
    for ($i = 0; $i -lt $items.Count -and $free.Count -lt 2; $i++) { if ($items[$i].Current.Name -match '未使用|unused') { $free += $i } }
    "  free key override numbers: $($free -join ',')"
    $a = $free[0]; $b = $free[1]
    Select-ListItemByIndex $list $a; Start-Sleep -Milliseconds 300
    Set-ElementValue (Find-ChildByAutomationId $ko "ModsBox") "MOD_LSFT"
    Set-ElementValue (Find-ChildByAutomationId $ko "KeyBox") "KC_F1"
    Set-ElementValue (Find-ChildByAutomationId $ko "ReplacementBox") "KC_F2"
    Select-ListItemByIndex $list $b; Start-Sleep -Milliseconds 300
    Set-ElementValue (Find-ChildByAutomationId $ko "ModsBox") "MOD_LCTL"
    Set-ElementValue (Find-ChildByAutomationId $ko "KeyBox") "KC_F3"
    Set-ElementValue (Find-ChildByAutomationId $ko "ReplacementBox") "KC_F4"
    Start-Sleep -Milliseconds 200
    if ($b -eq $a + 1) {
        Invoke-Element (Find-ChildByAutomationId $ko "MoveUpButton"); Start-Sleep -Milliseconds 300
        Chk "Up: number $a now holds KC_F3 (got '$(ListItemName $list $a)')" ((ListItemName $list $a) -match 'KC_F3')
        Chk "Up: number $b now holds KC_F1" ((ListItemName $list $b) -match 'KC_F1')
    } else { "  (skip Up: free numbers are not adjacent)" }
    Select-ListItemByIndex $list $a; Start-Sleep -Milliseconds 300
    Invoke-Element (Find-ChildByAutomationId $ko "DuplicateItemButton"); Start-Sleep -Milliseconds 400
    $dupCount = 0
    $items = $list.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($it in $items) { if ($it.Current.Name -match ($(ListItemName $list $a) -replace '^\s*\d+:\s*', '' | ForEach-Object { [regex]::Escape($_) })) { $dupCount++ } }
    Chk "Duplicate: the content appears twice (got $dupCount)" ($dupCount -eq 2)
    Select-ListItemByIndex $list $a; Start-Sleep -Milliseconds 300
    $nextBefore = ListItemName $list ($a + 1)
    $before = Get-AllTopWindowHandles $proc.Id
    Invoke-Element (Find-ChildByAutomationId $ko "DeleteItemButton")
    $confirm = Find-NewTopWindow $proc.Id $before -timeoutMs 4000
    $refList = $null; try { $refList = Find-ChildByAutomationId $confirm "ReferenceList" -timeoutMs 500 } catch { }
    Chk "Delete (key override): no reference list" ($null -eq $refList)
    Invoke-Element (Find-ChildByAutomationId $confirm "DeleteButton"); Start-Sleep -Milliseconds 400
    $now = ListItemName $list $a
    Chk "Delete: the next number moved up ('$($nextBefore -replace '^\s*\d+:\s*','')' at $a)" (($now -replace '^\s*\d+:\s*','') -eq ($nextBefore -replace '^\s*\d+:\s*',''))
    # 3. The "×" clear button
    Select-ListItemByIndex $list $a; Start-Sleep -Milliseconds 300
    Invoke-Element (Find-ChildByAutomationId $ko "ReplacementBoxClear"); Start-Sleep -Milliseconds 200
    Chk "× clears the field" ((Get-ElementValue (Find-ChildByAutomationId $ko "ReplacementBox")) -eq "")
    Invoke-Element (Find-ChildByAutomationId $ko "CancelButton"); Start-Sleep -Milliseconds 400

    # 2. Macro with a reference
    $mac = Open-Window $main "BtnMacros"
    $mlist = Find-ChildByAutomationId $mac "IdList"
    $m = -1
    $items = $mlist.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
    for ($i = 0; $i -lt $items.Count; $i++) { if ($items[$i].Current.Name -match '未使用|unused') { $m = $i; break } }
    Select-ListItemByIndex $mlist $m; Start-Sleep -Milliseconds 300
    Set-ElementValue (Find-ChildByAutomationId $mac "MacroBox") "KC_A KC_B"
    Invoke-Element (Find-ChildByAutomationId $mac "ApplyButton"); Start-Sleep -Milliseconds 400

    $before = Get-AllTopWindowHandles $proc.Id
    Invoke-Element (Find-ChildByAutomationId $main "Key_0")
    $dlg = Find-NewTopWindow $proc.Id $before -timeoutMs 6000
    Start-Sleep -Milliseconds 400
    Set-ElementValue (Find-ChildByAutomationId $dlg "TxtToken") "MACRO($m)"
    Start-Sleep -Milliseconds 200
    Invoke-Element (Find-ChildByAutomationId $dlg "BtnOk"); Start-Sleep -Milliseconds 600

    Set-ForegroundWindowFor $mac
    Select-ListItemByIndex $mlist $m; Start-Sleep -Milliseconds 300
    $before = Get-AllTopWindowHandles $proc.Id
    Invoke-Element (Find-ChildByAutomationId $mac "DeleteItemButton")
    $confirm = Find-NewTopWindow $proc.Id $before -timeoutMs 4000
    $refList = Find-ChildByAutomationId $confirm "ReferenceList"
    $refCount = $refList.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition).Count
    Chk "Delete (macro): the reference list has 1 place (got $refCount)" ($refCount -eq 1)
    Chk "Delete (macro): 'also remove' is on by default" ((Get-ToggleState (Find-ChildByAutomationId $confirm "UnassignCheck")) -eq [System.Windows.Automation.ToggleState]::On)
    Invoke-Element (Find-ChildByAutomationId $confirm "DeleteButton"); Start-Sleep -Milliseconds 400
    Invoke-Element (Find-ChildByAutomationId $mac "OkButton"); Start-Sleep -Milliseconds 600

    $before = Get-AllTopWindowHandles $proc.Id
    Invoke-Element (Find-ChildByAutomationId $main "Key_0")
    $dlg = Find-NewTopWindow $proc.Id $before -timeoutMs 6000
    Start-Sleep -Milliseconds 400
    $tok = Get-ElementValue (Find-ChildByAutomationId $dlg "TxtToken")
    Chk "after OK, Key_0 is no longer MACRO($m) (got '$tok')" ($tok -eq "KC_TRNS")
    Invoke-Element (Find-ChildByAutomationId $dlg "BtnCancel"); Start-Sleep -Milliseconds 300

    "=== FAIL COUNT: $fail ==="
}
catch { "EXCEPTION: $_ at line $($_.InvocationInfo.ScriptLineNumber)"; $fail++ }
finally {
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
}
