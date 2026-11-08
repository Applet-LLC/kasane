param([string]$Csv, [string]$Out)
$ErrorActionPreference = 'Stop'
$rows = Import-Csv $Csv

# ---- 文ごとの判断（Window:Line）。無いものは、種類と長さの既定の判断 ----
$S = '状況の文'   # その状況のときだけ出る文。削る対象から外し、手順 4（インジケータ・ステータス）で扱う
$o = @{}
function Set-Rec([string]$rec, [string]$note, [string[]]$keys) { foreach ($k in $keys) { $script:o[$k] = @($rec, $note) } }

# 状況の文（状態・結果・エラー・確認）
Set-Rec $S '状態の文。手順 4 で印・ステータスに寄せる' @(
 'CalibrationWindow:90','CalibrationWindow:91','CalibrationWindow:189','CalibrationWindow:195','CalibrationWindow:429',
 'EmergencyCheckWindow:89','EmergencyCheckWindow:145','HelperTabBase:73','IdentifyWindow:76','ImeLayerTab:70','ImeLayerTab:71',
 'ImportExportWindow:306','ImportExportWindow:393','ImportExportWindow:424','ImportExportWindow:460','ImportExportWindow:462','ImportExportWindow:498',
 'ImportExportWindow:499','ImportExportWindow:506','ImportExportWindow:509','ImportExportWindow:514','ImportExportWindow:560','ImportExportWindow:643',
 'ImportExportWindow:868','ImportExportWindow:888','ImportExportWindow:910','KeyAssignDialog:561','MacroDirectory:97','MacroDirectory:103',
 'MainWindow:18','MainWindow:1152','MainWindow:1153','MainWindow:1156','MainWindow:1256','NicolaLinkPanel:65','NicolaLinkPanel:74',
 'NodokaImportWindow:128','NodokaImportWindow:211','KleImportWindow:304','HelperActionsTab:153','HelperActionsTab:213',
 'IdListEditorWindow:241','IdListEditorWindow:242','DeleteConfirmWindow:48','HelperActionEditorWindow:208',
 'SettingsDialog:330','SettingsDialog:379','SettingsDialog:565','SettingsDialog:608','SettingsDialog:611','SettingsDialog:686','SettingsDialog:693',
 'SettingsDialog:757','SettingsDialog:758','SettingsDialog:764','SettingsDialog:770','SettingsDialog:779','SettingsDialog:785','SettingsDialog:849',
 'SettingsDialog:854','SettingsDialog:1092','SettingsDialog:1104','SettingsDialog:1114','SettingsDialog:1120','SettingsDialog:1131','SettingsDialog:1147',
 'SettingsDialog:1165','SettingsDialog:1166','TrayContext:173','TrayContext:199','TrayContext:210','TrayContext:237','TrayContext:245','TrayContext:246')
Set-Rec $S 'メイン画面の状態の印（閲覧専用）へ。帯の文は短くする' @('MainWindow.Localize:12')
Set-Rec '残す' '電源キーの注意。その状況のときだけ出ている' @('KeyAssignDialog:792')
Set-Rec '対象外' 'ファイルの種類の選択肢' @('HelperActionEditorWindow:322','ImportExportWindow:367','ImportExportWindow:629','NodokaImportWindow:141','RemapTextWindow:64')

# 画面の先頭や欄の下に、いつも出ている説明の段落 → 「?」で開くマニュアルの節へ
Set-Rec 'マニュアルへ' '「?」で開く節に同じ内容があるか確かめる' @(
 'AppRuleEditorWindow:45','AppRulesTab:19','ComboEditorWindow:19','KeyOverrideEditorWindow:17','TapDanceEditorWindow:15','HelperActionsTab:29',
 'HelperActionEditorWindow:111','HelperActionEditorWindow:121','HelperActionEditorWindow:156','HelperActionEditorWindow:216','HelperActionEditorWindow:228',
 'ImeLayerTab:24','NicolaLinkPanel:29','NicolaEditorWindow:55','NicolaEditorWindow:73','KleImportWindow:97','NodokaImportWindow:55','WindowPickerWindow:31',
 'ImportExportWindow:224','ImportExportWindow:242','ImportExportWindow:249','ImportExportWindow:418','ImportExportWindow:572','ImportExportWindow:741',
 'KeyAssignDialog:227','KeyAssignDialog:264','KeyAssignDialog:298','KeyAssignDialog:367','KeyAssignDialog:481','KeyAssignDialog:503',
 'SettingsDialog:136','SettingsDialog:184','SettingsDialog:315','SettingsDialog:383','SettingsDialog:415','SettingsDialog:466','SettingsDialog:493',
 'SettingsDialog:513','SettingsDialog:800','SettingsDialog:896','CalibrationWindow:176','CalibrationWindow:182')
