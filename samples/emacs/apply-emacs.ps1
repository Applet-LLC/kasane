<#
.SYNOPSIS
  emacs.kasane.json のマクロとキー置換を、KeymapTool でキーボードに書き込む（管理者権限が必要）。
.EXAMPLE
  .\apply-emacs.ps1 -Id CDEC0000
  .\apply-emacs.ps1 -Id CDEC0000 -Remove
#>
param(
    [Parameter(Mandatory)][string]$Id,
    [switch]$Remove,
    [string]$File = (Join-Path $PSScriptRoot 'emacs.kasane.json'),
    [string]$KeymapTool = 'C:\Program Files\applet\kasane\KeymapTool.exe'
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path $KeymapTool)) { throw "KeymapTool.exe が見つかりません: $KeymapTool" }
$Id = $Id -replace '^0[xX]', ''

# 指定した ID のキーボードを、ドライバが認識しているかを先に確かめる(認識していないと、書き込みは 1168 で失敗する)。
$listing = (& $KeymapTool --list | Out-String)
if ($listing -notmatch "UniqId=$Id\b") {
    throw "ID $Id のキーボードを、ドライバが認識していません。KeymapTool --list の表示にある UniqId を指定してください。`n$listing"
}
$kb = (Get-Content $File -Raw -Encoding UTF8 | ConvertFrom-Json).Keyboards[0]

function Run([string[]]$ToolArgs) {
    & $KeymapTool @ToolArgs
    if ($LASTEXITCODE -ne 0) { throw "KeymapTool が失敗しました(終了コード $LASTEXITCODE): $($ToolArgs -join ' ')" }
}

if ($Remove) {
    # キー置換は、表の32件すべてを空にする。ほかの用途で使っていた分も消える。
    0..31 | ForEach-Object { Run @('--set-key-override', $Id, "$_") }
    foreach ($m in $kb.Macros) { Run @('--set-macro', $Id, "$($m.Id)") }
    return
}
foreach ($m in $kb.Macros) { Run (@('--set-macro', $Id, "$($m.Id)") + @($m.Keys)) }
foreach ($o in $kb.KeyOverrides) { Run @('--set-key-override', $Id, "$($o.Id)", $o.Mods, $o.Key, $o.Replacement) }
Write-Host "書き込みました: マクロ $($kb.Macros.Count) 件、キー置換 $($kb.KeyOverrides.Count) 件。機能(マクロ・キー置換)が OFF でないことも確かめてください。"
