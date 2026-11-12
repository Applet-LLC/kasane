<#
.SYNOPSIS
  docs/manual/src/<lang>/*.md から docs/manual/<lang>/*.html を作る。
.DESCRIPTION
  原稿(Markdown)を ConvertFrom-Markdown で HTML にし、共通の目次(左)と前後の章へのリンクを付ける。
  あわせて、内部リンクと画像の存在を確認する(欠けがあれば、一覧を出して終了コード1)。
  英語版(en)は、章ごとに少しずつ訳している。原稿が無い章は、作らない。目次や本文のリンクは、その章の日本語版(../ja/)を指す。
  使い方: pwsh docs/manual/build-manual.ps1 [-Lang ja|en|all]
#>
param([string]$Lang = 'ja')

$ErrorActionPreference = 'Stop'

if ($Lang -eq 'all') {
    $failed = $false
    foreach ($l in 'ja', 'en') {
        & $PSCommandPath -Lang $l
        if ($LASTEXITCODE -ne 0) { $failed = $true }
    }
    exit ($(if ($failed) { 1 } else { 0 }))
}

$root = $PSScriptRoot
$src = Join-Path $root "src/$Lang"
$out = Join-Path $root $Lang
$isEnglish = ($Lang -eq 'en')

# 章の一覧(ファイル名は拡張子なし)。順番がそのまま目次の順になる。Title が日本語、En が英語。
$chapters = @(
    @{ Name = 'index';                Title = 'はじめに';                                   En = 'Introduction' }
    @{ Name = '01-install';           Title = '1. インストール';                            En = '1. Installation' }
    @{ Name = '02-uninstall';         Title = '2. アンインストール';                        En = '2. Uninstallation' }
    @{ Name = '03-license';           Title = '3. ライセンスの認証';                        En = '3. License Activation' }
    @{ Name = '04-tray';              Title = '4. トレイ';                                  En = '4. Tray' }
    @{ Name = '05-editor-basics';     Title = '5. エディタの基本画面';                      En = '5. Editor Basics' }
    @{ Name = '06-calibration';       Title = '6. キャリブレーション';                      En = '6. Calibration' }
    @{ Name = '07-emergency';         Title = '7. 緊急解除';                                En = '7. Emergency Release' }
    @{ Name = '08-key-assign';        Title = '8. キーの割り当て';                          En = '8. Assigning Keys' }
    @{ Name = '09-advanced';          Title = '9. マクロ・コンボ・タップダンス・キー置換';  En = '9. Macros, Combos, Tap Dances and Key Overrides' }
    @{ Name = '10-ime-nicola';        Title = '10. IME連動とNICOLA';                        En = '10. IME Linking and NICOLA' }
    @{ Name = '11-helper-apprules';   Title = '11. ヘルパー動作とアプリ別レイヤー';        En = '11. Helper Actions and Per-App Layers' }
    @{ Name = '12-recipes';           Title = '12. 設定の例';                               En = '12. Setup Examples' }
    @{ Name = '13-settings';          Title = '13. 設定ダイアログ';                         En = '13. Settings Dialog' }
    @{ Name = '14-import-export';     Title = '14. インポート／エクスポート';               En = '14. Import and Export' }
    @{ Name = '15-backup-migrate';    Title = '15. バックアップと移行';                     En = '15. Backup and Migration' }
    @{ Name = '16-reference';         Title = '16. トラブル対応、既知の制限など';           En = '16. Troubleshooting and Known Limitations' }
    @{ Name = '17-keymaptool';        Title = '17. KeymapTool とサンプル';                  En = '17. KeymapTool and Samples' }
    @{ Name = '18-readme';            Title = '18. README、来歴';                           En = '18. README and Background' }
    @{ Name = '19-terms';             Title = '19. 使用許諾';                               En = '19. Terms of Use' }
    @{ Name = '20-third-party';       Title = '20. サードパーティノーティス';              En = '20. Third-Party Notices' }
)
foreach ($c in $chapters) {
    $c.Display = if ($isEnglish) { $c.En } else { $c.Title }
    # 英語版は、原稿のある章だけを作る。日本語版は全章(無ければ、あとで問題として出す)。
    $c.Built = (-not $isEnglish) -or (Test-Path (Join-Path $src "$($c.Name).md"))
}

$htmlLang = $Lang
$productName = if ($isEnglish) { 'kasane Help' } else { 'かさね ヘルプ' }
# 各ページの右上に出す、バージョンと著作権の表示。
$pageHeadBase = if ($isEnglish) {
    'kasane 1.01 2026-11-10&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;Copyright 2026 Applet LLC <a href="https://appletllc.com/">https://appletllc.com/</a> <a href="mailto:appletllc@gmail.com">appletllc@gmail.com</a>'
} else {
    'かさね1.01 2026-11-10&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;Copyright 2026 Applet LLC <a href="https://appletllc.com/">https://appletllc.com/</a> <a href="mailto:appletllc@gmail.com">appletllc@gmail.com</a>'
}

function Escape-Html([string]$s) { [System.Net.WebUtility]::HtmlEncode($s) }

# 章へのリンクの href と表示名。英語版で訳していない章は、日本語版を指し、名前に「(日本語)」を添える。
function Get-ChapterHref($c) { if ($c.Built) { "$($c.Name).html" } else { "../ja/$($c.Name).html" } }
function Get-ChapterLabel($c) { if ($c.Built) { Escape-Html $c.Display } else { (Escape-Html $c.Display) + ' (日本語)' } }

New-Item -ItemType Directory -Force -Path $out | Out-Null
$problems = [System.Collections.Generic.List[string]]::new()
$bodies = @{}
$missingImages = [System.Collections.Generic.List[string]]::new()

for ($i = 0; $i -lt $chapters.Count; $i++) {
    $ch = $chapters[$i]
    $mdPath = Join-Path $src "$($ch.Name).md"
    if (-not $ch.Built) { continue }
    if (-not (Test-Path $mdPath)) { $problems.Add("原稿がありません: $mdPath"); continue }

    $body = (ConvertFrom-Markdown -Path $mdPath).Html

    # 英語版: 訳していない章へのリンクは、日本語版の章へ付け替える。
    if ($isEnglish) {
        foreach ($c in $chapters) {
            if ($c.Built) { continue }
            $body = $body.Replace("href=`"$($c.Name).html", "href=`"../ja/$($c.Name).html")
        }
    }

    # 目次
    $toc = New-Object System.Text.StringBuilder
    [void]$toc.AppendLine('<ol>')
    foreach ($c in $chapters) {
        $cls = if ($c.Name -eq $ch.Name) { ' class="current"' } else { '' }
        [void]$toc.AppendLine("<li$cls><a href=`"$(Get-ChapterHref $c)`">$(Get-ChapterLabel $c)</a></li>")
    }
    [void]$toc.AppendLine('</ol>')

    # 前後の章
    $prev = if ($i -gt 0) { "<a href=`"$(Get-ChapterHref $chapters[$i-1])`">← $(Get-ChapterLabel $chapters[$i-1])</a>" } else { '<span></span>' }
    $next = if ($i -lt $chapters.Count - 1) { "<a href=`"$(Get-ChapterHref $chapters[$i+1])`">$(Get-ChapterLabel $chapters[$i+1]) →</a>" } else { '<span></span>' }

    # 言語の切り替え(もう一方の言語に、同じ章があるときだけ)
    $pageHead = $pageHeadBase
    if ($isEnglish) {
        $pageHead += " <a href=`"../ja/$($ch.Name).html`">日本語</a>"
    } elseif (Test-Path (Join-Path $root "src/en/$($ch.Name).md")) {
        $pageHead += " <a href=`"../en/$($ch.Name).html`">English</a>"
    }

    $html = @"
<!doctype html>
<html lang="$htmlLang">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>$(Escape-Html $ch.Display) - $productName</title>
<link rel="stylesheet" href="../assets/style.css">
</head>
<body>
<div class="layout">
<nav class="toc">
<a class="brand" href="index.html">$productName</a>
$($toc.ToString())
</nav>
<main>
<div class="pagehead">$pageHead</div>
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

# リンク先のファイルを探す。マニュアルは <インストール先>\manual\ja\ に入るので、../../ で始まるリンクは
# インストール先の直下を指す。リポジトリでの置き場所は、Prepare-Staging.ps1 がインストール先へ入れる元のファイルと同じ。
$repoRoot = Split-Path (Split-Path $root -Parent) -Parent
function Resolve-LinkTarget([string]$target) {
    if ($target -notmatch '^\.\./\.\./(.+)$') { return (Join-Path $out $target) }
    $rel = $Matches[1]
    if ($rel -match '^(License\..+)$')                       { return (Join-Path $repoRoot "setup/License/$($Matches[1])") }
    if ($rel -match '^(README\..+)$')                        { return (Join-Path $repoRoot "setup/dist-readme/$($Matches[1])") }
    if ($rel -match '^samples/(.+)$')                         { return (Join-Path $repoRoot "driver/KeymapTool/samples/$($Matches[1])") }
    return (Join-Path $repoRoot $rel)   # THIRD-PARTY-NOTICES.txt、third-party/、docs/
}

# 内部リンクと画像の確認(全ページを書き出した後に行う)
foreach ($name in $bodies.Keys) {
    foreach ($m in [regex]::Matches($bodies[$name], '(?:href|src)="([^"#:]+)(?:#[^"]*)?"')) {
        $target = $m.Groups[1].Value
        if (-not (Test-Path (Resolve-LinkTarget $target))) {
            if ($target -match '\.(png|jpg|gif)$') { $missingImages.Add("${name}: $target") }
            else { $problems.Add("${name}: リンク先がありません: $target") }
        }
    }
}

# 章の一覧にない HTML(章の番号や名前を変えたときの、古いページ)は消す。インストーラーに古いページが入らないようにする。
$known = $chapters | Where-Object { $_.Built } | ForEach-Object { "$($_.Name).html" }
Get-ChildItem -LiteralPath $out -Filter *.html | Where-Object { $known -notcontains $_.Name } | ForEach-Object {
    Remove-Item -LiteralPath $_.FullName -Force
    Write-Host "古いページを削除: $($_.Name)"
}

if ($problems.Count -gt 0) {
    Write-Host "確認が必要な点が $($problems.Count) 件あります:" -ForegroundColor Yellow
    $problems | ForEach-Object { Write-Host "  $_" }
    exit 1
}
if ($missingImages.Count -gt 0) { Write-Host "未撮影の画像が $($missingImages.Count) 枚あります(警告)。" -ForegroundColor Yellow }
$builtCount = @($chapters | Where-Object { $_.Built }).Count
$untranslated = $chapters.Count - $builtCount
if ($untranslated -gt 0) { Write-Host "未訳の章が $untranslated 章あります(日本語版へのリンクにしました)。" -ForegroundColor Yellow }
Write-Host "OK: $builtCount ページを $out に作りました。"
