. \\100.64.1.22\kasanevm\out\lib\vmlib.ps1
function Names($w) { $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.Name } }
Get-Process kasane -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep 1
# 以前のテストで残っていた設定を、退避する(削除はしない)
$bk = 'C:\kasane-old-settings'; New-Item -ItemType Directory -Force $bk | Out-Null
foreach ($d in 'applet\kasane\30180000','LayerDriver\30180000') {
    $src = Join-Path $env:LOCALAPPDATA $d
    if (Test-Path $src) { $dst = Join-Path $bk ($d -replace '\\','_'); Move-Item $src $dst -Force; "moved: $src -> $dst" }
}
$p = Start-Process 'C:\Program Files\applet\kasane\keymap-gui\kasane.exe' -PassThru
$h = Find-WinByTitle 'かさね*' 25000; $w = El $h; Start-Sleep 4
"title: $((El $h).Current.Name)"
$kp = $p.Id
# クリア(全初期化): ドライバの設定も消す
$before = Get-AllTopWindowHandles $kp
Invoke-Element (Find-ChildByAutomationId $w 'BtnImportExport'); $ie = Find-NewTopWindow $kp $before -timeoutMs 8000
Select-TabById $ie 'TabClear'; Start-Sleep 1
$iw = El ([System.IntPtr]$ie.Current.NativeWindowHandle)
$all = $iw.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Button)))
$sel = $all | Where-Object { $_.Current.Name -like 'すべて選ぶ*' } | Select-Object -First 1
Invoke-Element $sel; Start-Sleep 1
$go = $all | Where-Object { $_.Current.Name -like 'クリアする*' } | Select-Object -First 1
$b2 = Get-AllTopWindowHandles $kp
Invoke-Element $go; $cf = Find-NewTopWindow $kp $b2 -timeoutMs 8000
if ($cf) { $cw = El ([System.IntPtr]$cf.Current.NativeWindowHandle); "confirm:"; Names $cw | ForEach-Object { "  " + $_.Current.Name.Substring(0,[math]::Min(120,$_.Current.Name.Length)) }
  $yes = Names $cw | Where-Object { $_.Current.Name -match '^はい' } | Select-Object -First 1; if ($yes) { Click-Center $yes }; Start-Sleep 4 }
Get-Process | Where-Object { $_.MainWindowTitle } | Select-Object ProcessName,MainWindowTitle | Format-Table -AutoSize | Out-String -Width 200
"main title now: $((El $h).Current.Name)"
'done'