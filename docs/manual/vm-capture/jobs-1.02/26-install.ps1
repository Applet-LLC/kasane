. \\100.64.1.22\kasanevm\out\lib\vmlib.ps1
# 「インストール」を押して、進行中のページ（install-wizard-progress）と完了のページ（install-wizard-finish）を撮り、完了を押す。
function Names($w) { $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.Name } }
$p = Get-Process msiexec | Where-Object { $_.MainWindowHandle -ne 0 -and $_.MainWindowTitle } | Select-Object -First 1
$h = $p.MainWindowHandle; $w = El $h
Click-Center (Names $w | Where-Object { $_.Current.Name -match (T '^インストール\(I\)' '^&?Install') } | Select-Object -First 1)
Start-Sleep 6
$progressPat = T 'インストールしています|お待ちください' 'Please wait|installs'
$finishPat = T '完了\(F\)|完了しました|インストールを完了' 'Finish|successfully|Completed'
$snapped = $false; $sw = [System.Diagnostics.Stopwatch]::StartNew()
while ($sw.Elapsed.TotalMinutes -lt 7) {
    $pm = Get-Process msiexec -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 -and $_.MainWindowTitle } | Select-Object -First 1
    if ($pm) {
        $names = (Names (El $pm.MainWindowHandle) | ForEach-Object { $_.Current.Name }) -join ' / '
        "{0:mm\:ss}  {1}" -f $sw.Elapsed, $names.Substring(0, [math]::Min(160, $names.Length))
        if (-not $snapped -and $names -match $progressPat) { Snap $pm.MainWindowHandle 'install-wizard-progress'; $snapped = $true }
        if ($names -match $finishPat) { Snap $pm.MainWindowHandle 'install-wizard-finish'; 'FINISH PAGE'; break }
    }
    Get-Process | Where-Object { $_.MainWindowTitle -match (T 'セキュリティ|警告|確認' 'Security|Warning|Confirm') } | ForEach-Object { Snap ([System.IntPtr]$_.MainWindowHandle) ('install-extra-' + $_.Id) }
    Start-Sleep 5
}
# 完了を押す
$pm = Get-Process msiexec -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 -and $_.MainWindowTitle } | Select-Object -First 1
if ($pm) { Click-Center (Names (El $pm.MainWindowHandle) | Where-Object { $_.Current.Name -match (T '^完了' '^&?Finish') } | Select-Object -First 1); Start-Sleep 4 }
Get-Process | Where-Object { $_.MainWindowTitle } | Select-Object ProcessName,MainWindowTitle | Format-Table -AutoSize | Out-String -Width 200
"--- services"; Get-Service kasaneLicenseSvc,kbdlayer -ErrorAction SilentlyContinue | Select-Object Name,Status,StartType | Format-Table -AutoSize | Out-String
"--- installed version: $((Get-Item 'C:\Program Files\applet\kasane\keymap-gui\kasane.exe').VersionInfo.FileVersion)"
'done'
