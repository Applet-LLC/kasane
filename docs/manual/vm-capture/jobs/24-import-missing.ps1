. \\100.64.1.22\kasanevm\out\lib\vmlib.ps1
function WizHwnd { $p = Get-Process msiexec -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 -and $_.MainWindowTitle } | Select-Object -First 1; if ($p) { $p.MainWindowHandle } }
function Names($w) { $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.Name } }
function Dump($w) { Names $w | ForEach-Object { "  [{0}] {1}" -f $_.Current.ControlType.ProgrammaticName.Replace('ControlType.',''), $_.Current.Name.Substring(0, [math]::Min(80, $_.Current.Name.Length)) } }
$h = WizHwnd; $w = El $h
Click-Center (Names $w | Where-Object { $_.Current.Name -eq '設定ファイルを取り込む' } | Select-Object -First 1); Start-Sleep -Milliseconds 700
# 入力欄(RichEdit)をクリックして、存在しないパスを入れる
$edit = $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.ClassName -eq 'RichEdit20W' } | Select-Object -First 1
Click-Center $edit; Start-Sleep -Milliseconds 500
[System.Windows.Forms.SendKeys]::SendWait('D:\kasane-backup\settings.kasane.json'); Start-Sleep -Milliseconds 600
Snap $h 'install-wizard-import-filled'
Click-Center (Names $w | Where-Object { $_.Current.Name -match '^次へ' } | Select-Object -First 1); Start-Sleep 3
Get-Process | Where-Object { $_.MainWindowTitle } | Select-Object ProcessName,MainWindowTitle | Format-Table -AutoSize | Out-String -Width 200
$m = Find-WinByTitle '*見つかりません*' 8000
if ($m) { Snap $m 'install-wizard-import-missing'; "-- dialog:"; Dump (El $m) } else { 'missing-file dialog not found' }
'done'