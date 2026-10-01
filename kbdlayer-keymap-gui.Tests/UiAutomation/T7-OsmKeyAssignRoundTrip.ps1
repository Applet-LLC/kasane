# GUI-T7: UI Automation test for assigning OSM (and a modded key) via the key-face + key-assign
# dialog AutomationIds added in f56cf8d -- feature/one-shot-mod E-4.
# Flow: click Key_<n> (KeyFace, async Invoke so it returns immediately even though it opens a modal
# dialog) -> FuncCombo="OSM" -> check ModCheck_MOD_LSFT + ModCheck_MOD_LCTL -> InsertButton ->
# TxtToken should read "OSM(MOD_LCTL|MOD_LSFT)" -> BtnOk. Then the same key, reopened, should show
# that same token. Separately, KeycodeButton_KC_A + ModCheck_MOD_LCTL -> LCTL(KC_A) on another slot.
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

    $key0 = Find-ChildByAutomationId $main "Key_0"
    "  Key_0 found, Name='$($key0.Current.Name)'"

    $before = Get-AllTopWindowHandles $proc.Id
    Invoke-Element $key0
    $dlg1 = Find-NewTopWindow $proc.Id $before -timeoutMs 6000
    "  key-assign dialog opened: $($dlg1.Current.Name)"

    # LstCategories order: 10 KeycodeCatalog categories (0=基本..9=メディア), then
    # レイヤー操作(10)/Mod-Tap(11)/Layer-Tap(12)/マクロ・タップダンス(13). Default SelectedIndex=0
    # shows 基本, so レイヤー操作 must be selected explicitly before FuncCombo exists.
    Select-ListItemByIndex (Find-ChildByAutomationId $dlg1 "LstCategories") 10
    Start-Sleep -Milliseconds 300

    $funcCombo = Find-ChildByAutomationId $dlg1 "FuncCombo"
    # "種類" items are MO/TG/DF/OSL/TO/TT/LM/OSM -> OSM is index 7.
    Select-ComboItemByIndex $funcCombo 7
    Start-Sleep -Milliseconds 200

    Set-ToggleState (Find-ChildByAutomationId $dlg1 "ModCheck_MOD_LSFT") $true
    Set-ToggleState (Find-ChildByAutomationId $dlg1 "ModCheck_MOD_LCTL") $true
    Start-Sleep -Milliseconds 200

    Invoke-Element (Find-ChildByAutomationId $dlg1 "InsertButton")
    Start-Sleep -Milliseconds 300
    $tok1 = Get-ElementValue (Find-ChildByAutomationId $dlg1 "TxtToken")
    "  TxtToken after InsertButton: '$tok1'"
    Chk "InsertButton composes OSM(MOD_LCTL|MOD_LSFT)" ($tok1 -eq "OSM(MOD_LCTL|MOD_LSFT)")

    $dlg1Handle = $dlg1.Current.NativeWindowHandle.ToString()
    Invoke-Element (Find-ChildByAutomationId $dlg1 "BtnOk")
    Start-Sleep -Milliseconds 600
    Chk "dialog closed after OK" ((Get-AllTopWindowHandles $proc.Id) -notcontains $dlg1Handle)

    # Reopen the same key to confirm the assignment stuck (in-memory; never saved/applied).
    $before2 = Get-AllTopWindowHandles $proc.Id
    Invoke-Element $key0
    $dlg2 = Find-NewTopWindow $proc.Id $before2 -timeoutMs 6000
    Start-Sleep -Milliseconds 300
    $tok2 = Get-ElementValue (Find-ChildByAutomationId $dlg2 "TxtToken")
    "  reopened TxtToken: '$tok2'"
    Chk "Key_0 round-trips to OSM(MOD_LCTL|MOD_LSFT) on reopen" ($tok2 -eq "OSM(MOD_LCTL|MOD_LSFT)")
    Invoke-Element (Find-ChildByAutomationId $dlg2 "BtnCancel")
    Start-Sleep -Milliseconds 300

    # Second case: a different key (Key_1), KeycodeButton_KC_A + ModCheck_MOD_LCTL -> LCTL(KC_A).
    $before3 = Get-AllTopWindowHandles $proc.Id
    Invoke-Element (Find-ChildByAutomationId $main "Key_1")
    $dlg3 = Find-NewTopWindow $proc.Id $before3 -timeoutMs 6000
    Start-Sleep -Milliseconds 300
    # "英字" (KC_A..KC_Z) is category index 1; default SelectedIndex=0 shows 基本 instead.
    Select-ListItemByIndex (Find-ChildByAutomationId $dlg3 "LstCategories") 1
    Start-Sleep -Milliseconds 300
    Set-ToggleState (Find-ChildByAutomationId $dlg3 "ModCheck_MOD_LCTL") $true
    Start-Sleep -Milliseconds 200
    Invoke-Element (Find-ChildByAutomationId $dlg3 "KeycodeButton_KC_A")
    Start-Sleep -Milliseconds 300
    $tok3 = Get-ElementValue (Find-ChildByAutomationId $dlg3 "TxtToken")
    "  TxtToken after KeycodeButton_KC_A (Ctrl checked): '$tok3'"
    Chk "KeycodeButton_KC_A + ModCheck_MOD_LCTL -> LCTL(KC_A)" ($tok3 -eq "LCTL(KC_A)")
    Invoke-Element (Find-ChildByAutomationId $dlg3 "BtnCancel")
    Start-Sleep -Milliseconds 300

    "=== FAIL COUNT: $fail ==="
}
catch { "EXCEPTION: $_"; $fail++ }
finally {
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
}
