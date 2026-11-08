<#
.SYNOPSIS
  GUI に出している文言の一覧を作る（読むだけ。2026-11-08、UI の静的テキストの見直し）。

.DESCRIPTION
  対象:
    - kasane\Views と kasane-tray の C#: Loc.T("日本語", "English") の呼び出し。文字列の連結（"a" + "b"）は 1 つの文にまとめる。
    - XAML の Text / Content / ToolTip / Title / Header の直書き。ただし、コードで同じ要素の同じ属性に代入しているもの
      （MainWindow.Localize.cs などで Loc.T に置き換えるもの）は、コード側に出るので数えない。
    - kasane-license の Strings.ja.resx（License Manager の文言）。
  種類（Kind）は、呼び出しの前の書き方から推定する:
    title / header / button / checkbox / label / tooltip / help（説明の段落）/ menu / dynamic（MessageBox・ステータス・検証・ログ・
    実行時の値を埋め込む文）/ other。
  長さは、等幅の字体での見た目の幅（全角 = 2。RemapTextFormatter.DisplayWidth と同じ数え方）。

.PARAMETER OutCsv
  一覧を書き出す CSV（UTF-8 BOM 付き）。省くと、画面ごとの件数だけを出す。

.EXAMPLE
  pwsh tools\List-UiTexts.ps1 -OutCsv .plans\handoff\ui-texts.csv
#>
param([string]$OutCsv)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

function Get-DisplayWidth([string]$s) {
    $w = 0
    foreach ($ch in $s.ToCharArray()) {
        $c = [int]$ch
        $wide = $c -ge 0x1100 -and ($c -le 0x115F -or ($c -ge 0x2010 -and $c -le 0x2BFF) -or ($c -ge 0x2E80 -and $c -le 0xA4CF) -or
                ($c -ge 0xAC00 -and $c -le 0xD7A3) -or ($c -ge 0xF900 -and $c -le 0xFAFF) -or ($c -ge 0xFE30 -and $c -le 0xFE4F) -or
                ($c -ge 0xFF00 -and $c -le 0xFF60) -or ($c -ge 0xFFE0 -and $c -le 0xFFE6))
        $w += $(if ($wide) { 2 } else { 1 })
    }
    return $w
}

