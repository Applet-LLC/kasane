. \\100.64.1.22\kasanevm\out\lib\vmlib.ps1
# 1. 正常な起動画面
$p = Start-Process C:\kasane-dist\Install-kasane.exe -PassThru
$h = Find-WinByTitle '*セットアップ*' 20000
if (-not $h) { 'launcher window not found'; return }
Start-Sleep 1; Snap $h 'install-launcher'
$p | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep 1
# 2. 失敗の画面(空の MSI を置いた別フォルダ)
New-Item -ItemType Directory -Force C:\kasane-fail | Out-Null
Copy-Item C:\kasane-dist\Install-kasane.exe C:\kasane-fail\ -Force
foreach ($n in 'KasaneSetup-ja-JP.msi','KasaneSetup-en-US.msi') { Set-Content C:\kasane-fail\$n 'dummy' }
$p2 = Start-Process C:\kasane-fail\Install-kasane.exe -PassThru
$h2 = Find-WinByTitle '*セットアップ*' 20000
Start-Sleep 1
$w = El $h2; Click-ByName $w 'インストール'
$e = Find-WinByTitle '*失敗*' 60000
if ($e) { Start-Sleep 1; Snap $e 'install-launcher-failed' } else { 'failure dialog not found' }
Get-Process Install-kasane -ErrorAction SilentlyContinue | Stop-Process -Force
'done'