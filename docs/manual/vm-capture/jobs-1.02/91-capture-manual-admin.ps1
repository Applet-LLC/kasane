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
& "$dir\Capture-Manual.ps1" -Lang $script:Lang -Exe $exe -OutDir $script:ImgDir -Only $only -Admin
'done'
