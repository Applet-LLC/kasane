. \\100.64.1.22\kasanevm\out\lib\vmlib.ps1
# 設定の「アプリ別レイヤー」から、「実行中のアプリから選ぶ」の画面（apprules-picker）を撮る。VM のウィンドウだけが並ぶ。
Get-Process kasane -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep 1
$p = Start-Process 'C:\Program Files\applet\kasane\keymap-gui\kasane.exe' -PassThru; $kp = $p.Id
$h = Find-WinByTitle (T 'かさね*' 'kasane 1*') 30000; $w = El $h; Start-Sleep 4
$before = Get-AllTopWindowHandles $kp
Invoke-Element (Find-ChildByAutomationId $w 'BtnSettings'); $d = Find-NewTopWindow $kp $before -timeoutMs 8000
Select-TabById $d 'TabAppRules'; Start-Sleep 1
$b3 = Get-AllTopWindowHandles $kp
$add = Find-ByName $d (T '追加...' 'Add...')
if (-not $add) { throw 'add button not found' }
Invoke-Element $add; $e = Find-NewTopWindow $kp $b3 -timeoutMs 6000
$b4 = Get-AllTopWindowHandles $kp
Invoke-Element (Find-ChildByAutomationId $e 'PickWindowButton'); $pk = Find-NewTopWindow $kp $b4 -timeoutMs 6000
Start-Sleep 1; Snap $pk 'apprules-picker'
$pk.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close(); Start-Sleep 1
$e.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close(); Start-Sleep 1
$d.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close(); Start-Sleep 1
Get-Process kasane -ErrorAction SilentlyContinue | Stop-Process -Force
'done'
