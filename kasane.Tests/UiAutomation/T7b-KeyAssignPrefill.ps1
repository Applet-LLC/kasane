# GUI-T7b: reopening a key that already has MT / LT shows that assignment in the panel (GUI plan 1-1,
# feature/gui-key-assign-edit). Before, the panels always came back with KC_A / KC_SPACE / layer 1.
# Flow (in memory only; nothing is saved or applied):
#  1. Key_2: open -> Mod-Tap category (11) -> TapKeyBox should be the key's own keycode (from the key name
#     "(KC_xxx)") instead of KC_A. Write MT(MOD_LSFT,KC_F) in TxtToken -> OK.
#  2. Key_2 again: the dialog opens on Mod-Tap with ModCombo = MOD_LSFT and TapKeyBox = KC_F.
#  3. Key_3: TxtToken = LT(3,KC_ENTER) -> OK. Reopen: Layer-Tap with LayerCombo = 3 and TapKeyBox = KC_ENTER.
#  4. Key_3: TxtToken = TG(4) -> OK. Reopen: layer actions with FuncCombo = TG and LayerCombo = 4.
. "$PSScriptRoot\UiaLib.ps1"
$ErrorActionPreference = "Stop"
$EXE = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\..\kasane\bin\Debug\net8.0-windows\win-x64\kasane.exe"))
$fail = 0
function Chk([string]$label, [bool]$cond) {
    $v = if ($cond) { 'PASS' } else { 'FAIL'; $script:fail++ }
    "  [$v] $label"
}

function Open-Key($main, [string]$keyId) {
    $before = Get-AllTopWindowHandles $proc.Id
    Invoke-Element (Find-ChildByAutomationId $main $keyId)
    $dlg = Find-NewTopWindow $proc.Id $before -timeoutMs 6000
    Start-Sleep -Milliseconds 400
    return $dlg
}

function Assign-Token($main, [string]$keyId, [string]$token) {
    $dlg = Open-Key $main $keyId
    Set-ElementValue (Find-ChildByAutomationId $dlg "TxtToken") $token
    Start-Sleep -Milliseconds 200
    Invoke-Element (Find-ChildByAutomationId $dlg "BtnOk")
    Start-Sleep -Milliseconds 600
}

$proc = $null
try {
    $proc = Start-Process $EXE -PassThru
    Start-Sleep -Milliseconds 1500
    $main = Find-TopWindowByProcess $proc.Id -timeoutMs 8000
    Set-ForegroundWindowFor $main

    # 1. A fresh Mod-Tap starts from the key's own keycode.
    $name2 = (Find-ChildByAutomationId $main "Key_2").Current.Name
    $own = if ($name2 -match '\((KC_[A-Z0-9_]+)\)') { $Matches[1] } else { $null }
    "  Key_2 name='$name2' own keycode='$own'"
    $dlg = Open-Key $main "Key_2"
    Select-ListItemByIndex (Find-ChildByAutomationId $dlg "LstCategories") 11
    Start-Sleep -Milliseconds 300
    $tap = Get-ElementValue (Find-ChildByAutomationId $dlg "TapKeyBox")
    Chk "new Mod-Tap tap key is the key's own keycode ($own), got '$tap'" ($own -and $tap -eq $own)
    Invoke-Element (Find-ChildByAutomationId $dlg "BtnCancel")
    Start-Sleep -Milliseconds 300

    # 2. An existing MT is expanded.
    Assign-Token $main "Key_2" "MT(MOD_LSFT,KC_F)"
    $dlg = Open-Key $main "Key_2"
    $mod = Get-ComboSelectedName (Find-ChildByAutomationId $dlg "ModCombo")
    $tap = Get-ElementValue (Find-ChildByAutomationId $dlg "TapKeyBox")
    Chk "reopened MT: ModCombo=MOD_LSFT (got '$mod')" ($mod -eq "MOD_LSFT")
    Chk "reopened MT: TapKeyBox=KC_F (got '$tap')" ($tap -eq "KC_F")
    Invoke-Element (Find-ChildByAutomationId $dlg "BtnCancel")
    Start-Sleep -Milliseconds 300

    # 3. An existing LT is expanded.
    Assign-Token $main "Key_3" "LT(3,KC_ENTER)"
    $dlg = Open-Key $main "Key_3"
    $layer = Get-ComboSelectedName (Find-ChildByAutomationId $dlg "LayerCombo")
    $tap = Get-ElementValue (Find-ChildByAutomationId $dlg "TapKeyBox")
    Chk "reopened LT: LayerCombo=3 (got '$layer')" ($layer -match '^3(:|$)')
    Chk "reopened LT: TapKeyBox=KC_ENTER (got '$tap')" ($tap -eq "KC_ENTER")
    Invoke-Element (Find-ChildByAutomationId $dlg "BtnCancel")
    Start-Sleep -Milliseconds 300

    # 4. An existing layer action is expanded.
    Assign-Token $main "Key_3" "TG(4)"
    $dlg = Open-Key $main "Key_3"
    $func = Get-ComboSelectedName (Find-ChildByAutomationId $dlg "FuncCombo")
    $layer = Get-ComboSelectedName (Find-ChildByAutomationId $dlg "LayerCombo")
    Chk "reopened TG: FuncCombo=TG (got '$func')" ($func -eq "TG")
    Chk "reopened TG: LayerCombo=4 (got '$layer')" ($layer -match '^4(:|$)')
    Invoke-Element (Find-ChildByAutomationId $dlg "BtnCancel")
    Start-Sleep -Milliseconds 300

    "=== FAIL COUNT: $fail ==="
}
catch { "EXCEPTION: $_"; $fail++ }
finally {
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
}
