# 昇格した版。stop2.txt があれば止まる。
$out = '\\100.64.1.22\kasanevm\out'
"agent2 started $(Get-Date -Format s) as $(whoami) elevated=$(([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))" | Set-Content "$out\results\agent2.txt"
while (-not (Test-Path "$out\stop2.txt")) {
    $job = Get-ChildItem "$out\jobs2" -Filter *.ps1 -ErrorAction SilentlyContinue | Sort-Object Name | Select-Object -First 1
    if (-not $job) { Start-Sleep 2; continue }
    $log = "$out\results\$($job.BaseName).log"
    try { & $job.FullName *>&1 | Out-String | Set-Content $log -Encoding UTF8 } catch { "ERROR: $($_ | Out-String)" | Set-Content $log -Encoding UTF8 }
    Rename-Item $job.FullName "$($job.Name).done" -ErrorAction SilentlyContinue
}
"agent2 stopped $(Get-Date -Format s)" | Add-Content "$out\results\agent2.txt"