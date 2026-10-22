. \\100.64.1.22\kasanevm\out\lib\vmlib.ps1
$prog = '\\100.64.1.22\kasanevm\out\results\66.progress'; Remove-Item $prog -ErrorAction SilentlyContinue
function P($s) { Add-Content $prog $s }
function Names($w) { try { $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.Name } } catch { @() } }
try {
Get-Process kasane,kasane-license -ErrorAction SilentlyContinue | Stop-Process -Force
$k = Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall','HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall' -ErrorAction SilentlyContinue | Where-Object { (Get-ItemProperty $_.PSPath).DisplayName -match 'かさね' } | Select-Object -First 1
$code = $k.PSChildName; P "product code: $code"
$u = (Get-ItemProperty $k.PSPath).UninstallString; P "uninstall string: $u"
$null = Start-Process msiexec.exe -ArgumentList "/x $code" -PassThru
$sw = [System.Diagnostics.Stopwatch]::StartNew(); $snapped = $false
while ($sw.Elapsed.TotalMinutes -lt 6) {
    $ms = Get-Process msiexec -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 -and $_.MainWindowTitle }
    if (-not $ms -and $sw.Elapsed.TotalSeconds -gt 20 -and -not (Get-Process msiexec -ErrorAction SilentlyContinue)) { P "msiexec finished"; break }
    foreach ($m in $ms) {
        $w = El $m.MainWindowHandle; $n = (Names $w | ForEach-Object { $_.Current.Name.Substring(0,[math]::Min(80,$_.Current.Name.Length)) }) -join ' / '
        P ("{0:mm\:ss} [{1}] {2}" -f $sw.Elapsed, $m.MainWindowTitle, $n)
        $yes = Names $w | Where-Object { $_.Current.Name -match '^はい' } | Select-Object -First 1
        if ($yes -and -not $snapped) { Snap $m.MainWindowHandle 'uninstall-confirm'; $snapped = $true; Click-Center $yes }
    }
    Start-Sleep 4
}
P "services: $((Get-Service kasaneLicenseSvc -ErrorAction SilentlyContinue).Status) / $((Get-Service kbdlayer -ErrorAction SilentlyContinue).Status)"
P "install dir exists: $(Test-Path 'C:\Program Files\applet\kasane')"
P "pending reboot (PendingFileRenameOperations): $([bool](Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager' -Name PendingFileRenameOperations -ErrorAction SilentlyContinue))"
} catch { P "ERROR: $_" }
'done'