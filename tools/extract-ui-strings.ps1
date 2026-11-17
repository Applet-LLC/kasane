<#
  コードの Loc.T(日本語, 英語) から、画面に実際に出る英語の文言を一覧にする（README・ヘルプの英語版を点検するため）。
  使い方: pwsh tools/extract-ui-strings.ps1 [-Out <出力ファイル>] [-Filter <英語の文言に含まれる語（正規表現）>]
  出力: ファイル名<TAB>日本語<TAB>英語（TSV）。WiX の英語文言（setup/Product.en-us.wxl）とライセンス管理の Strings.resx は、別途そのファイルを見ること。
  限界: 文字列補間（{...}）の中身は展開しない。XAML に直接書いた文字は拾わない。
#>
param(
    [string]$Out = (Join-Path $env:TEMP 'kasane-ui-strings.tsv'),
    [string]$Filter = ''
)
$root = Split-Path $PSScriptRoot -Parent
$rx = [regex]'(?s)\bT\(\s*\$?@?"((?:[^"\\]|\\.)*)"\s*,\s*\$?@?"((?:[^"\\]|\\.)*)"'
$dirs = 'kasane', 'kasane-tray', 'kasane-license', 'kasane-launcher', 'kasane-license-svc', 'kasane-pro-activator' | ForEach-Object { Join-Path $root $_ } | Where-Object { Test-Path $_ }
$rows = foreach ($f in Get-ChildItem -Recurse -Include *.cs -Path $dirs | Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' }) {
    $t = Get-Content -LiteralPath $f.FullName -Raw -Encoding UTF8
    foreach ($m in $rx.Matches($t)) { '{0}{3}{1}{3}{2}' -f $f.Name, $m.Groups[1].Value, $m.Groups[2].Value, "`t" }
}
if ($Filter) { $rows = $rows | Where-Object { ($_ -split "`t")[2] -match $Filter } }
$rows | Set-Content -LiteralPath $Out -Encoding UTF8
"$($rows.Count) 件を $Out に書きました"
