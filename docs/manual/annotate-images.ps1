<#
.SYNOPSIS
  撮影した画像に、番号の丸を描き込んだ版(*-labeled.png)を作る。
.DESCRIPTION
  05 章の「画面の各部」の図用。元の画像(img/<lang>/editor-main.png)は変えない。
  座標は、Capture-Manual.ps1 で撮った 1902x891 の画像(表示倍率 150%)に合わせてある。大きさが違う画像では、座標を見直すこと。
  使い方: pwsh docs/manual/annotate-images.ps1 [-Lang ja]
#>
param([string]$Lang = 'ja')
Add-Type -AssemblyName System.Drawing
$dir = Join-Path $PSScriptRoot "img/$Lang"

function Add-Labels([string]$name, [object[]]$labels) {
    $src = Join-Path $dir "$name.png"
    if (-not (Test-Path $src)) { Write-Host "  なし: $name"; return }
    $img = [System.Drawing.Image]::FromFile($src)
    $bmp = New-Object System.Drawing.Bitmap($img)
    $img.Dispose()
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
    $font = New-Object System.Drawing.Font('Yu Gothic UI', 23, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $fmt = New-Object System.Drawing.StringFormat; $fmt.Alignment = 'Center'; $fmt.LineAlignment = 'Center'
    foreach ($l in $labels) {
        $r = 19
        $g.FillEllipse([System.Drawing.Brushes]::Crimson, $l.X - $r, $l.Y - $r, 2 * $r, 2 * $r)
        $g.DrawEllipse((New-Object System.Drawing.Pen([System.Drawing.Color]::White, 2)), $l.X - $r, $l.Y - $r, 2 * $r, 2 * $r)
        $g.DrawString([string]$l.N, $font, [System.Drawing.Brushes]::White, (New-Object System.Drawing.RectangleF(($l.X - $r), ($l.Y - $r), (2 * $r), (2 * $r))), $fmt)
    }
    $g.Dispose()
    $bmp.Save((Join-Path $dir "$name-labeled.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "  作成: $name-labeled.png"
}

# editor-main: 1 タイトルバー / 2 キーボード・レイアウト行 / 3 レイヤー行 / 4 キー図 / 5 下部のボタン / 6 ステータス欄
Add-Labels 'editor-main' @(
    @{ N = 1; X = 345;  Y = 23 },
    @{ N = 2; X = 1868; Y = 145 },
    @{ N = 3; X = 410;  Y = 195 },
    @{ N = 4; X = 1450; Y = 290 },
    @{ N = 5; X = 1482; Y = 822 },
    @{ N = 6; X = 780;  Y = 872 }
)

# ---- 画像の合成と切り出し(VM で撮った画像を、原稿の図にする) ----
function New-Canvas([int]$w, [int]$h) {
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp); $g.Clear([System.Drawing.Color]::White)
    return @{ Bmp = $bmp; G = $g }
}
function Save-Canvas($c, [string]$name) { $c.G.Dispose(); $c.Bmp.Save((Join-Path $dir "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png); $c.Bmp.Dispose(); Write-Host "  作成: $name.png" }

# tray-icons: トレイの帯(動作中・一時停止中)を縦に並べ、状態の名前を付ける
$strips = @(@{ File = 'tray-strip-active'; Label = '動作中(1台)' }, @{ File = 'tray-strip-paused'; Label = '一時停止中' })
if ($strips | Where-Object { Test-Path (Join-Path $dir "$($_.File).png") }) {
    $font = New-Object System.Drawing.Font('Yu Gothic UI', 15, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    $c = New-Canvas 330 ((24 + 48) * $strips.Count)
    $y = 0
    foreach ($s in $strips) {
        $p = Join-Path $dir "$($s.File).png"; if (-not (Test-Path $p)) { continue }
        $img = [System.Drawing.Image]::FromFile($p)
        $c.G.DrawString($s.Label, $font, [System.Drawing.Brushes]::DimGray, 4, $y + 3)
        $c.G.DrawImage($img, 0, $y + 24, $img.Width, $img.Height); $img.Dispose(); $y += 72
    }
    Save-Canvas $c 'tray-icons'
}

# uninstall-off: OFF にしたあとのエディタの、タイトルバーと下部(ボタンと状態欄)だけを切り出して重ねる
$raw = Join-Path $dir 'uninstall-off-raw.png'
if (Test-Path $raw) {
    $img = [System.Drawing.Image]::FromFile($raw)
    $topH = 34; $botH = 64; $c = New-Canvas $img.Width ($topH + 8 + $botH)
    $c.G.DrawImage($img, (New-Object System.Drawing.Rectangle(0, 0, $img.Width, $topH)), 0, 0, $img.Width, $topH, [System.Drawing.GraphicsUnit]::Pixel)
    $c.G.DrawLine((New-Object System.Drawing.Pen([System.Drawing.Color]::LightGray, 1)), 0, $topH + 4, $img.Width, $topH + 4)
    $c.G.DrawImage($img, (New-Object System.Drawing.Rectangle(0, ($topH + 8), $img.Width, $botH)), 0, $img.Height - $botH, $img.Width, $botH, [System.Drawing.GraphicsUnit]::Pixel)
    $img.Dispose(); Save-Canvas $c 'uninstall-off'
}

# license-diagnostics: 認証済みの画面から、接続の診断の部分だけを切り出す
$act = Join-Path $dir 'license-activated.png'
if (Test-Path $act) {
    $img = [System.Drawing.Image]::FromFile($act)
    $c = New-Canvas 518 66
    $c.G.DrawImage($img, (New-Object System.Drawing.Rectangle(0, 0, 518, 66)), 14, 238, 518, 66, [System.Drawing.GraphicsUnit]::Pixel)
    $img.Dispose(); Save-Canvas $c 'license-diagnostics'
}
