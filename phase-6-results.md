# P6 試験結果: のどか（nodoka4）の設定の取り込み

- 対象: 作業ブランチ `claude/tender-euler-xxfl7a`、コミット `2d415e7`（インストール済みの `kasane.exe` / `kasane-tray.exe` の ProductVersion が `1.0.0+2d415e7…`）。画面の試験は、インストール済みの `kasane.exe`（非管理者）を UIA で操作した。実機への反映（適用）は、画面の「適用」が管理者権限を要し UIA では操作できないため、**画面で保存・CLI 形式のエクスポートまで行い、同じ内容を `KeymapTool`（`--apply-simple`、`--set-macro`、`--set-key-override`）で入れて、キーを注入して確認した**（P4・P5 と同じ方法）。のどか（nodoka）などは停止済み。
- 試験日: 2026-10-10（試験担当）
- 実物の設定: `nodoka4` のリポジトリ（`C:\Users\applet\Documents\GitHub\nodoka\nodoka\distrib\files`、`…\contrib`）と、ユーザーのホームの `C:\Users\applet\nodoka\dot.nodoka`（`%NODOKA%`）。**読むだけで、書き換えていない**。
- 結論: **G1〜G15 の確認は PASS。ただし、重大な所見が 1 件**（ユーザー自身の設定が、のどかのインストール先の include を見つけられず、**1 件も変換できない**）。そのほかは軽微。

## 重大な所見

**include の探す場所に、のどかのインストール先（実行ファイルのフォルダ、レジストリの `currentPath`）がない。** ユーザー自身の設定（`C:\Users\applet\nodoka\dot.nodoka`、シンボル `NODOKA USE109`）は、先頭で `include "read-keyboard-define.nodoka"` している。このファイルは **`C:\Program Files\nodoka\` にしかなく**（ホームのフォルダにも `%NODOKA%` にもない）、kasane は「読めない行: `read-keyboard-define.nodoka': no such file or other error.」となって、その後のキーがすべて「キー「D」が定義されていません」になる。結果は**「変換できた 0 件、近似 0 件、変換できない 22 件」**。のどか本体では、同じ設定が動く。

- のどか本体の探し方（`nodoka/nodoka/setting.cpp:206` の `getHomeDirectories`）: ① レジストリの現在の設定ファイルのフォルダ、② `%NODOKA%`、③ `%HOME%`、④ `%USERPROFILE%`、⑤ `%HOMEDRIVE%%HOMEPATH%`、⑥ **のどかの実行ファイルのフォルダ**（`GetModuleFileName`。ここで `C:\Program Files\nodoka`。レジストリの `HKCU\Software\appletkan\nodoka` の `currentPath` にも保存される）。
- kasane（`docs/IMPORT-NODOKA.md`）: 「設定ファイルのフォルダ → `%NODOKA%` → `%HOME%` → `%USERPROFILE%` → `%HOMEDRIVE%%HOMEPATH%`」。①（レジストリの設定のフォルダ）と⑥（実行ファイルのフォルダ＝`currentPath`）が無い。
- 修正案: 探す場所の最後に、レジストリ `HKCU\Software\appletkan\nodoka` の `currentPath` と、現在の設定（`.nodokaN`）のフォルダを足す。
- 再現: のどかの設定一覧の「★ (ホームディレクトリから)」を選んで「変換する」（またはパスに `C:\Users\applet\nodoka\dot.nodoka`、シンボル `NODOKA USE109`）。ユーザー自身の設定の大半が、これで変換できない。

## 結果一覧

