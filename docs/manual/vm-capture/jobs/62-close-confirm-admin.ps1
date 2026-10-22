. \\100.64.1.22\kasanevm\out\lib\vmlib.ps1
$prog = '\\100.64.1.22\kasanevm\out\results\62.progress'; Remove-Item $prog -ErrorAction SilentlyContinue
function P($s) { Add-Content $prog $s }
function Names($w) { try { $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.Name } } catch { @() } }
try {
$p = Start-Process 'C:\Program Files\applet\kasane\keymap-gui\kasane.exe' -PassThru; $kp = $p.Id
$h = Find-WinByTitle 'かさね*' 30000; $w = El $h; Start-Sleep 6; P "main: $((El $h).Current.Name)"
$before = Get-AllTopWindowHandles $kp
Invoke-Element (Find-ChildByAutomationId $w 'Key_61'); $d = Find-NewTopWindow $kp $before -timeoutMs 10000
Select-ListItemByIndex (Find-ChildByAutomationId $d 'LstCategories') 1; Start-Sleep -Milliseconds 600
Invoke-Element (Find-ChildByAutomationId $d 'KeycodeButton_KC_D'); Start-Sleep -Milliseconds 400
Invoke-Element (Find-ChildByAutomationId $d 'BtnOk'); Start-Sleep -Seconds 2
P "assigned S -> D; title: $((El $h).Current.Name)"
$b2 = Get-AllTopWindowHandles $kp
$w.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close(); P "close requested"
$c = Find-NewTopWindow $kp $b2 -timeoutMs 10000
$cw = El ([System.IntPtr]$c.Current.NativeWindowHandle); Names $cw | ForEach-Object { P ("  " + $_.Current.Name.Substring(0,[math]::Min(140,$_.Current.Name.Length))) }
Snap $c 'editor-close-confirm-admin'
$no = Names $cw | Where-Object { $_.Current.Name -match '^いいえ' } | Select-Object -First 1; if ($no) { Click-Center $no; P "clicked いいえ" }
Start-Sleep 3; P "editor running: $([bool](Get-Process kasane -ErrorAction SilentlyContinue))"
} catch { P "ERROR: $_" }
'done'