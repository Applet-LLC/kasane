<#
.SYNOPSIS
  emacs.kasane.json のレイヤー・マクロ・キー置換を、KeymapTool でキーボードに書き込む（管理者権限が必要）。
.DESCRIPTION
  キーボードのレイヤーマップ（レイヤー 0〜2）を、このファイルの内容で置き換えます。もとの設定は消えます。
  -Remove で、このファイルが使う項目（レイヤーマップ全体、マクロ、キー置換）を消します。
.EXAMPLE
  .\apply-emacs.ps1 -Id CDEC0000
  .\apply-emacs.ps1 -Id CDEC0000 -Remove
#>
param(
    [Parameter(Mandatory)][string]$Id,
    [switch]$Remove,
    [string]$File,
    [string]$KeymapTool = 'C:\Program Files\applet\kasane\KeymapTool.exe'
)
$ErrorActionPreference = 'Stop'
# $PSScriptRoot は、powershell.exe -File で起動すると param の既定値の中では空になるので、ここで補う。
if (-not $File) { $File = Join-Path $PSScriptRoot 'emacs.kasane.json' }
if (-not (Test-Path $KeymapTool)) { throw "KeymapTool.exe が見つかりません: $KeymapTool" }
$Id = $Id -replace '^0[xX]', ''

# 指定した ID のキーボードを、ドライバが認識しているかを先に確かめる(認識していないと、書き込みは 1168 で失敗する)。
$listing = (& $KeymapTool --list | Out-String)
if ($listing -notmatch "UniqId=$Id\b") {
    throw "ID $Id のキーボードを、ドライバが認識していません。KeymapTool --list の表示にある UniqId を指定してください。`n$listing"
}
$kb = (Get-Content $File -Raw -Encoding UTF8 | ConvertFrom-Json).Keyboards[0]

function Run([string[]]$ToolArgs, [switch]$IgnoreError) {
    & $KeymapTool @ToolArgs
    if ($LASTEXITCODE -ne 0 -and -not $IgnoreError) { throw "KeymapTool が失敗しました(終了コード $LASTEXITCODE): $($ToolArgs -join ' ')" }
}

if ($Remove) {
    # レイヤーマップは、キーボード 1 台に 1 つなので、全体を消す。キー置換とマクロは、このファイルの番号だけを消す。
    Run @('--off', $Id) -IgnoreError
    Run @('--clear', $Id) -IgnoreError
    foreach ($o in $kb.KeyOverrides) { Run @('--set-key-override', $Id, "$($o.Id)") }
    foreach ($m in $kb.Macros) { Run @('--set-macro', $Id, "$($m.Id)") }
    return
}

# レイヤー: Layers[].Keys の "SC_xxxx" (スキャンコード) を、KeymapTool の --apply-simple 用の 2 つのファイルにする。
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("emacs-apply-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
    $layers = @($kb.Layers | Sort-Object Index)
    $codes = @($layers[0].Keys.PSObject.Properties.Name | Sort-Object)
    $pos = for ($i = 0; $i -lt $codes.Count; $i++) { '{{"row":0,"col":{0},"scanCode":{1}}}' -f $i, [Convert]::ToInt32($codes[$i].Substring(3), 16) }
    Set-Content (Join-Path $tmp 'profile.json') ('{ "positions": [ ' + ($pos -join ',') + ' ] }') -Encoding ASCII
    $rows = foreach ($layer in $layers) {
        $keys = foreach ($c in $codes) { $p = $layer.Keys.PSObject.Properties[$c]; if ($p) { '"' + $p.Value + '"' } else { '"KC_TRNS"' } }
        '[ ' + ($keys -join ', ') + ' ]'
    }
    Set-Content (Join-Path $tmp 'keymap.json') ('{ "layout": [ ' + ($rows -join ', ') + ' ] }') -Encoding ASCII

    # マクロとキー置換を先に書く。レイヤーマップが、MACRO(n) と OSL(n)・TG(n) を使うため。
    foreach ($m in $kb.Macros) { Run (@('--set-macro', $Id, "$($m.Id)") + @($m.Keys)) }
    foreach ($o in $kb.KeyOverrides) { Run @('--set-key-override', $Id, "$($o.Id)", $o.Mods, $o.Key, $o.Replacement) }
    Run @('--off', $Id) -IgnoreError
    Run @('--clear', $Id) -IgnoreError
    Run @('--apply-simple', $Id, (Join-Path $tmp 'keymap.json'), (Join-Path $tmp 'profile.json'))
    Run @('--on', $Id)
} finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host "書き込みました: レイヤー $($kb.Layers.Count) 枚、マクロ $($kb.Macros.Count) 件、キー置換 $($kb.KeyOverrides.Count) 件。機能(マクロ・キー置換)が OFF でないことも確かめてください。"
