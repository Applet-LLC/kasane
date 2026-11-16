. \\100.64.1.22\kasanevm\out\lib\vmlib.ps1
# 管理者のエディタで、キーボードの一覧（editor-keyboard-list）、Caps Lock と左 Ctrl の入れ替え（editor-swapped）、適用した状態（editor-applied）を撮る。
# 45-clear.ps1 で初期状態にしてから行う。画像には、ボタンの並び（割り当ての一覧…、設定…）と、ろキーの表示（_）、版の表記（1.02）が写る。
Get-Process kasane -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep 1
$p = Start-Process 'C:\Program Files\applet\kasane\keymap-gui\kasane.exe' -PassThru; $kp = $p.Id
$h = Find-WinByTitle (T 'かさね*' 'kasane 1*') 30000; $w = El $h; Start-Sleep 5
"title: $((El $h).Current.Name)"
function Assign($keyId, $catIndex, $btnId) {
    $before = Get-AllTopWindowHandles $kp
    Invoke-Element (Find-ChildByAutomationId $w $keyId)
    $d = Find-NewTopWindow $kp $before -timeoutMs 8000
    Select-ListItemByIndex (Find-ChildByAutomationId $d 'LstCategories') $catIndex; Start-Sleep -Milliseconds 600
    Invoke-Element (Find-ChildByAutomationId $d $btnId); Start-Sleep -Milliseconds 400
    Invoke-Element (Find-ChildByAutomationId $d 'BtnOk'); Start-Sleep -Milliseconds 800
}
$combo = Find-ChildByAutomationId $w 'CmbDevice'
$combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); Start-Sleep -Milliseconds 800
Snap $h 'editor-keyboard-list'
$combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse(); Start-Sleep -Milliseconds 500
Assign 'Key_59' 5 'KeycodeButton_KC_LCTRL'
Assign 'Key_93' 3 'KeycodeButton_KC_CAPSLOCK'
Start-Sleep 1; Snap $h 'editor-swapped'
Invoke-Element (Find-ChildByAutomationId $w 'BtnApply'); Start-Sleep 4
"status: $((Find-ChildByAutomationId $w 'TxtStatus').Current.Name)"
Snap $h 'editor-applied'
'done'
