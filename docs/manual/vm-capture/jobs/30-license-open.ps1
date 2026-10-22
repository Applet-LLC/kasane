. \\100.64.1.22\kasanevm\out\lib\vmlib.ps1
Get-Process Install-kasane,msiexec -ErrorAction SilentlyContinue | Stop-Process -Force
"internet: $((Test-NetConnection gumroad.com -Port 443 -WarningAction SilentlyContinue).TcpTestSucceeded)"
$null = Start-Process 'C:\Program Files\applet\kasane\license-gui\kasane-license.exe' -PassThru
$h = Find-WinByTitle '*License Manager*' 20000
if (-not $h) { Get-Process | Where-Object { $_.MainWindowTitle } | Select-Object ProcessName,MainWindowTitle | Format-Table -AutoSize | Out-String -Width 200; return }
Start-Sleep 3; Snap $h 'license-first'
(El $h).FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.AutomationId -or $_.Current.Name } | ForEach-Object { "  [{0}] id={1} name={2}" -f $_.Current.ControlType.ProgrammaticName.Replace('ControlType.',''), $_.Current.AutomationId, $_.Current.Name.Substring(0,[math]::Min(60,$_.Current.Name.Length)) }
'done'