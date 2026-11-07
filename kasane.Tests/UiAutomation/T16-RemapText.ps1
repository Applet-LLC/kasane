# GUI-T16: the assignment list window (GUI plan 2-2, feature/gui-remap-text). In memory only.
#  1. "Assignment list..." opens the window; the text has the header and the layer 0 section.
#  2. Change Key_0 in the editor, press "Refresh": the new assignment appears as a "key ... -> ..." line.
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

    $before = Get-AllTopWindowHandles $proc.Id
    Invoke-Element (Find-ChildByAutomationId $main "BtnRemapText")
    $w = Find-NewTopWindow $proc.Id $before -timeoutMs 6000
    Start-Sleep -Milliseconds 500
    $text = Get-ElementValue (Find-ChildByAutomationId $w "RemapText")
    Chk "the list has the header" ($text -match '割り当ての一覧|assignment list')
    Chk "the list has the layer 0 section" ($text -match '## (レイヤー0|Layer 0)')

    # Change Key_0 to KC_F24 (in memory) and refresh.
    $before = Get-AllTopWindowHandles $proc.Id
    Invoke-Element (Find-ChildByAutomationId $main "Key_0")
    $dlg = Find-NewTopWindow $proc.Id $before -timeoutMs 6000
    Start-Sleep -Milliseconds 400
    Set-ElementValue (Find-ChildByAutomationId $dlg "TxtToken") "KC_F24"
    Start-Sleep -Milliseconds 200
    Invoke-Element (Find-ChildByAutomationId $dlg "BtnOk"); Start-Sleep -Milliseconds 600

    Invoke-Element (Find-ChildByAutomationId $w "RefreshButton"); Start-Sleep -Milliseconds 400
    $text2 = Get-ElementValue (Find-ChildByAutomationId $w "RemapText")
    Chk "after Refresh, the changed key is listed" ($text2 -match 'key .+ -> F24 +: KC_F24')
    Invoke-Element (Find-ChildByAutomationId $w "CloseButton"); Start-Sleep -Milliseconds 300

    "=== FAIL COUNT: $fail ==="
}
catch { "EXCEPTION: $_"; $fail++ }
finally {
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
}
