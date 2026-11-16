. \\100.64.1.22\kasanevm\out\lib\vmlib.ps1
# アンインストールを実行し、確認の画面（uninstall-confirm）を撮る。確認は Windows Installer の画面で、OS の言語（日本語）で出るため、英語の撮影でも「はい」を押す（英語のヘルプも、日本語の画像を使う）。
function Names($w) { try { $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.Name } } catch { @() } }
Get-Process kasane,kasane-license,kasane-tray -ErrorAction SilentlyContinue | Stop-Process -Force
$k = Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall' -ErrorAction SilentlyContinue |
    Where-Object { (Get-ItemProperty $_.PSPath).DisplayName -match 'kasane|かさね' } | Select-Object -First 1
if (-not $k) { throw 'uninstall entry not found' }
$code = $k.PSChildName; "product code: $code"
$null = Start-Process msiexec.exe -ArgumentList "/x $code" -PassThru
$sw = [System.Diagnostics.Stopwatch]::StartNew(); $snapped = $false
while ($sw.Elapsed.TotalMinutes -lt 6) {
    $ms = Get-Process msiexec -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 -and $_.MainWindowTitle }
    if (-not $ms -and $sw.Elapsed.TotalSeconds -gt 20 -and -not (Get-Process msiexec -ErrorAction SilentlyContinue)) { 'msiexec finished'; break }
    foreach ($m in $ms) {
        $w = El $m.MainWindowHandle
        $yes = Names $w | Where-Object { $_.Current.Name -match '^(はい|&?Yes)' } | Select-Object -First 1
        if ($yes -and -not $snapped) { Snap $m.MainWindowHandle 'uninstall-confirm'; $snapped = $true; Click-Center $yes }
    }
    Start-Sleep 4
}
"services: $((Get-Service kasaneLicenseSvc -ErrorAction SilentlyContinue).Status) / $((Get-Service kbdlayer -ErrorAction SilentlyContinue).Status)"
"install dir exists: $(Test-Path 'C:\Program Files\applet\kasane')"
'done'
