. \\100.64.1.22\kasanevm\out\lib\vmlib.ps1
# 【昇格していないエージェント（agent.ps1）で実行する】
# kasane.Tests\UiAutomation\Capture-Manual.ps1 を、インストール済みの kasane.exe に対して実行する（閲覧専用モードの画面が写る）。
# 共有の out\lib\ に、Capture-Manual.ps1 と UiaLib.ps1 を置いておく（README を参照）。
# 画面の表示倍率は、150% にしておく（リポジトリにある、ホストで撮った画像が、1902x891 で、150% のため）。
# 撮る画面: 日本語は、1.02 で変わった editor-main と editor-readonly だけ。英語は、すべて。out\only.txt があれば、それ（カンマ区切りの前方一致）に従う。
$dir = 'C:\kasane-capture'
New-Item -ItemType Directory -Force $dir | Out-Null
Copy-Item "$script:ShareOut\lib\Capture-Manual.ps1", "$script:ShareOut\lib\UiaLib.ps1" $dir -Force
$only = if (Test-Path "$script:ShareOut\only.txt") { @((Get-Content "$script:ShareOut\only.txt" -Raw).Trim() -split '\s*,\s*' | Where-Object { $_ }) }
        elseif ($script:Lang -eq 'ja') { @('editor-main', 'editor-readonly') }
        else { @() }
"lang: $script:Lang  only: [$($only -join ',')]  scale: $([int]((Get-ItemProperty 'HKCU:\Control Panel\Desktop\WindowMetrics' -Name AppliedDPI -ErrorAction SilentlyContinue).AppliedDPI))dpi"
Get-Process kasane -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep 1
$exe = 'C:\Program Files\applet\kasane\keymap-gui\kasane.exe'
# Capture-Manual.ps1 は、環境変数 KASANE を一時フォルダ(C:\kasane-data)にして、終わると、そのフォルダを消す。エージェントのプロセスに KASANE が残ると、
# あとのジョブで起動する kasane が、存在しないフォルダを設定フォルダにして、表示言語も自動(日本語)に戻ってしまう。そこで、終わったら、環境変数を戻す。
Remove-Item 'C:\kasane-data' -Recurse -Force -ErrorAction SilentlyContinue
$oldKasane = $env:KASANE
# Capture-Manual.ps1 は、別の PowerShell プロセスで、最上位のスクリプトとして動かす。入れ子(& で呼ぶ)だと、スクリプトの関数が、GetNewClosure のブロックから見えなくなる。
$cmArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "$dir\Capture-Manual.ps1", '-Lang', $script:Lang, '-Exe', $exe, '-OutDir', $script:ImgDir)
if ($only.Count -gt 0) { $cmArgs += '-Only', ($only -join ',') }
try { powershell @cmArgs *>&1 | Out-String -Width 200 } finally { if ($null -eq $oldKasane) { Remove-Item Env:KASANE -ErrorAction SilentlyContinue } else { $env:KASANE = $oldKasane }; Remove-Item 'C:\kasane-data' -Recurse -Force -ErrorAction SilentlyContinue }
'done'
