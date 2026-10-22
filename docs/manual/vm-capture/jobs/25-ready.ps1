. \\100.64.1.22\kasanevm\out\lib\vmlib.ps1
function WizHwnd { $p = Get-Process msiexec -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 -and $_.MainWindowTitle -eq '設定ファイルの取り込み' } | Select-Object -First 1; if ($p) { $p.MainWindowHandle } }
function Names($w) { $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.Name } }
function Dump($w) { Names $w | ForEach-Object { "  [{0}] {1}" -f $_.Current.ControlType.ProgrammaticName.Replace('ControlType.',''), $_.Current.Name.Substring(0, [math]::Min(80, $_.Current.Name.Length)) } }
$m = Find-WinByTitle '*見つかりません*' 5000
if ($m) { Click-Center (Names (El $m) | Where-Object { $_.Current.Name -eq 'OK' } | Select-Object -First 1); Start-Sleep 1 }
$h = Find-WinByTitle '設定ファイルの取り込み' 5000; $w = El $h
Click-Center (Names $w | Where-Object { $_.Current.Name -eq '設定ファイルを取り込む' } | Select-Object -First 1); Start-Sleep -Milliseconds 600
Click-Center (Names $w | Where-Object { $_.Current.Name -match '^次へ' } | Select-Object -First 1); Start-Sleep 3
Get-Process | Where-Object { $_.MainWindowTitle } | Select-Object ProcessName,MainWindowTitle | Format-Table -AutoSize | Out-String -Width 200
$p = Get-Process msiexec | Where-Object { $_.MainWindowHandle -ne 0 -and $_.MainWindowTitle } | Select-Object -First 1
$h2 = $p.MainWindowHandle; Snap $h2 'install-wizard-ready'; Dump (El $h2)
'done'