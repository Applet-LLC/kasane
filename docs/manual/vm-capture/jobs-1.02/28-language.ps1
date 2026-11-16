. \\100.64.1.22\kasanevm\out\lib\vmlib.ps1
# 英語で撮るときだけ、設定フォルダの tray.json に、表示言語（English）を書く。kasane、トレイ、License Manager が、これに従う。
# 日本語のときは、何もしない（自動認識。VM の OS が日本語なので、日本語になる）。
$dir = Join-Path $env:LOCALAPPDATA 'applet\kasane'
$machine = [Environment]::GetEnvironmentVariable('KASANE', 'Machine')
if ($machine -and [System.IO.Path]::IsPathRooted($machine)) { $dir = $machine }
"data dir: $dir  (lang $script:Lang)"
if ($script:Lang -eq 'en') {
    New-Item -ItemType Directory -Force $dir | Out-Null
    $path = Join-Path $dir 'tray.json'
    $cfg = if (Test-Path $path) { Get-Content $path -Raw -Encoding UTF8 | ConvertFrom-Json } else { [pscustomobject]@{} }
    Add-Member -InputObject $cfg -NotePropertyName UiLanguage -NotePropertyValue 'English' -Force
    [System.IO.File]::WriteAllText($path, ($cfg | ConvertTo-Json -Depth 10), (New-Object System.Text.UTF8Encoding($false)))
    "wrote: $(Get-Content $path -Raw)"
}
'done'