Set-Rec 'マニュアルへ' 'キー図の凡例。「?」か、小さな凡例のポップアップにする' @('MainWindow.Localize:68')
Set-Rec '短くする' '「この画面が前面の間、キーは Windows に届きません」の 1 行だけ残す（知らないと操作できないと誤解する）' @('CalibrationWindow.Localize:22','CalibrationWindow:204')
Set-Rec '短くする' '「再起動後に効きます」だけ残す' @('SettingsDialog:160')
Set-Rec '短くする' 'チェックの文は「割り当ても外す」にし、外し方はツールチップへ' @('DeleteConfirmWindow:41')
Set-Rec '短くする' 'ラベルは「修飾キー:」にし、括弧の中はツールチップへ' @('KeyAssignDialog:88','KeycodePickerWindow:85')
Set-Rec '短くする' '括弧の中はツールチップへ' @('AppRuleEditorWindow:70','HelperActionEditorWindow:188','KeycodePickerWindow:35','ImportExportWindow:73','ImportExportWindow:74','ImportExportWindow:75','ImportExportWindow:103','ImportExportWindow:105')
Set-Rec '短くする' '選択肢の英語の型名（Launch など）を外す（英語表示では残す）' @('HelperActionEditorWindow:25','HelperActionEditorWindow:26','HelperActionEditorWindow:27','HelperActionEditorWindow:28','HelperActionEditorWindow:29','HelperActionEditorWindow:31')
Set-Rec '短くする' '選択肢の括弧の補足を外し、ツールチップへ' @('SettingsDialog:22','SettingsDialog:23','SettingsDialog:24','SettingsDialog:29','SettingsDialog:30','SettingsDialog:31','SettingsDialog:456','SettingsDialog:457','SettingsDialog:458','SettingsDialog:483')
Set-Rec 'ツールチップへ' '欄の意味・書き方の例' @(
 'AppRuleEditorWindow:57','AppRuleEditorWindow:59','AppRuleEditorWindow:60','AppRuleEditorWindow:61','AppRuleEditorWindow:62',
 'HelperActionEditorWindow:130','HelperActionEditorWindow:153','HelperActionEditorWindow:204','HelperActionEditorWindow:307','HelperActionEditorWindow:308',
 'SettingsDialog:394','SettingsDialog:395','SettingsDialog:396','SettingsDialog:400','SettingsDialog:410','SettingsDialog:412',
 'SettingsDialog:1046','SettingsDialog:1047','SettingsDialog:1049','KeyAssignDialog:408','KeyAssignDialog:420')
Set-Rec '消す' 'ラベルの言い直し・見れば分かる' @('SettingsDialog:154','HelperActionsTab:53','IdListEditorWindow:359','RemapTextWindow:43','DeleteConfirmWindow:72','HelperActionsTab:157')
Set-Rec '残す' '一覧の見出し' @('KeyAssignDialog:235','KeyAssignDialog:271')
Set-Rec '残す' '仮想キーの名前は、選ぶときの手がかり' @('StepInsertBar:53','StepInsertBar:54','StepInsertBar:55','StepInsertBar:56','StepInsertBar:57','StepInsertBar:58','StepInsertBar:59','StepInsertBar:60')
Set-Rec $S '状態の文（未選択・未接続・取り込み中の表示）' @('ImportExportWindow:59','ImportExportWindow:435','ImportExportWindow:703','ImportExportWindow:748','KeycodeField:42','NicolaEditorWindow:265')
Set-Rec '残す' '操作の名前として必要（短くすると何をするか分からない）' @('MainWindow.Localize:61','KeyAssignDialog:240','KeyAssignDialog:276','SettingsDialog:551','SettingsDialog:563','AppRuleEditorWindow:64','ImportExportWindow:579','ImportExportWindow:50')
Set-Rec 'ツールチップへ' 'モードの説明。ラジオボタンのツールチップにする' @('CalibrationWindow.Localize:14','CalibrationWindow.Localize:17','CalibrationWindow.Localize:20')

function Default-Rec($r) {
    $w = [int]$r.Width; $t = $r.Text
    $paren = $t -match '[(（][^)）]{6,}[)）]'
    switch ($r.Kind) {
        { $_ -in 'title','header','menu' } { if ($w -gt 30 -or $paren) { return @('短くする', '括弧の補足を外す') } return @('残す', '') }
        'button'   { if ($paren) { return @('短くする', '括弧の中はツールチップへ') } if ($w -gt 24) { return @('短くする', 'ボタンは短い動詞に') } return @('残す', '') }
        'checkbox' { if ($paren -or $w -gt 30) { return @('短くする', '括弧の中・説明はツールチップへ') } return @('残す', '') }
        'tooltip'  { if ($w -gt 60) { return @('短くする', 'ツールチップは 1 文、40 字くらいまで') } return @('残す', '') }
        'help'     { if ($w -ge 100) { return @('マニュアルへ', '「?」で開く節に同じ内容があるか確かめる') } return @('ツールチップへ', '') }
        default    { if ($paren) { return @('短くする', '括弧の中はツールチップへ') } if ($w -gt 40) { return @('ツールチップへ', '') } return @('残す', '') }
    }
}

