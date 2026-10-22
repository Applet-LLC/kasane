. \\100.64.1.22\kasanevm\out\lib\vmlib.ps1
function Names($w) { $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.Name } }
$kp = (Get-Process kasane | Select-Object -First 1).Id
$done = $false
foreach ($hh in (Get-AllTopWindowHandles $kp)) {
    $e = El ([System.IntPtr][int64]$hh); $n = Names $e
    if (($n | Where-Object { $_.Current.Name -like '*初期状態に戻します*' })) {
        $ok = $n | Where-Object { $_.Current.Name -eq 'OK' } | Select-Object -First 1
        if ($ok) { Click-Center $ok; $done = $true; "clicked OK on clear confirm" }
    }
}
Start-Sleep 4
foreach ($hh in (Get-AllTopWindowHandles $kp)) {
    $e = El ([System.IntPtr][int64]$hh); "win: $($e.Current.Name)"
    Names $e | Where-Object { $_.Current.ControlType.ProgrammaticName -eq 'ControlType.Text' -and $_.Current.Name.Length -gt 12 } | Select-Object -First 4 | ForEach-Object { "    " + $_.Current.Name.Substring(0,[math]::Min(140,$_.Current.Name.Length)) }
}
'done'