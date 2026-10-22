. \\100.64.1.22\kasanevm\out\lib\vmlib.ps1
$prog = '\\100.64.1.22\kasanevm\out\results\63.progress'; Remove-Item $prog -ErrorAction SilentlyContinue
function P($s) { Add-Content $prog $s }
try {
# 1. OFF を押した状態
$p = Start-Process 'C:\Program Files\applet\kasane\keymap-gui\kasane.exe' -PassThru
$h = Find-WinByTitle 'かさね*' 30000; $w = El $h; Start-Sleep 6
Invoke-Element (Find-ChildByAutomationId $w 'BtnOff'); Start-Sleep 3
P "title: $((El $h).Current.Name)  status: $((Find-ChildByAutomationId $w 'TxtStatus').Current.Name)"
Snap $h 'uninstall-off'
Get-Process kasane -ErrorAction SilentlyContinue | Stop-Process -Force
# 2. 設定の「インストールされているアプリ」
Get-Process SystemSettings -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep 1
Start-Process 'ms-settings:appsfeatures'; Start-Sleep 6
$sh = Find-WinByTitle '設定' 20000; P "settings hwnd: $sh"
if ($sh) {
    $sw = El $sh
    $edit = $sw.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Edit))) | Select-Object -First 1
    if ($edit) { try { Set-ElementValue $edit 'かさね' } catch { P "search failed: $_" }; Start-Sleep 3 }
    Snap $sh 'uninstall-apps'
    $sw.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.Name -match 'かさね|kasane' } | Select-Object -First 8 | ForEach-Object { P ("  item: " + $_.Current.Name) }
}
} catch { P "ERROR: $_" }
'done'