$kindJa = @{ title='題名'; header='見出し'; menu='メニュー'; button='ボタン'; checkbox='チェック'; label='ラベル'; tooltip='ツールチップ'; help='説明'; other='その他' }
$static = foreach ($r in $rows) {
    if ($r.Kind -in 'dynamic','log') { continue }
    $key = "$($r.Window):$($r.Line)"; if ($r.File -match '\.Localize\.cs$') { $key = "$($r.Window).Localize:$($r.Line)" }
    $rec = if ($o.ContainsKey($key)) { $o[$key] } elseif ($r.Window -eq 'LicenseManager' -and $r.Text -match '^\[(Diag_|Msg_|Err_|Status_)') { @($S, 'License Manager の状態・結果の文') } else { Default-Rec $r }
    [pscustomobject]@{ Window = $r.Window; File = $r.File; Line = [int]$r.Line; Kind = $kindJa[$r.Kind]; Width = [int]$r.Width; Text = $r.Text; Rec = $rec[0]; Note = $rec[1] }
}

# ---- 用語の揺れ（静的な文の中の出現数） ----
$all = ($rows | Where-Object { $_.Kind -notin 'log' } | ForEach-Object Text) -join "`n"
function Count([string]$pat) { ([regex]::Matches($all, $pat)).Count }
$terms = @(
 @('キーボードへの書き込み', @('適用', '反映')),
 @('定義を消す', @('削除', 'クリア', '消す|消します|消し')),
 @('透過', @('透過', '▽')),
 @('リマップを止める', @('OFF\(素通し', '素通し', 'リマップをOFF')),
 @('長押し', @('長押し', 'ホールド', 'Hold')),
 @('レイヤーを使える状態にする', @('立てる', '有効にする', 'ON にする')),
 @('IME 連動の表記', @('IME連動', 'IME 連動')),
 @('オン・オフの表記', @('ON/OFF', 'ON / OFF', 'オン・オフ|オンとオフ')),
 @('管理者', @('管理者権限モード', '閲覧専用モード', '管理者として実行', '管理者として再起動')),
 @('設定の画面', @('設定画面', '設定ダイアログ'))
)

