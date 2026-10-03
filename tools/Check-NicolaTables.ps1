<#
.SYNOPSIS
  KeymapTool（C++）とKeymap Editor GUI（C#）のNICOLAの変換表が一致しているか確認する。

.DESCRIPTION
  driver/KeymapTool/NicolaTables.cpp と kbdlayer-keymap-gui/Keycodes/NicolaKana.cs から、
  ローマ字の表・JISかなの表・濁音の表・英字のスキャンコード・記号のスキャンコードを抜き出して比較する。
  どちらかを変更したら実行すること。不一致があれば終了コード1。
  （blobの組み立て全体は、kbdlayer-keymap-gui.Tests の NicolaServiceTests が
   KeymapTool.exe --dump-nicola の出力と照合する。）
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$cpp = Get-Content (Join-Path $root 'driver\KeymapTool\NicolaTables.cpp') -Raw -Encoding utf8
$cs = Get-Content (Join-Path $root 'kbdlayer-keymap-gui\Keycodes\NicolaKana.cs') -Raw -Encoding utf8

# 表の名前から、その直後の最初の "};" までを切り出す。
function Get-Section([string]$text, [string]$name) {
    $m = [regex]::Match($text, "(?s)\b$name\b[^=]*=\s*\{(.*?)\n\s*\};")
    if (-not $m.Success) { throw "$name が見つかりません" }
    return $m.Groups[1].Value
}

function Get-Entries([string]$section, [string]$pattern) {
    $list = @()
    foreach ($m in [regex]::Matches($section, $pattern)) {
        $vals = @()
        for ($i = 1; $i -lt $m.Groups.Count; $i++) { $vals += $m.Groups[$i].Value.ToUpperInvariant() -replace '0X', '0x' }
        $list += ($vals -join '|')
    }
    return ,$list
}

$problems = @()
function Compare-List([string]$label, $a, $b) {
    if ($a.Count -eq 0) { $script:problems += "${label}: C++側の表が空です（正規表現が合っていない）" ; return }
    if (($a -join ',') -ne ($b -join ',')) {
        $script:problems += "${label}が違います"
        $max = [Math]::Max($a.Count, $b.Count)
        for ($i = 0; $i -lt $max; $i++) {
            if ($a[$i] -ne $b[$i]) { $script:problems += "  [$i] C++=$($a[$i])  C#=$($b[$i])" }
        }
    }
}

# ローマ字: C++ {u8"あ", "a"}  C# ("あ", "a")
$romaCpp = Get-Entries (Get-Section $cpp 'kRomajiTable') '\{u8"([^"]+)",\s*"([^"]+)"\}'
$romaCs = Get-Entries (Get-Section $cs 'RomajiTable') '\("([^"]+)",\s*"([^"]+)"\)'
Compare-List 'ローマ字の表' $romaCpp $romaCs

# JISかな: C++ {u8"ぬ", 0x02, false}  C# ("ぬ", 0x02, false)
$jisCpp = Get-Entries (Get-Section $cpp 'kJisKanaTable') '\{u8"([^"]+)",\s*(0x[0-9A-Fa-f]+),\s*(true|false)\}'
$jisCs = Get-Entries (Get-Section $cs 'JisKanaTable') '\("([^"]+)",\s*(0x[0-9A-Fa-f]+),\s*(true|false)\)'
Compare-List 'JISかなの表' $jisCpp $jisCs

# 濁音: C++ {u8"が", u8"か", false}  C# ("が", "か", false)
$dakCpp = Get-Entries (Get-Section $cpp 'kDakutenTable') '\{u8"([^"]+)",\s*u8"([^"]+)",\s*(true|false)\}'
$dakCs = Get-Entries (Get-Section $cs 'DakutenTable') '\("([^"]+)",\s*"([^"]+)",\s*(true|false)\)'
Compare-List '濁音の表' $dakCpp $dakCs

# 英字 a〜z のスキャンコード
$letCpp = Get-Entries (Get-Section $cpp 'kLetterCodes') '(0x[0-9A-Fa-f]+)'
$letCs = Get-Entries (Get-Section $cs 'LetterCodes') '(0x[0-9A-Fa-f]+)'
Compare-List '英字のスキャンコード' $letCpp $letCs

# 記号: C++ case '-': *out = { 0x0C, false };  C# '-' => 0x0C,
$symCpp = Get-Entries $cpp "case '(.)': \*out = \{ (0x[0-9A-Fa-f]+), false \}"
$symCs = Get-Entries $cs "'(.)' => (0x[0-9A-Fa-f]+),"
Compare-List '記号のスキャンコード' $symCpp $symCs

# 文字列の先頭の Shift キー
$shiftCpp = [regex]::Match($cpp, 'kLeftShift\s*=\s*(0x[0-9A-Fa-f]+)').Groups[1].Value
$shiftCs = [regex]::Match($cs, 'LeftShift\s*=\s*(0x[0-9A-Fa-f]+)').Groups[1].Value
if ($shiftCpp -ne $shiftCs) { $problems += "Shiftのスキャンコードが違います C++=$shiftCpp C#=$shiftCs" }

if ($problems.Count -gt 0) {
    $problems | ForEach-Object { Write-Host $_ }
    exit 1
}
Write-Host "OK: ローマ字 $($romaCpp.Count) / JISかな $($jisCpp.Count) / 濁音 $($dakCpp.Count) / 英字 $($letCpp.Count) / 記号 $($symCpp.Count) 件が一致"
exit 0
