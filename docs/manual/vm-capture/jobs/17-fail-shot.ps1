. \\100.64.1.22\kasanevm\out\lib\vmlib.ps1
$m = Find-WinByTitle 'Windows インストーラー' 5000
if ($m) {
    $ok = Find-ByName (El $m) 'OK'
    if ($ok) { "OK ボタン: $($ok.Current.BoundingRectangle)  patterns: $(($ok.GetSupportedPatterns() | ForEach-Object { $_.ProgrammaticName }) -join ',')"; Click-Center $ok; Start-Sleep 4 } else { 'OK element not found' }
}
Get-Process | Where-Object { $_.MainWindowTitle } | Select-Object ProcessName,MainWindowTitle | Format-Table -AutoSize | Out-String -Width 200
$e = Find-WinByTitle '*失敗*' 30000
if ($e) { Snap $e 'install-launcher-failed' } else { 'launcher failure window not found' }
'done'