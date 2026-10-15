# トリガーの修飾キーを隠す処理（KeepMods）実機試験の結果（2026-10-15）

対象: コミット 82a5b02「tray: トリガーで動かす Sequence の間だけ、トリガーの修飾キーを OS から隠す」と、beb0093「docs: Alt・Win で F24 を内部で使う注意」。

## 結論

依頼の 5 項目はすべて期待どおりに動いた。FAIL は 0 件。ほかに観察が 1 件ある（Win+F16、後述。今回の機能の不具合とは断定できない）。

## 環境

| 項目 | 値 |
|---|---|
| ビルド | beb0093（kasane.exe、kasane-tray.exe とも ProductVersion 1.0.0+beb0093…） |
| kbdlayer.sys | 4EC924F29B5D86B8（前回の DPC 測定時の D58040CD98533752 から変更。今回の機能とは無関係） |
| Driver Verifier | 有効（`MODULE: kbdlayer.sys` を確認） |
| 対象キーボード | 内蔵 ACPI\LEN0071（UniqId 55F70000）。OIB で注入（スロット 0）。ドライバのレイヤーマップは OFF のまま |
| 設定の保存先 | 環境変数 `KASANE=C:\Users\applet\kasane`（`%LOCALAPPDATA%` ではない） |

## 方法

- トレイは `tray.json` の `Helper`（全キーボード共通）から読む。試験中だけ `Helper.Triggers` を書き込み、終了時にユーザーの元の `tray.json` へ戻した（ハッシュ一致を確認）。キーボード別の `55F70000\helper.json`（空の雛形）は触っていない。
- 注入したキーの結果は、試験用の窓（自作の WinForms）が受けたキーメッセージの順序で判定した。`C`=Ctrl、`S`=Shift、`A`=Alt、`PKT`=文字列入力（Unicode パケット）、`d`=押下、`u`=解放、`e`=拡張キー。
- 実際のキーボードではなく OIB 注入だが、ドライバのキー入力経路を通るため、トレイのフックから見ると物理打鍵と同じ（注入フラグなし）。

## 結果

### 1. Ctrl+F1 で文字だけが入る（Ctrl が掛からない）

| 試験 | 観測した順序 | 結果 |
|---|---|---|
| A: Ctrl+F1（手順 `a`、`b`） | `Cd Cu ad au bd bu Cd Cu` | PASS。実行中だけ Ctrl が離れ、終わって戻る。F1 は届かない |
| C: Ctrl+F2（文字列 `abc`） | `Cd Cu PKT×3 Cd Cu` | PASS。窓に `abc` だけが入った |

### 2. Ctrl を押し続けたままもう 1 回押す

| 試験 | 観測した順序 | 結果 |
|---|---|---|
| B: Ctrl を押したまま F1 を 2 回 | `Cd Cu ad au bd bu Cd Cu ad au bd bu Cd Cu` | PASS。毎回戻る。最後の `Cu` は物理の解放 |
| B2: 右 Ctrl（E0 1D）+ F1 | `Ced Ceu ad au bd bu Ced Ceu` | PASS。右側を隠し、右側を戻す |
| J: 実行中に Ctrl を離す（遅延 500 ms の手順） | `Cd Cu Cu ad au` | PASS。終了時に Ctrl は戻らない |
| I: Ctrl+Shift+F8 | `Cd Sd Cu Su ad au Cd Sd Su Cu` | PASS。両方を隠し、両方を戻す |

### 3. Alt・Win のトリガーでメニュー・スタートが開かない

| 試験 | 結果 |
|---|---|
| 対照: Alt を単独で押して離す | メニューが開く（試験の検出が働いている） |
| Alt+F14（既定）。Alt を長く／すぐ離す | PASS。どちらもメニューは開かない。順序は `Ad F24d F24u Au ad au Ad F24d F24u Au`（隠す前と戻した後に F24） |
| Win+F18（既定）。Win を長く／すぐ離す | PASS。スタートは開かない |
| 対照: Win を単独で押して離す | スタート（SearchHost）が開く |

