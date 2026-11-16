. \\100.64.1.22\kasanevm\out\lib\vmlib.ps1
# 【昇格したエージェント（agent2.ps1）で実行する】
# Capture-Manual.ps1 -Admin。管理者モードの画面（-admin の付く画像。ボタンの並びが写る editor-main-admin など）を撮る。
# 撮る画面: 日本語は、editor-main-admin だけ。英語は、すべて。out\only.txt があれば、それに従う。
$dir = 'C:\kasane-capture'
New-Item -ItemType Directory -Force $dir | Out-Null
Copy-Item "$script:ShareOut\lib\Capture-Manual.ps1", "$script:ShareOut\lib\UiaLib.ps1" $dir -Force
$only = if (Test-Path "$script:ShareOut\only.txt") { @((Get-Content "$script:ShareOut\only.txt" -Raw).Trim() -split '\s*,\s*' | Where-Object { $_ }) }
        elseif ($script:Lang -eq 'ja') { @('editor-main-admin') }
        else { @() }
"lang: $script:Lang  only: [$($only -join ',')]"
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
$cmArgs += '-Admin'
try { powershell @cmArgs *>&1 | Out-String -Width 200 } finally { if ($null -eq $oldKasane) { Remove-Item Env:KASANE -ErrorAction SilentlyContinue } else { $env:KASANE = $oldKasane }; Remove-Item 'C:\kasane-data' -Recurse -Force -ErrorAction SilentlyContinue }
'done'
