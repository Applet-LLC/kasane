. \\100.64.1.22\kasanevm\out\lib\vmlib.ps1
function WizHwnd { $p = Get-Process msiexec -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 -and $_.MainWindowTitle } | Select-Object -First 1; if ($p) { $p.MainWindowHandle } }
function Names($w) { $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.Name } }
function Dump($w) { Names $w | ForEach-Object { "  [{0}] {1}" -f $_.Current.ControlType.ProgrammaticName.Replace('ControlType.',''), $_.Current.Name.Substring(0, [math]::Min(90, $_.Current.Name.Length)) } }
for ($i = 1; $i -le 12; $i++) {
    $h = WizHwnd; if (-not $h) { "page ${i}: no wizard window"; break }
    $w = El $h; Start-Sleep -Milliseconds 800
    Snap $h ('wiz-{0:00}' -f $i)
    "== page $i"; $d = Dump $w; $d
    if ($i -gt 1 -and ($d -match '設定フォルダ')) { "  -> stop before data dir page"; break }
    $agree = Names $w | Where-Object { $_.Current.Name -match '同意' } | Select-Object -First 1
    if ($agree) { Click-Center $agree; "  -> 同意をクリック"; Start-Sleep -Milliseconds 600 }
    $next = Names $w | Where-Object { $_.Current.Name -match '^次へ' } | Select-Object -First 1
    if (-not $next) { "  -> no next button"; break }
    Click-Center $next; Start-Sleep 2
}
'done'