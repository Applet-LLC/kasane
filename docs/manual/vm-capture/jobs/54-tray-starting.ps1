. \\100.64.1.22\kasanevm\out\lib\vmlib.ps1
Get-Process kasane -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep 2
$tp = (Get-Process kasane-tray | Select-Object -First 1).Id
$t = Find-TrayButton
Click-Center $t
$got = $false
for ($i = 0; $i -lt 100 -and -not $got; $i++) {
    foreach ($hh in (Get-AllTopWindowHandles $tp)) {
        $e = El ([System.IntPtr][int64]$hh)
        $txt = ($e.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name }) -join ' / '
        "found window: name=[$($e.Current.Name)] text=[$txt] rect=$($e.Current.BoundingRectangle)"
        if ($txt -match '準備') { Snap ([System.IntPtr][int64]$hh) 'tray-starting'; $got = $true; break }
    }
    if (-not $got) { Start-Sleep -Milliseconds 150 }
}
if (-not $got) { 'starting notice not seen' }
Start-Sleep 6
"editor running: $([bool](Get-Process kasane -ErrorAction SilentlyContinue))"
'done'