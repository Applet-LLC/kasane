<#
.SYNOPSIS
  使用許諾契約書（License\License.*.rtf、License-Pro\License.*.rtf）から、同じ内容のテキストファイル（UTF-8、BOM 付き）を作る。
.DESCRIPTION
  インストーラーの画面には RTF を使うが、RTF を読める環境は少ない。そこで、読みやすいテキストを同じ場所に置き、インストール先の直下と配布の zip にも入れる。
  RTF を直したら、このスクリプトを実行して、テキストも作り直す（RTF が元。テキストは手で直さない）。
  -Check を付けると、書き込まずに、テキストが RTF と合っているかを確かめる（合っていなければ、終了コード 1）。Prepare-Staging.ps1 が使う。
  使い方: pwsh setup/Convert-LicenseToText.ps1 [-Check]
.NOTES
  ブラウザーが BOM のないテキストを日本語の文字コードで開き直すことがあるため、BOM を付ける。改行は CRLF。段落の間に空行を入れる。
#>
param([switch]$Check)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms

$utf8Bom = New-Object System.Text.UTF8Encoding($true)
$stale = [System.Collections.Generic.List[string]]::new()

foreach ($dir in 'License', 'License-Pro') {
    foreach ($lang in 'ja-JP', 'en-US') {
        $rtfPath = Join-Path $PSScriptRoot "$dir\License.$lang.rtf"
        $txtPath = Join-Path $PSScriptRoot "$dir\License.$lang.txt"
        if (-not (Test-Path -LiteralPath $rtfPath)) { throw "RTF not found: $rtfPath" }

        # RichTextBox は、RTF の \'xx（コードページ）と \uN をどちらも正しく読む。段落は改行 1 つで区切られて返る。
        $box = New-Object System.Windows.Forms.RichTextBox
        $box.Rtf = [System.IO.File]::ReadAllText($rtfPath, [System.Text.Encoding]::ASCII)
        # RTF の \line（段落内の改行）は、垂直タブ（U+000B）や U+2028 で返る。そのままだと、文字化けのような 1 文字になるため、改行 1 つにする。
        $lineBreak = [string][char]0xE000   # 一時の目印（私用領域の文字）
        $plain = $box.Text -replace "[\u000B\u2028]", $lineBreak
        $paragraphs = $plain -split "`r?`n" | ForEach-Object { $_.TrimEnd() } | Where-Object { $_ -ne '' } | ForEach-Object { $_.Replace($lineBreak, "`r`n") }
        $text = ($paragraphs -join "`r`n`r`n") + "`r`n"
        $bytes = $utf8Bom.GetPreamble() + $utf8Bom.GetBytes($text)

        if ($Check) {
            $current = if (Test-Path -LiteralPath $txtPath) { [System.IO.File]::ReadAllBytes($txtPath) } else { $null }
            if ($null -eq $current -or -not [System.Linq.Enumerable]::SequenceEqual([byte[]]$current, [byte[]]$bytes)) { $stale.Add($txtPath) }
        } else {
            [System.IO.File]::WriteAllBytes($txtPath, $bytes)
            Write-Host "wrote: $txtPath ($($paragraphs.Count) paragraphs)"
        }
    }
}

if ($Check -and $stale.Count -gt 0) {
    Write-Host "License text is out of date. Run: pwsh setup/Convert-LicenseToText.ps1" -ForegroundColor Red
    $stale | ForEach-Object { Write-Host "  $_" }
    exit 1
}
