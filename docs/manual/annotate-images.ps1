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
    $font = New-Object System.Drawing.Font('Yu Gothic UI', 15, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $fmt = New-Object System.Drawing.StringFormat; $fmt.Alignment = 'Center'; $fmt.LineAlignment = 'Center'
    foreach ($l in $labels) {
        $r = 14
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
    @{ N = 1; X = 340;  Y = 22 },
    @{ N = 2; X = 1868; Y = 145 },
    @{ N = 3; X = 410;  Y = 195 },
    @{ N = 4; X = 1450; Y = 290 },
    @{ N = 5; X = 1482; Y = 822 },
    @{ N = 6; X = 770;  Y = 873 }
)
