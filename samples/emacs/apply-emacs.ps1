<#
.SYNOPSIS
  emacs.kasane.json のレイヤー・マクロ・キー置換を、KeymapTool でキーボードに書き込む（管理者権限が必要）。
.DESCRIPTION
  キーボードのレイヤーマップ（レイヤー 0〜2。0 が通常、1 が C-x、2 が C-q）を、このファイルの内容で置き換えます。もとの設定は消えます。
  C-q（のどかの &Prefix(KeymapDefault) に当たる、次の 1 キーをそのまま送る操作）は、キー置換 34 番（OSL(2)）と、レイヤー 2 の MACRO(92〜118) です。
  -Remove で、このファイルが使う項目（レイヤーマップ全体、マクロ、キー置換）を消します。
  -KillLine を付けると、C-k（Ctrl+K）を、マクロではなく、トレイのヘルパー動作 KillLine にします（のどかの &EmacsEditKillLine と同じ。
  続けて C-k を押した分がクリップボードにつながり、行末の C-k は改行を削除します）。トレイが動いている必要があります。
  ヘルパー動作は、キーボードの helper.json に Id 100 で足します（ほかの動作は変えません）。
  -Excel を付けると、emacs-excel.kasane.json を使います。Excel が前面のときだけ、Ctrl+L が Ctrl+PageUp（前のシート）、
  Ctrl+; が Ctrl+PageDown（次のシート）になります。空のレイヤー 3 と、レイヤー 3 が立っている間だけ効くキー置換 35・36 番
  （layers=3）を足し、helper.json に EXCEL.EXE でレイヤー 3 を立てるアプリ別レイヤーの規則を足します
  （同じ名前の規則は置き換え、ほかの規則は変えません）。キー置換のレイヤー条件に対応したドライバが要ります。
  トレイが動いている必要があります。
.EXAMPLE
  .\apply-emacs.ps1 -Id CDEC0000
  .\apply-emacs.ps1 -Id CDEC0000 -Excel
  .\apply-emacs.ps1 -Id CDEC0000 -Remove
#>
param(
    [Parameter(Mandatory)][string]$Id,
    [switch]$Remove,
    [switch]$KillLine,
    [switch]$Excel,
    [string]$File,
    [string]$KeymapTool = 'C:\Program Files\applet\kasane\KeymapTool.exe'
)
$ErrorActionPreference = 'Stop'
# $PSScriptRoot は、powershell.exe -File で起動すると param の既定値の中では空になるので、ここで補う。
if (-not $File) { $File = Join-Path $PSScriptRoot $(if ($Excel) { 'emacs-excel.kasane.json' } else { 'emacs.kasane.json' }) }
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

# トレイの設定の置き場所（環境変数 KASANE が絶対パスならそれ、なければ %LOCALAPPDATA%\applet\kasane）。
$dataDir = if ($env:KASANE -and [IO.Path]::IsPathRooted($env:KASANE)) { $env:KASANE } else { Join-Path $env:LOCALAPPDATA 'applet\kasane' }
$helperPath = Join-Path $dataDir (Join-Path $Id 'helper.json')
$KillLineId = 100
# このファイルが helper.json に足すアプリ別レイヤーの規則（emacs-excel.kasane.json の Helper.AppLayerRules）。
$fileRules = @($kb.Helper.AppLayerRules | Where-Object { $_ })
# 名前で見分けて足し直す・消す規則。-Remove は、どちらのファイルで書き込んだ分も消す。
$ruleNames = @(@($fileRules | ForEach-Object { $_.Name }) + 'Emacs: Excel のシート切り替え' | Select-Object -Unique)

# helper.json の、KillLine の動作（Id 100）と、このファイルの規則を足す・消す。ほかの内容は、そのまま残す。
function Set-HelperEntries([bool]$KillLinePresent, [object[]]$Rules) {
    $cfg = $null
    if (Test-Path $helperPath) { $cfg = Get-Content $helperPath -Raw -Encoding UTF8 | ConvertFrom-Json }
    if (-not $cfg) {
        if (-not $KillLinePresent -and $Rules.Count -eq 0) { return }
        $cfg = [pscustomobject]@{ Version = 1; AppLayerRules = @(); Actions = @(); Triggers = @() }
    }
    foreach ($p in 'AppLayerRules', 'Actions') {
        if (-not $cfg.PSObject.Properties[$p]) { $cfg | Add-Member -NotePropertyName $p -NotePropertyValue @() }
    }
    $actions = @($cfg.Actions | Where-Object { $_ -and $_.Id -ne $KillLineId })
    if ($KillLinePresent) { $actions += [pscustomobject]@{ Id = $KillLineId; Name = 'Emacs C-k'; Action = [pscustomobject]@{ Type = 'KillLine' } } }
    $cfg.Actions = $actions
    # 規則は先頭に置く（先に一致したものが使われるので、ほかの規則より優先する）。
    $cfg.AppLayerRules = @($Rules) + @($cfg.AppLayerRules | Where-Object { $_ -and $ruleNames -notcontains $_.Name })
    New-Item -ItemType Directory -Force (Split-Path $helperPath) | Out-Null
    $json = $cfg | ConvertTo-Json -Depth 20
    [IO.File]::WriteAllText($helperPath, $json, (New-Object Text.UTF8Encoding($false)))
}

