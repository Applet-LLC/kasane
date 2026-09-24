<#
.SYNOPSIS
  KeymapTool（C++）とKeymap Editor GUI（C#）のキーコード表が一致しているか確認する。

.DESCRIPTION
  driver/KeymapTool/KeycodeTable.cpp と kbdlayer-keymap-gui/Keycodes/KeycodeTable.cs から
  ("KC_xxx", SC1(0x..)) / {"KC_xxx", SCE0(0x..)} / SC(0xE1, 0x..) の組を抜き出して比較する。
  どちらかの表を変更したら実行すること。不一致があれば終了コード1。
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$cppPath = Join-Path $root 'driver\KeymapTool\KeycodeTable.cpp'
$csPath = Join-Path $root 'kbdlayer-keymap-gui\Keycodes\KeycodeTable.cs'

$pattern = '"(KC_\w+)",\s*(SC1\(0x[0-9A-Fa-f]+\)|SCE0\(0x[0-9A-Fa-f]+\)|SC\(0x[0-9A-Fa-f]+,\s*0x[0-9A-Fa-f]+\))'

function Read-Table([string]$path) {
    $table = [ordered]@{}
    foreach ($m in [regex]::Matches((Get-Content $path -Raw -Encoding utf8), $pattern)) {
        $name = $m.Groups[1].Value
        $sc = ($m.Groups[2].Value -replace '\s', '').ToUpperInvariant() -replace '0X', '0x'
        if ($table.Contains($name)) { throw "$path : $name が2回定義されています" }
        $table[$name] = $sc
    }
    return $table
}

$cpp = Read-Table $cppPath
$cs = Read-Table $csPath
$problems = @()

foreach ($name in $cpp.Keys) {
    if (-not $cs.Contains($name)) { $problems += "C#側に無い: $name = $($cpp[$name])" }
    elseif ($cs[$name] -ne $cpp[$name]) { $problems += "値が違う: $name  C++=$($cpp[$name])  C#=$($cs[$name])" }
}
foreach ($name in $cs.Keys) {
    if (-not $cpp.Contains($name)) { $problems += "C++側に無い: $name = $($cs[$name])" }
}
# 逆変換（スキャンコード→名前）で使われる正式名は「先に書いたもの」なので、並び順も揃っている必要がある。
$cppOrder = @($cpp.Keys) -join ','
$csOrder = @($cs.Keys) -join ','
if ($problems.Count -eq 0 -and $cppOrder -ne $csOrder) { $problems += "キーコードの並び順が違います（同じスキャンコードの正式名が変わる可能性があります）" }

if ($problems.Count -gt 0) {
    $problems | ForEach-Object { Write-Host $_ -ForegroundColor Red }
    exit 1
}
Write-Host "OK: $($cpp.Count) 個のキーコードが一致しています。" -ForegroundColor Green
