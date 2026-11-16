. \\100.64.1.22\kasanevm\out\lib\vmlib.ps1
# License Manager。最初の画面（license-input）と、無効なキーを入れたときのエラー（license-error）。
# キーは、スクリプトに書かず、共有の out\keys\ のファイルから読む（invalid.txt、trial.txt、subscription.txt。1 行にキーだけ）。
function Get-Key([string]$name) {
    $p = "$script:ShareOut\keys\$name.txt"
    if (-not (Test-Path $p)) { throw "key file not found: $p" }
    (Get-Content $p -Raw).Trim()
}
Get-Process Install-kasane,msiexec -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process kasane-license -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep 1
"internet: $((Test-NetConnection gumroad.com -Port 443 -WarningAction SilentlyContinue).TcpTestSucceeded)"
$null = Start-Process 'C:\Program Files\applet\kasane\license-gui\kasane-license.exe' -PassThru
$h = Find-WinByTitle '*License Manager*' 20000
if (-not $h) { Get-Process | Where-Object { $_.MainWindowTitle } | Select-Object ProcessName,MainWindowTitle | Format-Table -AutoSize | Out-String -Width 200; return }
$w = El $h; Start-Sleep 3
Snap $h 'license-input'
Set-ElementValue (Find-ChildByAutomationId $w 'TxtLicenseKey') (Get-Key 'invalid')
Invoke-Element (Find-ChildByAutomationId $w 'BtnActivate'); Start-Sleep 10
"status: $((Find-ChildByAutomationId $w 'TxtStatus').Current.Name)"
Snap $h 'license-error'
'done'
