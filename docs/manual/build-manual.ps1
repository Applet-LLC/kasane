<#
.SYNOPSIS
  docs/manual/src/<lang>/*.md から docs/manual/<lang>/*.html を作る。
.DESCRIPTION
  原稿(Markdown)を ConvertFrom-Markdown で HTML にし、共通の目次(左)と前後の章へのリンクを付ける。
  あわせて、内部リンクと画像の存在を確認する(欠けがあれば、一覧を出して終了コード1)。
  使い方: pwsh docs/manual/build-manual.ps1 [-Lang ja]
#>
param([string]$Lang = 'ja')

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$src = Join-Path $root "src/$Lang"
$out = Join-Path $root $Lang

# 章の一覧(ファイル名は拡張子なし)。順番がそのまま目次の順になる。
$chapters = @(
    @{ Name = 'index';                Title = 'はじめに' }
    @{ Name = '01-install';           Title = '1. インストール' }
    @{ Name = '02-uninstall';         Title = '2. アンインストール' }
    @{ Name = '03-license';           Title = '3. ライセンスの認証' }
    @{ Name = '04-tray';              Title = '4. トレイ' }
    @{ Name = '05-editor-basics';     Title = '5. エディタの基本画面' }
    @{ Name = '06-calibration';       Title = '6. キャリブレーション' }
    @{ Name = '07-emergency';         Title = '7. 緊急解除' }
    @{ Name = '08-key-assign';        Title = '8. キーの割り当て' }
    @{ Name = '09-advanced';          Title = '9. マクロ・コンボ・タップダンス・キー置換' }
    @{ Name = '10-ime-nicola';        Title = '10. IME連動とNICOLA' }
    @{ Name = '11-helper-apprules';   Title = '11. ヘルパー動作とアプリ別レイヤー' }
    @{ Name = '12-settings';          Title = '12. 設定ダイアログ' }
    @{ Name = '13-import-export';     Title = '13. インポート／エクスポート' }
    @{ Name = '14-backup-migrate';    Title = '14. バックアップと移行' }
    @{ Name = '15-reference';         Title = '15. 付録' }
)

$htmlLang = if ($Lang -eq 'ja') { 'ja' } else { $Lang }
$productName = 'kasane マニュアル'

function Escape-Html([string]$s) { [System.Net.WebUtility]::HtmlEncode($s) }

New-Item -ItemType Directory -Force -Path $out | Out-Null
$problems = [System.Collections.Generic.List[string]]::new()
$bodies = @{}
$missingImages = [System.Collections.Generic.List[string]]::new()

for ($i = 0; $i -lt $chapters.Count; $i++) {
    $ch = $chapters[$i]
    $mdPath = Join-Path $src "$($ch.Name).md"
    if (-not (Test-Path $mdPath)) { $problems.Add("原稿がありません: $mdPath"); continue }

    $body = (ConvertFrom-Markdown -Path $mdPath).Html

    # 目次
    $toc = New-Object System.Text.StringBuilder
    [void]$toc.AppendLine('<ol>')
    foreach ($c in $chapters) {
        $cls = if ($c.Name -eq $ch.Name) { ' class="current"' } else { '' }
        [void]$toc.AppendLine("<li$cls><a href=`"$($c.Name).html`">$(Escape-Html $c.Title)</a></li>")
    }
    [void]$toc.AppendLine('</ol>')

    # 前後の章
    $prev = if ($i -gt 0) { "<a href=`"$($chapters[$i-1].Name).html`">← $(Escape-Html $chapters[$i-1].Title)</a>" } else { '<span></span>' }
    $next = if ($i -lt $chapters.Count - 1) { "<a href=`"$($chapters[$i+1].Name).html`">$(Escape-Html $chapters[$i+1].Title) →</a>" } else { '<span></span>' }

    $html = @"
<!doctype html>
<html lang="$htmlLang">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>$(Escape-Html $ch.Title) - $productName</title>
<link rel="stylesheet" href="../assets/style.css">
</head>
<body>
<div class="layout">
<nav class="toc">
<a class="brand" href="index.html">$productName</a>
$($toc.ToString())
</nav>
<main>
$body
<div class="pager">$prev$next</div>
</main>
</div>
</body>
</html>
"@
    Set-Content -Path (Join-Path $out "$($ch.Name).html") -Value $html -Encoding utf8NoBOM

    $bodies[$ch.Name] = $body
}

# 内部リンクと画像の確認(全ページを書き出した後に行う)
foreach ($name in $bodies.Keys) {
    foreach ($m in [regex]::Matches($bodies[$name], '(?:href|src)="([^"#:]+)(?:#[^"]*)?"')) {
        $target = $m.Groups[1].Value
        if (-not (Test-Path (Join-Path $out $target))) {
            if ($target -match '\.(png|jpg|gif)$') { $missingImages.Add("${name}: $target") }
            else { $problems.Add("${name}: リンク先がありません: $target") }
        }
    }
}

if ($problems.Count -gt 0) {
    Write-Host "確認が必要な点が $($problems.Count) 件あります:" -ForegroundColor Yellow
    $problems | ForEach-Object { Write-Host "  $_" }
    exit 1
}
if ($missingImages.Count -gt 0) { Write-Host "未撮影の画像が $($missingImages.Count) 枚あります(警告)。" -ForegroundColor Yellow }
Write-Host "OK: $($chapters.Count) ページを $out に作りました。"
