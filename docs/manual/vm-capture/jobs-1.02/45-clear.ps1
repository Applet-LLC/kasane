. \\100.64.1.22\kasanevm\out\lib\vmlib.ps1
# 撮影の前に、ドライバの設定も含めて、全部を初期状態にする（以前のテストの設定が残っていると、エディタを開いたときに、適用されてしまうため）。
# インポート／エクスポート → 初期化 → すべて選ぶ → 初期化する... → 確認の OK。
function Names($w) { try { $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.Name } } catch { @() } }
Get-Process kasane -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep 1
$p = Start-Process 'C:\Program Files\applet\kasane\keymap-gui\kasane.exe' -PassThru; $kp = $p.Id
$h = Find-WinByTitle (T 'かさね*' 'kasane 1*') 30000; $w = El $h; Start-Sleep 4
"title: $((El $h).Current.Name)"
$before = Get-AllTopWindowHandles $kp
Invoke-Element (Find-ChildByAutomationId $w 'BtnImportExport'); $ie = Find-NewTopWindow $kp $before -timeoutMs 8000
Select-TabById $ie 'TabClear'; Start-Sleep 1
$iw = El ([System.IntPtr]$ie.Current.NativeWindowHandle)
Invoke-Element (Find-ChildByAutomationId $iw 'ClearSelectAllButton'); Start-Sleep 1
$b2 = Get-AllTopWindowHandles $kp
Invoke-Element (Find-ChildByAutomationId $iw 'ClearDoButton')
$cf = Find-NewTopWindow $kp $b2 -timeoutMs 8000
if ($cf) {
    $cw = El ([System.IntPtr]$cf.Current.NativeWindowHandle)
    'confirm:'; Names $cw | ForEach-Object { '  ' + $_.Current.Name.Substring(0, [math]::Min(120, $_.Current.Name.Length)) }
    $ok = Names $cw | Where-Object { $_.Current.Name -eq 'OK' } | Select-Object -First 1
    if ($ok) { Click-Center $ok }
    Start-Sleep 4
    # 結果の知らせが出たら、OK で閉じる
    foreach ($hh in (Get-AllTopWindowHandles $kp)) {
        $e = El ([System.IntPtr][int64]$hh)
        $okb = Names $e | Where-Object { $_.Current.Name -eq 'OK' } | Select-Object -First 1
        if ($okb -and $e.Current.Name -ne $h) { Click-Center $okb; Start-Sleep 1 }
    }
} else { 'confirm dialog not found' }
Get-Process | Where-Object { $_.MainWindowTitle } | Select-Object ProcessName,MainWindowTitle | Format-Table -AutoSize | Out-String -Width 200
Get-Process kasane -ErrorAction SilentlyContinue | Stop-Process -Force
'done'
