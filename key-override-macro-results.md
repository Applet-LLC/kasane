# キー置換の置き換え先 MACRO(n) 実機試験の結果（2026-10-15）

対象: コミット 64503c5「driver/GUI/CLI: キー置換の置き換え先に MACRO(n) を許す（条件の修飾キーを隠して送る。オートリピートは既定オフ）」。

## 結論

**主目的（マクロの出力に Ctrl・Alt が掛からないこと）は満たされていない。** Ctrl+F1 を `MACRO(7)`（`a`、`b`）に結び付けると、条件の修飾キーを一度離したあと、**マクロの最初のキーの直前に修飾キーが戻る**。マクロの `a`・`b` は Ctrl+A、Ctrl+B として出力され、試験用のテキストボックスには 1 文字も入らなかった。

一方、オートリピートの扱い、機能の ON/OFF、未設定マクロ、既存のキー置換の回帰、実行中に Ctrl を離したときの押しっぱなしは、期待どおりだった。Alt を条件にした場合は、メニューが開いた（FAIL）。

## 環境

| 項目 | 値 |
|---|---|
| ビルド | 64503c5（kasane.exe、kasane-tray.exe とも ProductVersion 1.0.0+64503c5…） |
| kbdlayer.sys | 5A6BDAD91D2210BC |
| Driver Verifier | 有効（`MODULE: kbdlayer.sys`） |
| 機能 | Macro・Combo・TapDance・KeyOverride すべて ON |
| 対象キーボード | 内蔵 ACPI\LEN0071（UniqId 55F70000）。OIB で注入（スロット 0） |
| 試験用の設定 | マクロ 7（`KC_A KC_B`）、マクロ 8（`KC_A`×32＝64 ステップ）、簡易マップ（A→A のみ。修飾キーは素通し）。キー置換: 1=Ctrl+F1→MACRO(7)、2=Ctrl+F2→MACRO(7) repeat、3=Alt+F14→MACRO(7)、4=Win+F18→MACRO(7)、5=Shift+BS→Delete、6=Ctrl+F3→MACRO(8)、7=Ctrl+F9→MACRO(9)（未設定） |

ユーザーのレイヤーマップ（Ctrl・Shift が MO、F1 の L1 が MACRO(0)）は、キー置換が通常の Ctrl を前提にするため、確認のうえで試験中は簡易マップに差し替え、終了後は消去した（`HasLayerMap=0`）。マクロ 0（20 ステップ）は触っていない。ユーザーが GUI で再適用する。

## 方法

試験用の窓（自作の WinForms）が受けたキーメッセージの順序で判定した。`C`=Ctrl、`S`=Shift、`a`/`b`=文字、`Del`=Delete、`d`=押下、`u`=解放、`e`=拡張キー。物理 Ctrl の解放は最後の `Cu`。

## 結果

### 1・2. Ctrl+F1 → MACRO(7)（期待: `Cd Cu ad au bd bu Cd Cu`）

| 試験 | 観測した順序 | 結果 |
|---|---|---|
| T1 Ctrl+F1 | `Cd Cu Cd ad au bd bu Cu` | **FAIL**。Ctrl を離したあと、`a` の前に Ctrl が戻る |
| T1b 右 Ctrl | `Ced Ceu Ced ad au bd bu Ceu` | **FAIL**（同じ） |
| T2 Ctrl を押したまま F1 を 2 回 | `Cd Cu Cd ad au bd bu Cu Cd ad au bd bu Cu` | **FAIL**（毎回、`a` の前に戻る） |

テキストボックスは空のままだった（Ctrl+A・Ctrl+B として処理された）。Ctrl が戻る時刻は、離した時刻と同じ（ミリ秒の同刻）で、`a` の直前だった。キーログの時刻: `Cu` 1567406、`Cd` 1567406、`a` 押下 1567421。

### 3. オートリピート

| 試験 | 観測した順序 | 結果 |
|---|---|---|
| T3 F1 を押し続ける（4 回リピート）、既定 | マクロ 1 回のみ（`Cd Cu Cd ad au bd bu Cu`） | PASS（繰り返さない）。ただし Ctrl は上と同じ理由で出力に掛かる |
| T3b F2 を押し続ける（4 回リピート）、repeat 指定 | マクロ 5 回（押下 1 回＋リピート 4 回） | 繰り返しは PASS。毎回 `a` の前に Ctrl が戻るため、「リピートのたびに Ctrl が掛からない」は **FAIL** |

### 4. Alt・Win を条件にした場合