$sb = [System.Text.StringBuilder]::new()
function L([string]$s = '') { [void]$sb.AppendLine($s) }
L '# GUI の文言の一覧と、削る案（2026-11-08）'
L ''
L 'GUI の各画面（エディタ・トレイのメニュー・License Manager）の文言の一覧です。静的な文（いつも出ている見出し・ボタン・説明・ツールチップ）に、1 件ずつ推奨を付けました。推奨の列を直して返してください（違うと思うものだけで構いません）。'
L ''
L '作り方: `tools/List-UiTexts.ps1` で `Loc.T` の呼び出し・XAML の直書き・License Manager の resx を集め、種類と見た目の幅（全角 2）を付けました。推奨は、説明の段落・中くらいの長さの文（約 210 件）を 1 件ずつ読んで決め、ほかは種類と長さで決めています。全件（動的な文・ログを含む）は `ui-texts.csv` にあります。'
L ''
L '## 推奨の意味'
L ''
L '| 推奨 | 直し方 |'
L '|---|---|'
L '| 消す | ラベルの言い直し・見れば分かる手順。消して、代わりも置かない |'
L '| 短くする | ボタン・ラベル・チェック・選択肢に付いた括弧書きや説明を外す。外した中身は、必要ならツールチップへ |'
L '| ツールチップへ | 欄の意味・書き方の例。画面からは消し、欄やボタンのツールチップ（1 文、40 字くらいまで）にする |'
L '| マニュアルへ | 画面の先頭や欄の下にいつも出ている説明。画面からは消し、「?」で開くマニュアルの節に同じ内容があるかを確かめる（無ければ書き足す） |'
L '| 状況の文 | その状況のときだけ出る状態・結果・エラーの文。削る対象から外し、手順 4（インジケータ・ステータス）で、印や色と合わせて見直す |'
L '| 残す | 今のままでよい短い文。または、事故を防ぐ注意（電源キーなど） |'
L '| 対象外 | ファイルの種類の選択肢など、文言の見直しに当たらないもの |'
L ''
L '## 画面ごとの件数'
L ''
L '| 画面 | 静的な文 | 消す | 短くする | ツールチップへ | マニュアルへ | 状況の文 | 残す | 動的・ログ |'
L '|---|---|---|---|---|---|---|---|---|'
$byWin = $static | Group-Object Window
$dyn = $rows | Where-Object { $_.Kind -in 'dynamic','log' } | Group-Object Window
$totals = @{}
foreach ($g in ($byWin | Sort-Object { ($_.Group | Measure-Object Width -Sum).Sum } -Descending)) {
    $c = @{}; foreach ($x in $g.Group) { $c[$x.Rec] = 1 + [int]$c[$x.Rec] }
    $d = ($dyn | Where-Object Name -eq $g.Name | ForEach-Object Count)
    L ("| {0} | {1} | {2} | {3} | {4} | {5} | {6} | {7} | {8} |" -f $g.Name, $g.Count, [int]$c['消す'], [int]$c['短くする'], [int]$c['ツールチップへ'], [int]$c['マニュアルへ'], [int]$c[$S], ([int]$c['残す'] + [int]$c['対象外']), [int]$d)
    foreach ($k in $c.Keys) { $totals[$k] = [int]$totals[$k] + $c[$k] }
}
L ("| **合計** | {0} | {1} | {2} | {3} | {4} | {5} | {6} | {7} |" -f @($static).Count, [int]$totals['消す'], [int]$totals['短くする'], [int]$totals['ツールチップへ'], [int]$totals['マニュアルへ'], [int]$totals[$S], ([int]$totals['残す'] + [int]$totals['対象外']), @($rows | Where-Object { $_.Kind -in 'dynamic','log' }).Count)
L ''
$gone = $static | Where-Object { $_.Rec -in '消す','ツールチップへ','マニュアルへ' }
$short = $static | Where-Object { $_.Rec -eq '短くする' }
$alwaysWidth = ($static | Where-Object { $_.Rec -notin $S, '対象外' } | Measure-Object Width -Sum).Sum
L ("画面にいつも出ている文（状況の文と対象外を除く）は、見た目の幅の合計で {0:N0}。そのうち、画面から無くなる文（消す・ツールチップへ・マニュアルへ）が {1:N0}（{2:P0}）、短くする文が {3:N0}（{4:P0}）です。件数では少なく見えますが、無くなるのは長い説明の段落なので、画面の文字の量はおよそ半分になる見込みです。" -f $alwaysWidth, ($gone | Measure-Object Width -Sum).Sum, (($gone | Measure-Object Width -Sum).Sum / $alwaysWidth), ($short | Measure-Object Width -Sum).Sum, (($short | Measure-Object Width -Sum).Sum / $alwaysWidth))
L ''
L '## 用語の揺れ'
L ''
L '同じ操作・同じ概念に、別の言葉を使っているところです（全文の出現数。動的な文を含む）。直すときに、どちらに揃えるかを決めてください。'
L ''
L '| 概念 | 言葉と出現数 |'
L '|---|---|'
foreach ($t in $terms) { L ("| {0} | {1} |" -f $t[0], (($t[1] | ForEach-Object { "{0} {1}" -f ($_ -replace '\\\(', '(' -replace '\|.*', ''), (Count $_) }) -join '、')) }
L ''
L '## 画面ごとの一覧'
L ''
L '幅は、等幅の字体での見た目の幅（全角 2）。文が長いものは、先頭の 120 字だけを出しています（全文は CSV）。'
foreach ($g in ($byWin | Sort-Object { ($_.Group | Measure-Object Width -Sum).Sum } -Descending)) {
    L ''
    L "### $($g.Name)"
    L ''
    L '| 行 | 種類 | 幅 | 文 | 推奨 | メモ |'
    L '|---|---|---|---|---|---|'
    foreach ($x in ($g.Group | Sort-Object File, Line)) {
        $txt = $x.Text; if ($txt.Length -gt 120) { $txt = $txt.Substring(0, 120) + '…' }
        $txt = $txt.Replace('|', '\|')
        $loc = if ($x.Line -gt 0) { "$([IO.Path]::GetFileName($x.File)):$($x.Line)" } else { [IO.Path]::GetFileName($x.File) }
        L ("| {0} | {1} | {2} | {3} | **{4}** | {5} |" -f $loc, $x.Kind, $x.Width, $txt, $x.Rec, $x.Note)
    }
}
[IO.File]::WriteAllText($Out, $sb.ToString(), [Text.UTF8Encoding]::new($false))
"静的 $(@($static).Count) 件"
$totals.GetEnumerator() | Sort-Object Value -Descending | ForEach-Object { "{0} {1}" -f $_.Key, $_.Value }
