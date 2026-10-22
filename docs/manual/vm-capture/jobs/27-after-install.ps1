. \\100.64.1.22\kasanevm\out\lib\vmlib.ps1
function Names($w) { $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.Name } }
$pm = Get-Process msiexec -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 -and $_.MainWindowTitle } | Select-Object -First 1
if ($pm) { Click-Center (Names (El $pm.MainWindowHandle) | Where-Object { $_.Current.Name -match '^完了' } | Select-Object -First 1); Start-Sleep 4 }
Get-Process | Where-Object { $_.MainWindowTitle } | Select-Object ProcessName,MainWindowTitle | Format-Table -AutoSize | Out-String -Width 200
$l = Find-WinByTitle '*セットアップ*' 3000; if ($l) { "launcher still open"; Snap $l 'install-launcher-after' }
"--- install dir"; Get-ChildItem 'C:\Program Files\applet\kasane' -ErrorAction SilentlyContinue | Select-Object -First 40 Name | Out-String
"--- start menu (all users)"; Get-ChildItem 'C:\ProgramData\Microsoft\Windows\Start Menu\Programs' -Recurse -Include *.lnk -ErrorAction SilentlyContinue | Where-Object { $_.FullName -match 'applet|kasane|かさね' } | Select-Object -Expand FullName | Out-String
"--- services"; Get-Service kasaneLicenseSvc,kbdlayer -ErrorAction SilentlyContinue | Select-Object Name,Status,StartType | Format-Table -AutoSize | Out-String
"--- run keys"; Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run','HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce' -ErrorAction SilentlyContinue | Format-List kasane*,*kasane* | Out-String
"--- env KASANE: [$([Environment]::GetEnvironmentVariable('KASANE','Machine'))]"
"--- driver store"; pnputil /enum-drivers 2>&1 | Select-String -Pattern 'kbdlayer' -Context 0,3 | Out-String
"--- uninstall entry"; Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*','HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -match 'かさね|kasane' } | Select-Object DisplayName,DisplayVersion,Publisher | Format-List | Out-String
'done'