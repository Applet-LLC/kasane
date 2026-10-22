. \\100.64.1.22\kasanevm\out\lib\vmlib.ps1
function Names($w) { $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.Name } }
$p = Get-Process msiexec | Where-Object { $_.MainWindowHandle -ne 0 -and $_.MainWindowTitle } | Select-Object -First 1
$h = $p.MainWindowHandle; $w = El $h
Click-Center (Names $w | Where-Object { $_.Current.Name -match '^インストール\(I\)' } | Select-Object -First 1)
Start-Sleep 6
$snapped = $false; $sw = [System.Diagnostics.Stopwatch]::StartNew()
while ($sw.Elapsed.TotalMinutes -lt 7) {
    $titles = Get-Process | Where-Object { $_.MainWindowTitle } | ForEach-Object { "$($_.ProcessName):$($_.MainWindowTitle)" }
    "{0:mm\:ss}  {1}" -f $sw.Elapsed, ($titles -join ' | ')
    $pm = Get-Process msiexec -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 -and $_.MainWindowTitle } | Select-Object -First 1
    if ($pm) {
        $names = (Names (El $pm.MainWindowHandle) | ForEach-Object { $_.Current.Name }) -join ' / '
        "      msi: " + $names.Substring(0, [math]::Min(200, $names.Length))
        if (-not $snapped -and $names -match 'インストールしています|お待ちください') { Snap $pm.MainWindowHandle 'install-wizard-progress'; $snapped = $true }
        if ($names -match '完了\(F\)|完了しました|インストールを完了') { Snap $pm.MainWindowHandle 'install-wizard-finish'; "FINISH PAGE"; break }
    }
    Get-Process | Where-Object { $_.MainWindowTitle -match 'セキュリティ|警告|確認' } | ForEach-Object { Snap ([System.IntPtr]$_.MainWindowHandle) ('install-extra-' + $_.Id) }
    Start-Sleep 5
}
'done'