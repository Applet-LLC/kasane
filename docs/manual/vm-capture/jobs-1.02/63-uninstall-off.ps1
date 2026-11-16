. \\100.64.1.22\kasanevm\out\lib\vmlib.ps1
# OFF を押した状態のエディタ（uninstall-off-raw）。uninstall-off は、annotate-images.ps1 が、これのタイトルバーと下部を切り出して作る。
Get-Process kasane -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep 1
$p = Start-Process 'C:\Program Files\applet\kasane\keymap-gui\kasane.exe' -PassThru
$h = Find-WinByTitle (T 'かさね*' 'kasane 1*') 30000; $w = El $h; Start-Sleep 6
Invoke-Element (Find-ChildByAutomationId $w 'BtnOff'); Start-Sleep 3
"title: $((El $h).Current.Name)  status: $((Find-ChildByAutomationId $w 'TxtStatus').Current.Name)"
Snap $h 'uninstall-off-raw'
Get-Process kasane -ErrorAction SilentlyContinue | Stop-Process -Force
'done'