| # | 結果 | 内容（実測） |
|---|---|---|
| G1 | PASS | `dotnet test kasane.Tests`: **395 件すべて合格**（`NodokaTests` 34 件を一覧で確認。「トークナイザー」「パーサー」の件も合格）。ビルドの警告・エラー 0 |
| G2 | PASS | UIA: `T13`（5 件）、`T12`・`T11`・`T9` を含む `T1`〜`T13` **すべて PASS** |
| G3 | PASS（所見あり） | 実物の `default.nodoka`（`109.nodoka` などを include）をシンボル空・`USE109`・`USE104` で変換（各 約 0.4 秒）: **クラッシュなし、読めない行 0**。結果は 3 通りとも同じ: 「変換できた 43 件、近似 1 件、変換できない 391 件（レイヤー16枚、キー置換5）」。「変換できない」は、画面の一覧で重複を除くと 176 行。内訳: **レイヤー0以外での修飾キー付きの割り当て 44、`&Variable` 42、`&Prefix` 18、`&WindowMove` 16、`&WindowCling*` 16、`event prefixed` 6、`&Window*`（Minimize/Maximize/Lower…）ほか**。メモ: 申し送りの想定（「大半が `&Window*`・`&PostMessage`・`&Clipboard*`」）より、`&Variable`（Emacs 風の数引数）と「レイヤー0以外での修飾キー付き」が多い。レイヤーが 16 枚になる（上限）。**一覧の行は、実物の絶対パスで始まるため、肝心の理由が右へ隠れる**（横スクロールが要る） |
| G4 | PASS | `dot.nodoka`（`USE109`: 変換できた 0/近似 0/変換できない 3、`USE109 USEdefault`: 29/0/280、レイヤー16枚）、`emacsedit.nodoka`・`contrib\sample.nodoka`・`cursor.nodoka`・`ime.nodoka`: 断片のファイルは単独だとキーが未定義のため、`109.nodoka` を include するラッパーを作って変換: `sample`（18/0/4、キー置換12。`Esc`↔`半角/全角`、`Ctrl`↔`英数` の入れ替え）、`cursor`（1/1/0、`mod Mod1 += LCtrl` → `MO(1)`）、`emacsedit`（5/0/146、レイヤー6枚、`&Variable` が 100 件超。キーマップ `EmacsMove` などは「`&Prefix` から呼ばれないので変換しません」の情報）、`ime`（ブロック 1〜4 のコメントを外したコピー: 4/1/3。`変換 → LALT(KC_GRAVE)`、`RAlt`、`Ctrl+Space`、`Win+Space` が変換される。`IL-`（IME オン）の条件と `mod Alt -= RAlt` は変換できない）。**読めない行は、すべて 0** |
| G5 | PASS | `mod shift += !!Space` → 「Space → `MT(MOD_LSFT,KC_SPACE)`」（近似: 一回だけ有効になる修飾キー）。画面で取り込み（追加）→保存して、`project.json` の Space のスロットが `MT(MOD_LSFT,KC_SPACE)`。実機に入れて注入: Space を 80ms 押す → 空白（vk 20）、Space を押し続けて（400ms 後に）A → **Shift+A**（Shift 押下 → A → Shift 解放。空白なし）。Space だけを 800ms 押し続けると、Shift が押されて離れるだけ（空白は出ない） |
| G6 | PASS | `mod M0 = !Up` と `key M0-Left = Left Up` → 「Up → `MO(1)`」「レイヤー1 の Left → `MACRO(0)`（マクロ `KC_LEFT KC_UP`）」（近似: 複数キーの列）。保存後の `project.json` が一致。実機で、Up を押しながら（300ms 後に）Left → **「← ↑」**（vk 25、26）、Up 単独は何も出ない、Left 単独は Left |
| G7 | PASS | `window TW /pwsh\.exe:WindowsForms10\.Window/ : Global` と `key *A = *B` → 「レイヤー1 とアプリ別レイヤーの規則」。helper.json に `{"Name":"TW","Match":{"NodokaClass":"pwsh\\.exe:WindowsForms10\\.Window"},"Layers":[1]}`（`NodokaClass` の条件）。設定ダイアログの「アプリ別レイヤー」に `[有効] TW  のどかclass~… → レイヤー 1` と出る。実機: 一致するウィンドウ（試験用の窓）が前面のとき、アプリ管理レイヤー 1（0x0002）が立ち（48ms）、A → B（vk 42）。コンソール（`cmd.exe`）が前面のとき 0x0000 |
| G8 | PASS | `key C-A = Home`、`key W-N = &ShellExecute(...)`（試験では `cmd.exe /c echo … >> marker`、メモ帳を避けた）→ 前者は**キー置換**（`MOD_LCTL+KC_A → KC_HOME`）、後者は**ヘルパーのトリガー**（`KC_N` + `MOD_LGUI`、`Launch`。**近似**として報告）。設定ダイアログの「ヘルパー動作」の「トリガー」に `Win+KC_N — 起動: cmd.exe /c echo G8 >> marker-g8.txt` が出る（このキーボード用に書かれる）。実機: Ctrl+A → Home（vk 24）、Win+N → マーカーが書かれ、N のキーはアプリに届かない。メモ: `window` の後ろに書いた `key C-A`・`key W-N` は、のどかの意味でもウィンドウのキーマップ内なので、「レイヤー0以外での修飾キー付きの割り当ては対応しません」（仕様） |
| G9 | PASS | `def option UnitID = K1 ExtraInfo 0x55F70000` と `key K1-*A = *C`（共通に `key Q = W`）: 変換のまとめに「キーボードごとの設定が 1 台分あります」。取り込みに進むと「全体一括」の扱いになり、対応表に 2 行（「共通」と `0x55F70000`）。**どちらも 55F70000 が自動で選ばれる**（共通は今のキーボード、K1 は UniqId が同じ機器）。読み込み後、保存先の `project.json` に Q → W と A → C が両方入る。メモ: 共通の行の左側のラベルが **「? →」** と表示される |
| G10 | PASS | 同じ内容（`include "109.nodoka"`、日本語のキー名 `変換`・`無変換`・`半角/全角`、日本語のコメント）を UTF-8（BOM なし/あり）・UTF-16LE・CP932 で保存して変換: **4 つとも同じ結果**（変換できた 3、近似 1、変換できない 0）。「キーが定義されていません」なし |
| G11 | PASS | バイナリ（4KB の乱数）、空のファイル、1 万行（`key A = B` ×10000）、存在しない include、未定義のキー、閉じていない `if`、余分な `endif`、閉じていない文字列、自分自身を include するファイル、存在しないパス: **いずれもクラッシュ・固まりなし**。理由が出る（`invalid character U+0004`、`no such file or other error`、`unbalanced if`、`unbalanced endif`、`include is too deep`、`キー「Nonexistent」が定義されていません`、`…を読めません: Could not find a part of the path`）。メモ: 1 万行は、変換のまとめは出るが、一覧を最後まで読むのに約 2 分（画面が仮想化されたリストのため。変換自体の時間は計っていない）。存在しないパスの理由は英語の .NET のメッセージ |
| G12 | PASS | のどかの設定一覧（レジストリ `HKCU\Software\appletkan\nodoka`）に 5 件が出る（`★ (ホームディレクトリから)` ＋ 日本語 109 キーボード の 4 種）。★ が今のもの（1 件）。選ぶとパスとシンボルが入る（例: `C:\Program Files\nodoka\dot.nodoka`、`USE109 USEdefault`）。★（ホームディレクトリから）は、パスが `C:\Users\applet\nodoka\dot.nodoka`、シンボル `NODOKA USE109` になる |
| G13 | PASS | のどか由来は「追加」が既定。取り込み（追加）→ タイトルが `kasane *` → 保存。「置き換え」: 全体一括の扱い（G9）では、確認の後に保存先を直接書き換え、前に `backup\before-import-….kasane.json` ができる。キーボード単位（G5〜G8）の取り込みは、P5 と同じ（項目・置き換え／追加）。「確認」: 全体一括の扱いでは**押せない**（P5 の仕様どおり）。キーボード単位では、E10 と同じで変更しない |
| G14 | PASS（所見あり） | 目視（自分のウィンドウだけを撮影）: のどか取り込みの窓、インポートタブ（「のどかの設定を取り込む...」ボタンが「ファイルを選ぶ...」の隣）、取り込みの対応表。**文字の欠け・重なりなし**。所見: ① 変換結果の一覧の行が、長い絶対パスで始まって、理由が右へ隠れる。② 対応表の共通の行のラベルが「? →」。③ 窓の下が空白（一覧が高い。長い一覧には必要） |
| G15 | PASS | 回帰: P5 の書き出し・読み込み（`p5-e4`〜`e7` と同じ試験: 書き出し・置き換え・追加・番号の付け直し・二重追加で増えない）、P4（設定ダイアログ、キー割り当て）、保存、CLI 形式のエクスポートと実機への反映。`T9`・`T11`・`T12` は G2 で PASS。前回と同じ既知の違いのみ（`OverlapRatio` の `1.0`/`1` の表記） |

