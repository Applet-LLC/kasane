. \\100.64.1.22\kasanevm\out\lib\vmlib.ps1
# License Manager。最初の画面（license-input）と、無効なキーを入れたときのエラー（license-error）。
# キーは、スクリプトに書かず、共有の out\keys\ のファイルから読む（trial.txt、subscription.txt。1 行にキーだけ。無効なキーは、偽のキーをこのジョブに直接書く）。
function Get-Key([string]$name) {
    $p = "$script:ShareOut\keys\$name.txt"
    if (-not (Test-Path $p)) { throw "key file not found: $p" }
    (Get-Content $p -Raw).Trim()
}
Get-Process Install-kasane,msiexec -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process kasane, kasane-license -ErrorAction SilentlyContinue | Stop-Process -Force
# 再起動の前に認証した状態が残っていると、最初の画面が出ない。ライセンスの状態を戻す(32 と同じ)。
Stop-Service kasaneLicenseSvc -Force
Remove-Item 'C:\ProgramData\kbdlayer-license\license-svc\licensestate.dat' -Force -ErrorAction SilentlyContinue
Start-Service kasaneLicenseSvc; Start-Sleep 3
Start-Sleep 1
"internet: $((Test-NetConnection gumroad.com -Port 443 -WarningAction SilentlyContinue).TcpTestSucceeded)"
$null = Start-Process 'C:\Program Files\applet\kasane\license-gui\kasane-license.exe' -PassThru
$h = Find-WinByTitle '*License Manager*' 20000
if (-not $h) { Get-Process | Where-Object { $_.MainWindowTitle } | Select-Object ProcessName,MainWindowTitle | Format-Table -AutoSize | Out-String -Width 200; return }
$w = El $h; Start-Sleep 3
Snap $h 'license-input'
Set-ElementValue (Find-ChildByAutomationId $w 'TxtLicenseKey') 'AAAAAAAA-AAAAAAAA-AAAAAAAA-AAAAAAAA'   # 無効なキーの例。実在しない、明らかに偽のキー(invalid.txt は使わない。本物のキーを画像に写さないため)
Invoke-Element (Find-ChildByAutomationId $w 'BtnActivate'); Start-Sleep 10
"status: $((Find-ChildByAutomationId $w 'TxtStatus').Current.Name)"
Snap $h 'license-error'
'done'