### 4. 「修飾キーを送る」にチェック（KeepMods=true）で従来動作

| 試験 | 観測した順序 | 結果 |
|---|---|---|
| D: Ctrl+F3、KeepMods=true | `Cd ad au Cu` | PASS。Ctrl を離さない |
| F: Alt+F15、KeepMods=true | `Ad ad au Au`（F24 なし）。メニューは開いた | PASS（従来動作。Alt は隠さないのでメニューが開く） |
| Win+F18、KeepMods=true（出力なしの手順） | Win を離した時点でスタートが開いた | PASS（従来動作） |

### 5. 既存のトリガーへの影響

| 試験 | 観測した順序 | 結果 |
|---|---|---|
| G: F13 単独（手順 `a`） | `ad au` | PASS。修飾キーのイベントなし |
| K: 修飾キーなしの F1（トリガーは Ctrl+F1 のみ） | `F1d F1u` | PASS。トリガーにならず、そのまま届く |
| H: ReleaseSteps（Ctrl+F7、Steps は a の押下、ReleaseSteps は b） | `Cd Cu ad Cd bd bu Cu` | 参考。Steps の間は隠し、終了後に戻す。ReleaseSteps の `b` は Ctrl が押されたまま実行（仕様どおり、隠さない） |

### 設定画面（GUI）

| 項目 | 結果 |
|---|---|
| 種類が Launch のとき、チェックは表示されない | PASS |
| 種類が Sequence のトリガーでチェックが出る。表記は「実行中も、押している修飾キー（Ctrl など）を送る（チェックしないと、実行の間だけ隠す）」 | PASS |
| 既定は未チェック | PASS |
| GUI で作った Ctrl+F1（`text abc`）が、そのまま動く（Ctrl を隠す） | PASS |
| チェックして OK → `"KeepMods": true` が保存され、Ctrl を隠さなくなる | PASS |
| 再度開くとチェック済み。外して OK → `KeepMods` がファイルから消える（既定値は保存しない） | PASS |
| HLP 用の動作の編集画面（Sequence）にはチェックが出ない | PASS |

## 観察（要確認）

**Win+F16 のトリガーで、スライドしてシャットダウンの画面（SlideToShutDown）が出ることがある。**

- トリガーなしで Win+F16 を押しても出る。Windows 側のホットキーとみられる。
- トリガー（Win+F16、既定の隠す設定、出力は `a`）では、再現して 2 回とも Win を離した時点（約 0.8 秒後）に出た。
- 出力なしの手順（遅延 10 ms のみ）では、F16 でも出なかった（既定でも KeepMods=true でも）。F18 では、同じ操作で出なかった。
- 出力ありで KeepMods=true の F16 は、前面ウィンドウの状態が不安定で、結論を出せなかった。
- したがって、今回の機能（隠す処理）が原因かどうかは分からない。F16 で出力あり、という組み合わせだけで起きている。
- 実害の可能性: Win を含むトリガーで F16 を選ぶと、シャットダウン画面が出る。ドキュメントで F16 を勧めていれば、注意書きが要るかもしれない。

## 制限・未実施

- 実際の物理キーボードでの Alt/Win の確認は未実施（OIB 注入のみ）。
- メモ帳での確認は未実施。代わりに、自作の窓（メニュー付き WinForms）でメニューの開閉を検出した。
- `HLP(id)` のキーで隠さないこと（依頼の「対象外」）は、今回の試験の対象にしていない。
- `Repeat`（オートリピート）付きのトリガーでの隠す処理は未確認。
- 複数キーボードでの確認は未実施。
- ARM64 は未確認。

## 後始末

- `tray.json`、`55F70000\helper.json` は元のファイルへ戻した（ハッシュ一致）。
- 試験用の窓、KeymapTool、xperf は残っていない。トレイ（kasane-tray）は稼働中。
- 昇格ランナーは終了した。
- 試験の過程で、環境変数 `KASANE` が設定されていて、設定の保存先が `C:\Users\applet\kasane` になっていることを確認した。`%LOCALAPPDATA%\applet\kasane` に書いても読まれない。
