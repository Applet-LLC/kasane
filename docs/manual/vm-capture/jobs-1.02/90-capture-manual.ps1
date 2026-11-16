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
& "$dir\Capture-Manual.ps1" -Lang $script:Lang -Exe $exe -OutDir $script:ImgDir -Only $only
'done'
