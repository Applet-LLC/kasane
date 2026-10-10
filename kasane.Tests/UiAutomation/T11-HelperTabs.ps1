# GUI-T11: UI Automation test for the settings dialog's "ヘルパー動作" and "アプリ別レイヤー" tabs (P4, 2026-10-08).
# Adds a rule and an action through their editor windows and checks the lists and the editors' validation.
# Nothing is saved: the settings dialog is always closed with キャンセル, so helper.json / tray.json are not touched.
. "$PSScriptRoot\UiaLib.ps1"
$ErrorActionPreference = "Stop"
$EXE = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\..\kasane\bin\Debug\net8.0-windows\win-x64\kasane.exe"))
$fail = 0
function Chk([string]$label, [bool]$cond) {
    $v = if ($cond) { 'PASS' } else { 'FAIL'; $script:fail++ }
    "  [$v] $label"
}
function Count-Items([System.Windows.Automation.AutomationElement]$list) {
    return $list.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition).Count
}
# ラジオボタンは InvokePattern ではなく SelectionItemPattern で選ぶ。
function Select-Radio([System.Windows.Automation.AutomationElement]$el) {
    $p = $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    $p.Select()
}
function Open-Settings($main, $procId) {
    $before = Get-AllTopWindowHandles $procId
    Invoke-Element (Find-ChildByAutomationId $main "BtnSettings")
    return Find-NewTopWindow $procId $before -timeoutMs 6000
}

