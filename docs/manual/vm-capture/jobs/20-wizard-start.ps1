. \\100.64.1.22\kasanevm\out\lib\vmlib.ps1
Get-Process Install-kasane -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep 1
$null = Start-Process C:\kasane-dist\Install-kasane.exe -PassThru
$h = Find-WinByTitle '*セットアップ*' 20000; Start-Sleep 1
Click-ByName (El $h) 'インストール'
Start-Sleep 12
Get-Process | Where-Object { $_.MainWindowTitle } | Select-Object ProcessName,MainWindowTitle | Format-Table -AutoSize | Out-String -Width 200
'done'