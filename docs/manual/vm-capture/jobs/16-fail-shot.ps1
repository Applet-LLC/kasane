. \\100.64.1.22\kasanevm\out\lib\vmlib.ps1
if (-not (Get-Process Install-kasane -ErrorAction SilentlyContinue)) {
    $null = Start-Process C:\kasane-fail\Install-kasane.exe -PassThru
    $h2 = Find-WinByTitle '*セットアップ*' 20000; Start-Sleep 1; Click-ByName (El $h2) 'インストール'; Start-Sleep 8
}
$m = Find-WinByTitle 'Windows インストーラー' 10000
if ($m) {
    # msiexec のエラー画面(ダミーの MSI のため)。前面にして Enter で閉じる
    [void][VmLib.Native]::SetForegroundWindow($m); Start-Sleep -Milliseconds 800
    [System.Windows.Forms.SendKeys]::SendWait('{ENTER}'); Start-Sleep 4
}
Get-Process | Where-Object { $_.MainWindowTitle } | Select-Object ProcessName,MainWindowTitle | Format-Table -AutoSize | Out-String -Width 200
$e = Find-WinByTitle '*失敗*' 30000
if ($e) { Snap $e 'install-launcher-failed' } else { 'launcher failure window not found' }
'done'