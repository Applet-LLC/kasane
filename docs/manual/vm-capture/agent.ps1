# VM のデスクトップ(対話セッション)で動く。共有の out\jobs\*.ps1 を名前順に 1 つずつ実行し、出力を out\results\<名前>.log に書く。
# 中身は、撮影とインストールの手順だけ。止めるときは、このウィンドウを閉じる(または out\stop.txt を作る)。
$out = '\\100.64.1.22\kasanevm\out'
"agent started $(Get-Date -Format s) as $(whoami)" | Set-Content "$out\results\agent.txt"
while (-not (Test-Path "$out\stop.txt")) {
    $job = Get-ChildItem "$out\jobs" -Filter *.ps1 -ErrorAction SilentlyContinue | Sort-Object Name | Select-Object -First 1
    if (-not $job) { Start-Sleep 2; continue }
    $log = "$out\results\$($job.BaseName).log"
    try { & $job.FullName *>&1 | Out-String | Set-Content $log -Encoding UTF8 } catch { "ERROR: $($_ | Out-String)" | Set-Content $log -Encoding UTF8 }
    Rename-Item $job.FullName "$($job.Name).done" -ErrorAction SilentlyContinue
}
"agent stopped $(Get-Date -Format s)" | Add-Content "$out\results\agent.txt"