## 実施できなかった点
- 画面の「キーボードに適用」の操作（UIA は昇格したウィンドウを操作できない）。
- ユーザー自身の設定（`C:\Users\applet\nodoka\dot.nodoka`）の変換は、上の重大な所見のため、実質行えていない。修正後の再試験を希望（`include` を `C:\Program Files\nodoka` から探せること）。

## ほかの所見
1. 作業ツリーに、試験とは無関係の未コミットの変更がある（`kasane/Import/Nodoka/NodokaText.cs`: 行の区切りの判定 `c == ' '` が `c == '\u2028'` になっている）。試験担当は触っていない（更新時刻 2026-10-10 11:15）。インストールされた版（`2d415e7`）には含まれない。
2. 「変換できない」の件数（391）と、一覧の行数（重複を除くと 176）が違う（件数は、同じ行をレイヤーごとに数えているためと思われる）。一覧の表示と件数の関係が分かりにくい。
3. 一覧のフィルタの名前が「変換できなかったもの」で、まとめでは「変換できない」。

## 試験の後始末
- 試験の前に退避した `%LOCALAPPDATA%\applet\kasane`・`%LOCALAPPDATA%\LayerDriver` を元に戻した（`55F70000\project.json` 2385 バイト。試験で作った `backup\`、`helper.json`、`tray.json`、`tray.log` は消した）。キーボードのレイヤーマップは `--clear`、キー置換・マクロは `--clear` で消えている（`HasLayerMap=0`）。試験用の `.nodoka` は、スクラッチの `x6\` にあり、リポジトリには書いていない。
- ユーザーの `C:\Users\applet\nodoka` と、nodoka のリポジトリは、読んだだけ（変更なし）。
- インストール済みのトレイ（このセッション）が動いている。