if ($Remove) {
    # レイヤーマップは、キーボード 1 台に 1 つなので、全体を消す。キー置換とマクロは、このファイルの番号だけを消す。
    Run @('--off', $Id) -IgnoreError
    Run @('--clear', $Id) -IgnoreError
    foreach ($o in $kb.KeyOverrides) { Run @('--set-key-override', $Id, "$($o.Id)") }
    foreach ($m in $kb.Macros) { Run @('--set-macro', $Id, "$($m.Id)") }
    Set-HelperEntries $false @()
    return
}

# レイヤー: Layers[].Keys の "SC_xxxx" (スキャンコード) を、KeymapTool の --apply-simple 用の 2 つのファイルにする。
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("emacs-apply-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
    $layers = @($kb.Layers | Sort-Object Index)
    $codes = @($layers[0].Keys.PSObject.Properties.Name | Sort-Object)
    # レイヤー 0 のキーが 1 個も無いと、レイヤーマップを作れない（1〜256 個）。レイヤーマップを消してから失敗して、
    # 何も効かない状態で残らないよう、何も書き換える前に止める。
    if ($codes.Count -lt 1 -or $codes.Count -gt 256) { throw "レイヤー 0 のキーが $($codes.Count) 個です（1〜256 個が必要です）。何も書き換えていません: $File" }
    $pos = for ($i = 0; $i -lt $codes.Count; $i++) { '{{"row":0,"col":{0},"scanCode":{1}}}' -f $i, [Convert]::ToInt32($codes[$i].Substring(3), 16) }
    Set-Content (Join-Path $tmp 'profile.json') ('{ "positions": [ ' + ($pos -join ',') + ' ] }') -Encoding ASCII
    $rows = foreach ($layer in $layers) {
        $keys = foreach ($c in $codes) { $p = $layer.Keys.PSObject.Properties[$c]; if ($p) { '"' + $p.Value + '"' } else { '"KC_TRNS"' } }
        '[ ' + ($keys -join ', ') + ' ]'
    }
    Set-Content (Join-Path $tmp 'keymap.json') ('{ "layout": [ ' + ($rows -join ', ') + ' ] }') -Encoding ASCII

    # マクロとキー置換を先に書く。レイヤーマップが、MACRO(n) と OSL(n)・TG(n) を使うため。
    foreach ($m in $kb.Macros) { Run (@('--set-macro', $Id, "$($m.Id)") + @($m.Keys)) }
    foreach ($o in $kb.KeyOverrides) {
        $replacement = $o.Replacement
        # -KillLine のとき、C-k だけ、マクロ(126)ではなくヘルパー動作にする。
        if ($KillLine -and $o.Mods -eq 'MOD_LCTL' -and $o.Key -eq 'KC_K') { $replacement = "HLP($KillLineId)" }
        $overrideArgs = @('--set-key-override', $Id, "$($o.Id)", $o.Mods, $o.Key, $replacement)
        # Layers があるキー置換は、そのレイヤーが立っている間だけ効く（layers=3,5 の形）。
        if ($o.PSObject.Properties['Layers'] -and @($o.Layers).Count -gt 0) { $overrideArgs += ('layers=' + (@($o.Layers) -join ',')) }
        Run $overrideArgs
    }
    Set-HelperEntries ([bool]$KillLine) $fileRules
    Run @('--off', $Id) -IgnoreError
    Run @('--clear', $Id) -IgnoreError
    Run @('--apply-simple', $Id, (Join-Path $tmp 'keymap.json'), (Join-Path $tmp 'profile.json'))
    Run @('--on', $Id)
} finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host "書き込みました: レイヤー $($kb.Layers.Count) 枚、マクロ $($kb.Macros.Count) 件、キー置換 $($kb.KeyOverrides.Count) 件、アプリ別レイヤーの規則 $($fileRules.Count) 件。機能(マクロ・キー置換)が OFF でないことも確かめてください。"