$proc = $null
try {
    $proc = Start-Process $EXE -PassThru
    Start-Sleep -Milliseconds 1500
    $main = Find-TopWindowByProcess $proc.Id -timeoutMs 8000
    Set-ForegroundWindowFor $main
    $dlg = Open-Settings $main $proc.Id

    # ---- アプリ別レイヤー ----
    Select-TabById $dlg "TabAppRules"
    $list = Find-ChildByAutomationId $dlg "RulesList"
    $n0 = Count-Items $list
    "  rules before: $n0"
    Chk "rules tab has the scope radio buttons and the list" ((Find-ChildByAutomationId $dlg "RulesScopeGlobal") -and $list)
    Select-Radio (Find-ChildByAutomationId $dlg "RulesScopeGlobal")

    $before = Get-AllTopWindowHandles $proc.Id
    Invoke-Element (Find-ChildByAutomationId $dlg "AddRuleButton")
    $ed = Find-NewTopWindow $proc.Id $before -timeoutMs 6000
    Start-Sleep -Milliseconds 300
    $edHandle = $ed.Current.NativeWindowHandle.ToString()
    Chk "an empty rule is rejected (no condition)" ((Find-ChildByAutomationId $ed "RuleStatus").Current.Name -match "照合条件がありません")
    Invoke-Element (Find-ChildByAutomationId $ed "OkButton")
    Start-Sleep -Milliseconds 500
    Chk "editor stays open while invalid" ((Get-AllTopWindowHandles $proc.Id) -contains $edHandle)

    Set-ElementValue (Find-ChildByAutomationId $ed "RuleTitleRegex") "(["
    Start-Sleep -Milliseconds 300
    Chk "a broken regex is reported" ((Find-ChildByAutomationId $ed "RuleStatus").Current.Name -match "TitleRegex")
    Set-ElementValue (Find-ChildByAutomationId $ed "RuleTitleRegex") ""
    Set-ElementValue (Find-ChildByAutomationId $ed "RuleExe") "t11test.exe"
    Set-ToggleState (Find-ChildByAutomationId $ed "RuleLayer3") $true
    Start-Sleep -Milliseconds 300
    Chk "a valid rule shows OK" ((Find-ChildByAutomationId $ed "RuleStatus").Current.Name -eq "OK")
    Invoke-Element (Find-ChildByAutomationId $ed "OkButton")
    Start-Sleep -Milliseconds 600
    Chk "editor closes on OK" ((Get-AllTopWindowHandles $proc.Id) -notcontains $edHandle)
    Chk "the rule is added to the list" ((Count-Items $list) -eq ($n0 + 1))

    # ---- ヘルパー動作 ----
    Select-TabById $dlg "TabHelper"
    $actions = Find-ChildByAutomationId $dlg "ActionsList"
    $triggers = Find-ChildByAutomationId $dlg "TriggersList"
    $a0 = Count-Items $actions
    $t0 = Count-Items $triggers
    Select-Radio (Find-ChildByAutomationId $dlg "HelperScopeGlobal")

    $before = Get-AllTopWindowHandles $proc.Id
    Invoke-Element (Find-ChildByAutomationId $dlg "AddActionButton")
    $ed = Find-NewTopWindow $proc.Id $before -timeoutMs 6000
    Start-Sleep -Milliseconds 300
    $edHandle = $ed.Current.NativeWindowHandle.ToString()
    Chk "a Launch action without a path is rejected" ((Find-ChildByAutomationId $ed "ActionStatus").Current.Name -match "Path が空")
    Set-ElementValue (Find-ChildByAutomationId $ed "ActionName") "t11"
    Set-ElementValue (Find-ChildByAutomationId $ed "ActionPath") "notepad.exe"
    Start-Sleep -Milliseconds 300
    Chk "a Launch action with a path shows OK" ((Find-ChildByAutomationId $ed "ActionStatus").Current.Name -eq "OK")
    Select-ComboItemByIndex (Find-ChildByAutomationId $ed "ActionType") 2    # Sequence
    Start-Sleep -Milliseconds 300
    Set-ElementValue (Find-ChildByAutomationId $ed "ActionSteps") "key LCTL(KC_C)`ndelay 50`nbogus 1"
    Start-Sleep -Milliseconds 300
    Chk "a bad step line is reported with its line number" ((Find-ChildByAutomationId $ed "ActionStatus").Current.Name -match "3行目")
    Set-ElementValue (Find-ChildByAutomationId $ed "ActionSteps") "key LCTL(KC_C)`ndelay 50`ntext hello"
    Start-Sleep -Milliseconds 300
    Chk "a good sequence shows OK" ((Find-ChildByAutomationId $ed "ActionStatus").Current.Name -eq "OK")
    Invoke-Element (Find-ChildByAutomationId $ed "OkButton")
    Start-Sleep -Milliseconds 600
    Chk "editor closes on OK" ((Get-AllTopWindowHandles $proc.Id) -notcontains $edHandle)
    Chk "the action is added to the list" ((Count-Items $actions) -eq ($a0 + 1))

    $before = Get-AllTopWindowHandles $proc.Id
    Invoke-Element (Find-ChildByAutomationId $dlg "AddTriggerButton")
    $ed = Find-NewTopWindow $proc.Id $before -timeoutMs 6000
    Start-Sleep -Milliseconds 300
    Set-ElementValue (Find-ChildByAutomationId $ed "TriggerKey") "KC_NOPE"
    Start-Sleep -Milliseconds 300
    Chk "an unknown trigger key is reported" ((Find-ChildByAutomationId $ed "ActionStatus").Current.Name -match "KC_NOPE")
    Set-ElementValue (Find-ChildByAutomationId $ed "TriggerKey") "KC_F13"
    Select-ComboItemByIndex (Find-ChildByAutomationId $ed "ActionType") 4    # PauseToggle
    Start-Sleep -Milliseconds 300
    Chk "a PauseToggle trigger shows OK" ((Find-ChildByAutomationId $ed "ActionStatus").Current.Name -eq "OK")
    Invoke-Element (Find-ChildByAutomationId $ed "OkButton")
    Start-Sleep -Milliseconds 600
    Chk "the trigger is added to the list" ((Count-Items $triggers) -eq ($t0 + 1))

    # 何も保存しない（キャンセル）
    Invoke-Element (Find-ChildByAutomationId $dlg "CancelButton")
    Start-Sleep -Milliseconds 300
    "=== FAIL COUNT: $fail ==="
}
catch { "EXCEPTION: $_"; $fail++ }
finally {
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
}