| 試験 | 結果 |
|---|---|
| 対照: Alt を単独で押して離す | メニューが開く（検出は有効） |
| T4a Alt+F14 → MACRO(7) | **FAIL**。メニューが開いた（`act`） |
| T4a2 Alt+F14、すぐ離す | **FAIL**。メニューが開いた（`act,deact,act`） |
| T4b Win+F18 → MACRO(7)（長く／すぐ離す） | スタートは開かなかった。ただし Win も Ctrl と同じ経路で、マクロの前に戻るはずなので、前面ウィンドウの変化だけでは判断できない（参考） |

### 5. 既存のキー置換の回帰

| 試験 | 観測した順序 | 結果 |
|---|---|---|
| T9 Shift+BS → Delete | `Sd Su Deled Deleu Sd Su` | PASS |
| T9b BS 単独 | `BSd BSu` | PASS |
| T9c Ctrl+BS（置換なし） | `Cd BSd BSu Cu` | PASS |

### 6. 実行中に Ctrl を離したとき

| 試験 | 結果 |
|---|---|
| T7 Ctrl+F3（64 ステップのマクロ）、F3 押下の 2 ms 後に Ctrl を離す | PASS。最後の `Cu` だけで、押されたまま残らない。実行後の OS の状態: LCtrl・RCtrl・Ctrl すべて離れている |
| T7b そのあと `a` だけを打つ | PASS。`ad au`（Ctrl なし） |
| 試験終了後のキーの押しっぱなし確認（LCtrl・RCtrl・LShift・LAlt・LWin・a） | すべて離れている |

### 7. 機能 OFF・未設定マクロ

| 試験 | 観測した順序 | 結果 |
|---|---|---|
| T5a マクロ機能 OFF: Ctrl+F1 | `Cd F1d F1u Cu` | PASS（素通し） |
| T5a2 マクロ機能 OFF: Shift+BS | `Sd Su Deled Deleu Sd Su` | PASS（キー置換は別機能なので有効） |
| T5b キー置換機能 OFF: Ctrl+F1 | `Cd F1d F1u Cu` | PASS（素通し） |
| T5b2 キー置換機能 OFF: Shift+BS | `Sd BSd BSu Su` | PASS（素通し） |
| T5c 機能を ON に戻す | マクロが動くが、`a` の前に Ctrl が戻る | T1 と同じ |
| T8 Ctrl+F9 → MACRO(9)（未設定） | `Cd Cu` | PASS。修飾キーに触れず、F9 は飲み込まれ、何も出ない |

## 原因の見立て（コードを読んだだけで、修正はしていない）

`driver/kbdlayer/LayerEngine.c` の流れ:

1. `SendKeyOverrideMacro`（2042 行付近）は、`ReleaseLeakedModifiers` で条件の修飾キーを OS 上で離したあと、`SendMacroSteps` でマクロを送る。この時点で `SuppressedMods` は 0 でない。
2. `SendMacroSteps` の各ステップは `SendDecodedEvent` を経て、`ForwardToClass` を通る。
3. `ForwardToClass`（1123 行付近）には、「置き換え中に、修飾キーでもなく置き換え先自身でもないキーの make が来たら、`RestoreSuppressedModifiers` で離していた修飾キーを先に戻す」という規則がある。
4. マクロの `a` はこの条件に当たる。そのため最初の make の直前に `RestoreSuppressedModifiers` が働き、Ctrl（`RealModsDown` に残っている）が戻る。

観測した「`Cu` と `Cd` が同刻、その直後に `a`」と一致する。

置き換え先が単純キーの Key Override では、この規則は「利用者が別のキーを押した」場合のために入っている。マクロの出力中は、その規則を止める必要がある（マクロの送出中は `SuppressOwnerScanCode`／`SuppressedMods` で判定しない、など）。修正方法の判断は開発側に任せる。

## 制限・未実施

- GUI（キー置換の編集画面のチェック、`KeymapTool --set-key-override … repeat` 以外の画面操作）は、今回の試験の対象にしていない。CLI の設定と、`--get-key-override` を使う確認だけ行った。
- 物理キーボードでの確認は未実施（OIB 注入のみ）。
- Win は、マクロの前に戻る経路だけでは検出できず、結果は参考値。修正後の再試験で Win+A・Win+B などの出力を使って確認するとよい。
- ARM64 は未確認。

## 後始末

- 簡易マップを消去（`HasLayerMap=0`、`Enabled=0`）。キー置換 1〜7、マクロ 7・8 を消去。機能は Macro・Combo・TapDance・KeyOverride すべて ON に戻した。
- マクロ 0（20 ステップ）、`tray.json`、`55F70000\helper.json` は変更していない（`tray.json` はバックアップとハッシュ一致）。
- 試験用の窓、KeymapTool は残っていない。トレイは稼働中。昇格ランナーは終了。
- ユーザーのレイヤーマップは、GUI で再適用が必要。
