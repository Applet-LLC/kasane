. \\100.64.1.22\kasanevm\out\lib\vmlib.ps1
# トレイを起動する（インストール直後は、ログオンするまで起動しない）。英語のときは、28-language.ps1 のあとなので、英語で出る。
Get-Process kasane-license,kasane -ErrorAction SilentlyContinue | Stop-Process -Force
"kbdlayer: $((Get-Service kbdlayer -ErrorAction SilentlyContinue).Status)  license svc: $((Get-Service kasaneLicenseSvc -ErrorAction SilentlyContinue).Status)"
if (-not (Get-Process kasane-tray* -ErrorAction SilentlyContinue)) { $null = Start-Process 'C:\Program Files\applet\kasane\tray\kasane-tray.exe'; Start-Sleep 8 }
Get-Process kasane-tray* -ErrorAction SilentlyContinue | Select-Object Name,Id | Out-String
$t = Find-TrayButton
"tray button: $(if ($t) { $t.Current.Name } else { 'not found (is it hidden in the overflow area? pin the icon first)' })"
'done'
