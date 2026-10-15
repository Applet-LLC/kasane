# キー置換の置き換え先 MACRO(n) 追試の結果（2026-10-16）

対象: コミット 99f8170「driver: キー置換のマクロ送出中は、離した修飾キーを先に戻す規則を止める」（ブランチ feature/keyoverride-macro）。前回の結果は key-override-macro-results.md（64503c5、マクロの出力に Ctrl が掛かった）。

## 結論

**前回の FAIL は直った。** 依頼の再試験項目は、すべて期待どおりだった（FAIL は 0 件）。回帰の項目も前回と同じく合格。

## 環境

| 項目 | 値 |
|---|---|
| ビルド | 99f8170（kasane.exe、kasane-tray.exe とも ProductVersion 1.0.0+99f8170…） |
| kbdlayer.sys | AA853246006E0AE1（前回は 5A6BDAD91D2210BC） |
| Driver Verifier | 有効（`MODULE: kbdlayer.sys`） |
| 機能 | Macro・Combo・TapDance・KeyOverride すべて ON |
| 対象キーボード | 内蔵 ACPI\LEN0071（UniqId 55F70000）。OIB で注入（スロット 0） |
| 試験用の設定 | 前回と同じ（マクロ 7＝`a b`、マクロ 8＝`a`×32、簡易マップ、キー置換 1〜7） |

依頼文のコミット番号は 2938178 だが、インストールされたビルドの ProductVersion とリポジトリの feature/keyoverride-macro の先頭は 99f8170。2938178 は見つからなかった（`git log --all` に無い）。99f8170 の内容（マクロ送出中は規則を止める）は依頼文の説明と一致する。

ユーザーのレイヤーマップは、前回の試験後に消去したまま（再適用されていなかった）。試験中だけ簡易マップを使い、終了後に消去した。

## 結果

### 再試験の項目

| 試験 | 観測した順序 | 結果 |
|---|---|---|
| T1 Ctrl+F1 → MACRO(7) | `Cd Cu ad au bd bu Cd Cu` | **PASS**（前回 FAIL） |
| T1b 右 Ctrl（E0 1D） | `Ced Ceu ad au bd bu Ced Ceu` | **PASS**（前回 FAIL） |
| T2 Ctrl を押したまま F1 を 2 回 | `Cd Cu ad au bd bu Cd Cu ad au bd bu Cd Cu` | **PASS**（前回 FAIL） |
| T3 F1 を押し続ける（既定: 繰り返さない） | マクロ 1 回のみ | PASS |
| T3b F2 を押し続ける（repeat 指定。押下 1 回＋リピート 4 回） | マクロ 5 回。毎回 `Cu ad au bd bu Cd` | **PASS**（前回は毎回 Ctrl が掛かった） |
| T4a Alt+F14 → MACRO(7) | メニューは開かない | **PASS**（前回 FAIL） |
| T4a2 Alt+F14、Alt をすぐ離す | メニューは開かない | **PASS**（前回 FAIL） |
| T4b Win+F18 → MACRO(7)（`a`、`b`）。長く／すぐ離す | 前面ウィンドウは変わらない（スタート・Win+A のクイック設定は出ない） | PASS |

Win の判定の対照: マップを使わずに Win+A を実際に押すと、前面が `ShellHost`（クイック設定）に変わる。Win+F18 のマクロでは変わらなかったので、Win は前回と違い、マクロの出力に掛かっていないと言える。Alt の対照（Alt を単独で押して離す）では、メニューが開く（検出は有効）。

### 回帰（前回合格した項目）

| 試験 | 観測した順序 | 結果 |
|---|---|---|
| T5a マクロ機能 OFF: Ctrl+F1 | `Cd F1d F1u Cu` | PASS（素通し） |
| T5a2 マクロ機能 OFF: Shift+BS | `Sd Su Deled Deleu Sd Su` | PASS（キー置換は有効） |
| T5b キー置換機能 OFF: Ctrl+F1 | `Cd F1d F1u Cu` | PASS（素通し） |
| T5b2 キー置換機能 OFF: Shift+BS | `Sd BSd BSu Su` | PASS（素通し） |
| T5c 機能を ON に戻す: Ctrl+F1 | `Cd Cu ad au bd bu Cd Cu` | PASS |
| T8 Ctrl+F9 → MACRO(9)（未設定） | `Cd Cu`（修飾キーに触れず、F9 は飲み込まれる） | PASS |
| T9 Shift+BS → Delete | `Sd Su Deled Deleu Sd Su` | PASS |
| T9b BS 単独、T9c Ctrl+BS | 素通し | PASS |
| T7 Ctrl+F3（64 ステップ）、F3 押下の 2 ms 後に Ctrl を離す | 実行後に Ctrl は押されたまま残らない（LCtrl・RCtrl・Ctrl すべて離れている） | PASS |
| T7b そのあと `a` だけを打つ | `ad au`（Ctrl なし） | PASS |
| T7c Ctrl+F1 をもう一度 | `Cd Cu ad au bd bu Cd Cu` | PASS |
| 試験終了後のキーの押しっぱなし確認（LCtrl・RCtrl・LShift・LAlt・LWin・a） | すべて離れている | PASS |

## 観察（不具合ではない）

T7 の観測は、`Cd Cu ad×32 Cd Cu` だった。Ctrl の物理の解放が、マクロを送り終わるまで待たされたため、マクロの最後に戻された Ctrl が、その直後の解放で離れた（`Cd Cu` の 2 つ目）。Ctrl だけが一瞬押される形になるが、押されたまま残ることはなく、キーの出力は伴わない。

## 制限・未実施

- GUI（キー置換の編集画面のチェック、`MACRO(n)` のときだけ出る「オートリピートも、マクロを繰り返す」）は、今回の追試の対象にしていない。CLI の設定と `--get-key-override` で確認した。
- マクロの中身に修飾キー付きのキー（`LCTL(KC_C)` など）を入れた場合の動き（依頼文の「残りの注意点」）は確認していない。
- 物理キーボードでの確認は未実施（OIB 注入のみ）。ARM64 は未確認。

## 後始末

- 簡易マップを消去（`HasLayerMap=0`、`Enabled=0`）。キー置換 1〜7、マクロ 7・8 を消去。機能は Macro・Combo・TapDance・KeyOverride すべて ON に戻した。
- マクロ 0（20 ステップ）、`tray.json`、`55F70000\helper.json` は変更していない（`tray.json` はバックアップとハッシュ一致）。
- 試験用の窓、KeymapTool は残っていない。トレイは稼働中。昇格ランナーは終了。
- ユーザーのレイヤーマップは、前回の試験後から未適用のまま。GUI で再適用が必要。
