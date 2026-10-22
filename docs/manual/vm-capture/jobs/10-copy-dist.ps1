New-Item -ItemType Directory -Force C:\kasane-dist | Out-Null
robocopy '\\100.64.1.22\kasanevm\dist' C:\kasane-dist /E /NFL /NDL /NJH /NP | Select-Object -Last 4
Get-ChildItem C:\kasane-dist | Select-Object Name,Length | Format-Table -AutoSize | Out-String