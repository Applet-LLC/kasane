. \\100.64.1.22\kasanevm\out\lib\vmlib.ps1
function Names($w) { $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.Name } }
function PanePress($w, $pattern) { $e = Names $w | Where-Object { $_.Current.Name -match $pattern } | Select-Object -First 1; if ($e) { Click-Center $e; $true } else { $false } }
$kp = (Get-Process kasane | Select-Object -First 1).Id
# 確認の画面(いいえ)を閉じる
$c = Find-WinByTitle 'かさね' 5000
if ($c) { $cw = El $c; if (PanePress $cw '^いいえ') { "closed confirm" }; Start-Sleep 1 }
$main = Find-WinByTitle 'かさね*ドライバ*' 8000; $mw = El $main
$before = Get-AllTopWindowHandles $kp
Invoke-Element (Find-ChildByAutomationId $mw 'BtnSettings'); $d = Find-NewTopWindow $kp $before -timeoutMs 8000
# 表示言語を、自動認識(0)に戻す
Select-TabById $d 'TabGeneral'; Start-Sleep 1
Select-ComboItemByIndex (Find-ChildByAutomationId $d 'LanguageBox') 0; Start-Sleep 1
$b2 = Get-AllTopWindowHandles $kp
Invoke-Element (Find-ChildByAutomationId $d 'OkButton'); Start-Sleep 2
$cf = Find-NewTopWindow $kp $b2 -timeoutMs 6000
if ($cf) { if (PanePress (El ([System.IntPtr]$cf.Current.NativeWindowHandle)) '^いいえ') { "restored language (confirm closed)" } }
Start-Sleep 1
# 実行中のアプリから選ぶ
$before = Get-AllTopWindowHandles $kp
Invoke-Element (Find-ChildByAutomationId $mw 'BtnSettings'); $d = Find-NewTopWindow $kp $before -timeoutMs 8000
Select-TabById $d 'TabAppRules'; Start-Sleep 1
$b3 = Get-AllTopWindowHandles $kp
$add = $d.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.AndCondition((New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty,'追加...')),(New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Button)))))
Invoke-Element $add; $e = Find-NewTopWindow $kp $b3 -timeoutMs 6000
$b4 = Get-AllTopWindowHandles $kp
Invoke-Element (Find-ChildByAutomationId $e 'PickWindowButton'); $pk = Find-NewTopWindow $kp $b4 -timeoutMs 6000
Start-Sleep 1; Snap $pk 'apprules-picker'
"-- picker list:"; (El ([System.IntPtr]$pk.Current.NativeWindowHandle)).FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::ListItem))) | ForEach-Object { "  " + $_.Current.Name }
$pk.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close(); Start-Sleep 1
$e.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close(); Start-Sleep 1
$d.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close(); Start-Sleep 1
Get-Process kasane -ErrorAction SilentlyContinue | Stop-Process -Force
"lang setting: $((Get-Content (Join-Path $env:LOCALAPPDATA 'applet\kasane\tray.json') -ErrorAction SilentlyContinue))"
'done'