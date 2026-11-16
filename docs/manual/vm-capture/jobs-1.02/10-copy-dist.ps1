. \\100.64.1.22\kasanevm\out\lib\vmlib.ps1
# 配布物（Subscription 版: Install-kasane.exe と 2 つの MSI）を、VM のローカルにコピーする。共有の dist\ に、1.02 の配布物を入れておく。
"lang: $script:Lang"
New-Item -ItemType Directory -Force C:\kasane-dist | Out-Null
robocopy '\\100.64.1.22\kasanevm\dist' C:\kasane-dist /E /NFL /NDL /NJH /NP | Select-Object -Last 4
Get-ChildItem C:\kasane-dist | Select-Object Name,Length | Format-Table -AutoSize | Out-String
