# GUI-T7c: the key-assign dialog's Combo (16) and Key override (17) categories (GUI plan 1-2,
# feature/gui-key-assign-defaults). In memory only; every editor is cancelled, nothing is saved or applied.
#  1. Key_0 (Esc): category 16 -> OK is disabled. "Create a combo with this key" opens the combo editor with
#     Member1Combo = this key.
#  2. Category 17 -> "Create a key override with this key" opens the key override editor with KeyBox = KC_ESC.
#  3. Category 0 again -> OK is enabled again.
. "$PSScriptRoot\UiaLib.ps1"
$ErrorActionPreference = "Stop"
$EXE = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\..\kasane\bin\Debug\net8.0-windows\win-x64\kasane.exe"))
$fail = 0
function Chk([string]$label, [bool]$cond) {
    $v = if ($cond) { 'PASS' } else { 'FAIL'; $script:fail++ }
    "  [$v] $label"
}
function IsEnabled($el) { $el.Current.IsEnabled }

$proc = $null
try {
    $proc = Start-Process $EXE -PassThru
    Start-Sleep -Milliseconds 1500
    $main = Find-TopWindowByProcess $proc.Id -timeoutMs 8000
    Set-ForegroundWindowFor $main

    $key0 = Find-ChildByAutomationId $main "Key_0"
    $keyName = $key0.Current.Name
    $before = Get-AllTopWindowHandles $proc.Id
    Invoke-Element $key0
    $dlg = Find-NewTopWindow $proc.Id $before -timeoutMs 6000
    Start-Sleep -Milliseconds 400
    $cats = Find-ChildByAutomationId $dlg "LstCategories"

    # 1. Combo
    Select-ListItemByIndex $cats 16
    Start-Sleep -Milliseconds 400
    Chk "OK is disabled on the Combo category" (-not (IsEnabled (Find-ChildByAutomationId $dlg "BtnOk")))
    $before = Get-AllTopWindowHandles $proc.Id
    Invoke-Element (Find-ChildByAutomationId $dlg "CreateComboFromKeyButton")
    $combo = Find-NewTopWindow $proc.Id $before -timeoutMs 6000
    Start-Sleep -Milliseconds 600
    $m1 = Get-ComboSelectedName (Find-ChildByAutomationId $combo "Member1Combo")
    $m2 = Get-ComboSelectedName (Find-ChildByAutomationId $combo "Member2Combo")
    "  key='$keyName' member1='$m1' member2='$m2'"
    Chk "combo editor: key 1 is this key" ($m1 -eq $keyName.Split(' ')[0] -or $m1 -like "$($keyName.Split(' ')[0])*")
    Chk "combo editor: key 2 is unset" ($m2 -match '未使用|unused')
    # 1b. Choose key 2 and press OK: the open key-assign dialog's combo panel must refresh at once (hardware test C3).
    Select-ComboItemByIndex (Find-ChildByAutomationId $combo "Member2Combo") 2
    Invoke-Element (Find-ChildByAutomationId $combo "OkButton")
    Start-Sleep -Milliseconds 800
    $relatedList = $null
    try { $relatedList = Find-ChildByAutomationId $dlg "RelatedComboList" -timeoutMs 1500 } catch { }
    Chk "after OK in the combo editor, the dialog lists the new combo" ($null -ne $relatedList)
    Chk "after OK, 'create a combo' is disabled (the key is used)" (-not (IsEnabled (Find-ChildByAutomationId $dlg "CreateComboFromKeyButton")))

    # 2. Key override
    Select-ListItemByIndex $cats 17
    Start-Sleep -Milliseconds 400
    Chk "OK is disabled on the Key override category" (-not (IsEnabled (Find-ChildByAutomationId $dlg "BtnOk")))
    $before = Get-AllTopWindowHandles $proc.Id
    Invoke-Element (Find-ChildByAutomationId $dlg "CreateKeyOverrideFromKeyButton")
    $ko = Find-NewTopWindow $proc.Id $before -timeoutMs 6000
    Start-Sleep -Milliseconds 600
    $kb = Get-ElementValue (Find-ChildByAutomationId $ko "KeyBox")
    Chk "key override editor: KeyBox = KC_ESC (got '$kb')" ($kb -eq "KC_ESC")
    Invoke-Element (Find-ChildByAutomationId $ko "CancelButton")
    Start-Sleep -Milliseconds 400

    # 3. Back to a normal category
    Select-ListItemByIndex $cats 0
    Start-Sleep -Milliseconds 300
    Chk "OK is enabled again on a normal category" (IsEnabled (Find-ChildByAutomationId $dlg "BtnOk"))
    Invoke-Element (Find-ChildByAutomationId $dlg "BtnCancel")
    Start-Sleep -Milliseconds 300

    "=== FAIL COUNT: $fail ==="
}
catch { "EXCEPTION: $_"; $fail++ }
finally {
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
}