# C# の文字列リテラル（"..." と $"..."）を、位置 $i から読む。戻り値: @{ Text; End; Interpolated; Holes }
function Read-Literal([string]$t, [int]$i) {
    $interp = $false
    if ($t[$i] -eq '$') { $interp = $true; $i++ }
    if ($t[$i] -eq '@') { $i++ }               # 逐語的（ほぼ使っていない。エスケープは同じ扱い）
    if ($t[$i] -ne '"') { return $null }
    $i++
    $sb = [System.Text.StringBuilder]::new()
    $holes = 0
    while ($i -lt $t.Length) {
        $c = $t[$i]
        if ($c -eq '\') {
            $n = $t[$i + 1]
            switch ($n) { 'n' { [void]$sb.Append("`n") } 't' { [void]$sb.Append("`t") } 'u' { [void]$sb.Append([char][Convert]::ToInt32($t.Substring($i + 2, 4), 16)); $i += 4 } default { [void]$sb.Append($n) } }
            $i += 2; continue
        }
        if ($interp -and $c -eq '{') {
            if ($t[$i + 1] -eq '{') { [void]$sb.Append('{'); $i += 2; continue }
            # 埋め込み {式}: 入れ子の括弧と文字列を飛ばす
            $depth = 1; $j = $i + 1
            while ($j -lt $t.Length -and $depth -gt 0) {
                if ($t[$j] -eq '"') { $j++; while ($j -lt $t.Length -and $t[$j] -ne '"') { if ($t[$j] -eq '\') { $j++ }; $j++ } }
                elseif ($t[$j] -eq '{') { $depth++ } elseif ($t[$j] -eq '}') { $depth-- }
                $j++
            }
            [void]$sb.Append('{…}'); $holes++; $i = $j; continue
        }
        if ($interp -and $c -eq '}' -and $t[$i + 1] -eq '}') { [void]$sb.Append('}'); $i += 2; continue }
        if ($c -eq '"') { return @{ Text = $sb.ToString(); End = $i + 1; Interpolated = $interp; Holes = $holes } }
        [void]$sb.Append($c); $i++
    }
    return $null
}

# Loc.T( の直後 $i から、最初の引数（日本語）を読む。連結した文字列をまとめ、文字列以外の式があれば NonLiteral。
function Read-FirstArg([string]$t, [int]$i) {
    $parts = [System.Collections.Generic.List[string]]::new()
    $holes = 0; $nonLiteral = $false; $depth = 0
    while ($i -lt $t.Length) {
        $c = $t[$i]
        if ($c -eq '"' -or ($c -eq '$' -and $t[$i + 1] -eq '"') -or ($c -eq '@' -and $t[$i + 1] -eq '"')) {
            $lit = Read-Literal $t $i
            if ($null -eq $lit) { break }
            $parts.Add($lit.Text); $holes += $lit.Holes; $i = $lit.End; continue
        }
        if ($c -eq '(') { $depth++; $i++; continue }
        if ($c -eq ')') { if ($depth -eq 0) { break }; $depth--; $i++; continue }
        if ($c -eq ',' -and $depth -eq 0) { break }
        if ($c -match '\s' -or $c -eq '+') { $i++; continue }
        $nonLiteral = $true; $i++
    }
    return @{ Text = ($parts -join ''); Holes = $holes; NonLiteral = $nonLiteral; End = $i }
}

function Get-Kind([string]$before, [string]$text, [bool]$dynamic) {
    $b = $before
    # 実行時の値を埋め込む文（{…}）や、文字列以外の式と連結した文は、動的。
    if ($dynamic) { return 'dynamic' }
    # 同じ文の中に、メッセージ・ステータス・検証・ログの呼び出しがあれば、動的（実行時に出る文）。
    if ($b -match '(MessageBox\.Show|SetStatus\(|ShowValidation\(|errors\.Add\(|warnings\.Add\(|\.WriteLine\(|\bLog\.Write\(|log\.Write\(|TrayLog\.Write\(|_status\.Text\s*=|TxtValidation\.Text\s*=|throw new|Exception\(|report\.\w+\(|Fail\()') { return 'dynamic' }
    if ($b -match 'ToolTip\s*=\s*$') { return 'tooltip' }
    if ($b -match '(?<![A-Za-z])Title\s*=\s*$') { return 'title' }
    if ($b -match 'Header\s*=\s*$') { return $(if ($b -match 'MenuItem') { 'menu' } else { 'header' }) }
    if ($b -match '(Help|Note|RowHelpBelow|AddHelp|HelpText)\(\s*$') { return 'help' }
    if ($b -match 'Content\s*=\s*$') {
        if ($b -match 'new (CheckBox|RadioButton)[^;]*$') { return 'checkbox' }
        if ($b -match '(new Button|Btn\()[^;]*$') { return 'button' }
        return 'button'
    }
    if ($b -match '(Btn|Button)\(\s*"[^"]*",\s*$') { return 'button' }
    if ($b -match 'Text\s*=\s*$') {
        if ($b -match '(TextWrapping|SubText)' -or (Get-DisplayWidth $text) -ge 60) { return 'help' }
        return 'label'
    }
    if ($b -match '(Row|Line|Field)\(\s*$') { return 'label' }
    if ($dynamic) { return 'dynamic' }
    # 呼び出しの形から分からないもの（コンストラクタの引数・選択肢の配列など）は、長さで分ける。
    $w = Get-DisplayWidth $text
    if ($w -ge 60) { return 'help' }
    if ($w -le 24) { return 'label' }
    return 'other'
}

# トレイで画面に出すのは、メニューと「バージョン情報」だけ。ほかのファイルの文は、ログ（tray.log）に書く文。
$TrayUiFiles = @('TrayContext.cs', 'AboutForm.cs', 'LayerMenuModel.cs', 'LayerDisplay.cs')

function Get-Window([string]$path) {
    $name = [IO.Path]::GetFileName($path) -replace '\.xaml\.cs$|\.Localize\.cs$|\.cs$|\.xaml$', ''
    return $name
}

$rows = [System.Collections.Generic.List[object]]::new()

# ---- C#（Loc.T） ----
$csFiles = @(Get-ChildItem (Join-Path $root 'kasane\Views') -Filter *.cs -File) + @(Get-ChildItem (Join-Path $root 'kasane-tray') -Filter *.cs -File -Recurse | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })
foreach ($f in $csFiles) {
    $t = [IO.File]::ReadAllText($f.FullName)
    $lineStarts = [System.Collections.Generic.List[int]]::new(); $lineStarts.Add(0)
    for ($k = 0; $k -lt $t.Length; $k++) { if ($t[$k] -eq "`n") { $lineStarts.Add($k + 1) } }
    foreach ($m in [regex]::Matches($t, '\bLoc\.T\(')) {
        $arg = Read-FirstArg $t ($m.Index + $m.Length)
        if ($arg.Text.Length -eq 0) { continue }
        $line = [Array]::BinarySearch($lineStarts.ToArray(), $m.Index); if ($line -lt 0) { $line = -$line - 2 }
        $from = [Math]::Max(0, $m.Index - 400)
        $before = $t.Substring($from, $m.Index - $from)
        $before = ($before -split ';')[-1]               # 同じ文の中だけを見る
        $dynamic = $arg.Holes -gt 0 -or $arg.NonLiteral
        $kind = Get-Kind $before $arg.Text $dynamic
        if ($f.FullName -match '\\kasane-tray\\' -and $TrayUiFiles -notcontains $f.Name) { $kind = 'log' }
        $rows.Add([pscustomobject]@{
            Window = Get-Window $f.FullName; File = $f.FullName.Substring($root.Length + 1); Line = $line + 1
            Kind = $kind; Width = Get-DisplayWidth $arg.Text; Text = ($arg.Text -replace "`r?`n", ' ⏎ ')
        })
    }
}

# ---- XAML の直書き（コードで上書きしていないもの） ----
foreach ($f in Get-ChildItem (Join-Path $root 'kasane\Views') -Filter *.xaml -File) {
    $code = (Get-ChildItem $f.DirectoryName -Filter ($f.BaseName + '*.cs') | ForEach-Object { [IO.File]::ReadAllText($_.FullName) }) -join "`n"
    $lines = [IO.File]::ReadAllLines($f.FullName)
    $currentName = $null
    for ($n = 0; $n -lt $lines.Length; $n++) {
        if ($lines[$n] -match 'x:Name="(\w+)"') { $currentName = $Matches[1] }
        elseif ($lines[$n] -match '^\s*<\w') { $currentName = $null }
        foreach ($am in [regex]::Matches($lines[$n], '\b(Text|Content|ToolTip|Title|Header)="([^"]*[぀-ヿ一-鿿][^"]*)"')) {
            $attr = $am.Groups[1].Value
            $overridden = $currentName -and ($code -match "\b$currentName\.$attr\s*=")
            if ($attr -eq 'Title' -and $code -match '\bTitle\s*=') { $overridden = $true }
            if ($overridden) { continue }
            $txt = $am.Groups[2].Value
            $kind = switch ($attr) { 'ToolTip' { 'tooltip' } 'Title' { 'title' } 'Header' { 'header' } 'Content' { 'button' } default { if ((Get-DisplayWidth $txt) -ge 60) { 'help' } else { 'label' } } }
            $rows.Add([pscustomobject]@{ Window = Get-Window $f.FullName; File = $f.FullName.Substring($root.Length + 1); Line = $n + 1; Kind = $kind; Width = Get-DisplayWidth $txt; Text = $txt })
        }
    }
}

# ---- License Manager（Strings.ja.resx） ----
$resx = Join-Path $root 'kasane-license\Properties\Strings.ja.resx'
if (Test-Path $resx) {
    [xml]$x = Get-Content $resx -Raw -Encoding utf8
    foreach ($d in $x.root.data) {
        $txt = [string]$d.value
        $kind = if ($txt -match '\{\d\}') { 'dynamic' } elseif ($d.name -match 'Tooltip|ToolTip') { 'tooltip' } elseif ($d.name -match 'Button|Btn') { 'button' } elseif ($d.name -match 'Title') { 'title' } elseif ((Get-DisplayWidth $txt) -ge 60) { 'help' } else { 'label' }
        $rows.Add([pscustomobject]@{ Window = 'LicenseManager'; File = 'kasane-license\Properties\Strings.ja.resx'; Line = 0; Kind = $kind; Width = Get-DisplayWidth $txt; Text = "[$($d.name)] " + ($txt -replace "`r?`n", ' ⏎ ') })
    }
}

if ($OutCsv) {
    $rows | Sort-Object Window, File, Line | Export-Csv -Path $OutCsv -NoTypeInformation -Encoding utf8BOM
    Write-Host "$($rows.Count) 件を $OutCsv に書きました。"
}
$rows | Group-Object Window | Sort-Object { ($_.Group | Where-Object { $_.Kind -notin 'dynamic','log' } | Measure-Object Width -Sum).Sum } -Descending | ForEach-Object {
    $s = $_.Group | Where-Object { $_.Kind -notin 'dynamic','log' }
    [pscustomobject]@{
        Window = $_.Name; All = $_.Count; Static = @($s).Count
        Help = @($s | Where-Object Kind -eq 'help').Count; Tooltip = @($s | Where-Object Kind -eq 'tooltip').Count
        Over40 = @($s | Where-Object Width -ge 40).Count; StaticWidth = ($s | Measure-Object Width -Sum).Sum
    }
} | Format-Table -AutoSize
