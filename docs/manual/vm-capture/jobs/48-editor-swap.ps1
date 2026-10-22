. \\100.64.1.22\kasanevm\out\lib\vmlib.ps1
$kp = (Get-Process kasane | Select-Object -First 1).Id
$h = Find-WinByTitle 'かさね*' 10000; $w = El $h
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
"title: $((El $h).Current.Name)"
"status: $((Find-ChildByAutomationId $w 'TxtStatus').Current.Name)"
Snap $h 'editor-applied'
'done'