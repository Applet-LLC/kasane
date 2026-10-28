# 修飾付きキーへのキー置換の試験結果・第 1 部（2026-10-28）

対象: `override-modded-key-handoff.md` の第 1 部（M-1〜M-14）。第 2 部（L-1〜L-14、ライセンス検証の切り離し）は、この文書の範囲外（末尾を参照）。

## 結論

**FAIL は 0 件。** 実施できた項目はすべて期待どおりだった。ただし次の 3 点は、依頼文の文字どおりにはならなかった、または実施できなかった。

1. **M-2**: 置き換え先が `LSFT(KC_CAPSLOCK)` のとき、Shift は一度も離されない（依頼文は「Shift を離してから出す」）。出力は「Shift ↓ → Caps ↓↑ → Shift ↑」で、Alt は出ない。押しっぱなしも残らない。`ARCHITECTURE.md` の「置き換え先が修飾を含むときは、置き換え先が使う修飾を抑止の対象から外す」と一致するので、仕様どおりと判断した。
2. **M-14 の `pnputil /restart-device`**: 「構成操作を完了するには、システムを再起動する必要があります」と返り、デバイスは実際には再起動されなかった（内蔵キーボードのスタックは止められない）。画面ロック・解除は PIN の入力が要るため実施していない。USB の抜き差しは、内蔵キーボードなので対象外。
3. **M-1 のメモ帳のメニューバー確認**と、**M-13 の「打鍵の遅延が体感で変わらない」**は、実施していない（後者は人の体感が必要。注入したキー列では、打鍵から出力までの順序と間隔に乱れはなかった）。

## 環境

| 項目 | 値 |
|---|---|
| kbdlayer.sys | D1E846B8A64D30F7（ディスク上・DriverStore・読み込み中の 3 つが一致。10-28 10:58 のビルド） |
| 版 | Subscription（`LicenseLastState=1`）。試験中は変化なし |
| Driver Verifier | `/standard`（フラグ 0x001209bb）。`kbdlayer.sys` 対象。特別プール割り当て 644 件がすべて成功、失敗 0 |
| Windows | 管理者保護が有効（昇格すると `t480s\admin_applet` で動く） |
| 対象キーボード | 内蔵 ACPI\LEN0071（UniqId 55F70000）。OIB のスロット 0 で注入 |
| 常駐ソフト | nodoka、PowerToys は動いていないことを、各グループの前に確認 |
| 判定の方法 | `testsink.exe` が受け取った WM_KEYDOWN／UP のスキャンコード列と、末尾の MODSNAPSHOT（全修飾キーが離れていること） |

## 試験用の設定

| 設定 | 内容 |
|---|---|
| S1 | Caps（0x3A）= `LALT(KC_ZKHK)`、キー置換 0 = `MOD_LSFT KC_ZKHK "LSFT(KC_CAPSLOCK)"` |
| S2 | キー置換 0 = `MOD_LSFT KC_ZKHK "MACRO(0)"`、マクロ 0 = `KC_A KC_B` |
| S3 | Caps = `LCTL(KC_C)`、キー置換 0 = `MOD_LSFT KC_C KC_D` |
| S4 | キー置換 0 = `MOD_LSFT KC_2 KC_LBRC`、1 = `MOD_LSFT KC_BSPACE KC_DELETE`、2 = `MOD_LCTL|MOD_LSFT KC_Z LCTL(KC_Y)` |

試験の終了時は、S1 に戻した（元の状態が S1 だった）。マクロ・コンボ・タップダンスは、M-13 で使った分を消した。`--set-features` は 1 1 1 1。

## 結果

表のスキャンコードは、`d` が make、`u` が break。`2a` = LShift、`3a` = Caps、`29` = 半角/全角、`38` = LAlt、`1e` = A。

### 第 1 部

| 項目 | 内容 | 観測した順序 | 結果 |
|---|---|---|---|
| M-1 | Caps 単独 | `38d 29d 29u 38u` | PASS（Shift は出ない） |
| M-2 | Shift ↓、Caps ↓↑、Shift ↑ | `2ad 3ad 3au 2au` | PASS（上の 1. を参照。Alt なし、押しっぱなしなし） |
| M-3 | Caps を離す前に Shift を離す | `2ad 3ad 2au 3au` | PASS |
| M-4 | 置き換え中に A を押す | `2ad 3ad 1ed 1eu 3au 2au`（A の make 時に Shift が有効） | PASS |
| M-5 | S2: 置き換え先が MACRO(0) | `2ad 2au 1ed 1eu 30d 30u 2ad 2au`（a b が 1 回。マクロのキーに Alt・Ctrl・Shift なし。Caps の up は出ない） | PASS |
| M-6 | キー置換の機能を OFF（`--set-features … 0`） | Shift+Alt+半角/全角。修飾キーの順序は `2ad 38d … 38u 2au`。後述の注記あり | PASS（注記付き） |
| M-7 | Shift+Caps を押したまま `--off` | `2ad 3ad 3au 2au` の後に A が普通に出る。押しっぱなしなし | PASS |
| M-8 | S3: Shift+Caps | `2ad 2au 20d 20u 2ad 2au`（D だけ。Ctrl なし） | PASS |
| M-8b | S3: Caps 単独 | `1dd 2ed 2eu 1du`（Ctrl+C） | PASS |
| M-9 | LM・OSM が、キー置換の有無で同じ出力 | LM は `2ad 1ed 1eu 2au`、OSM は `1dd 1ed 1du 1eu 1ed 1eu`（有りと無しで同一） | PASS |
| M-11 | Shift を押したまま Caps を 25 回リピート | `2ad` の後に `3ad` が 25 回、`3au 2au` | PASS |
| M-12 | Shift + 実際の 0x29（Caps ではない） | `2ad 3ad 3au 2au`（同じ置き換えが効く） | PASS |

M-6 の注記: ZKHK の make と break が `29u 29d` の順で届いた。Windows が 半角/全角 を VK 0xF3（up）と VK 0xF4（down）に変換する際に、Shift と Alt が重なると順序が入れ替わるため。sink のログでは `vk=0xf3 sc=0x29 up` の直後に `vk=0xf4 sc=0x29 down`。Alt ＋ ZKHK だけ（M-1）では入れ替わらない。ドライバの出力ではなく、受け側（Windows）の表示の問題と判断した。

### M-10（既存の A・B 系の回帰）

S4 で実施。**12 項目すべて PASS**。

| 項目 | 内容 | 観測した順序 | 結果 |
|---|---|---|---|
| A-1 | LShift+2 → `@` | `2ad 2au 1ad 1au 2ad 2au` | PASS |
| A-2 | RShift+2 → `@` | `36d 36u 1ad 1au 36d 36u` | PASS |
| A-3 | 2 単独は 2 のまま | `03d 03u` | PASS |
| A-4 | Shift+BS → Delete | `2ad 2au 53d 53u 2ad 2au` | PASS |
| A-5 | Ctrl+Shift+Z → Ctrl+Y | `1dd 2ad 2au 15d 15u 2ad 2au 1du` | PASS |
| A-5c | Ctrl+Z は変わらない | `1dd 2cd 2cu 1du` | PASS |
| A-6 | Ctrl+Shift+2 → Ctrl+`@` | `1dd 2ad 2au 1ad 1au 2ad 2au 1du` | PASS |
| B-1 | 2 を押したまま Shift を離す | `2ad 2au 1ad 1au`（Shift の break は 1 回、2 を離した後の余分な make なし） | PASS |
| B-4 | Shift、2、BS、2 を離す、BS を離す、Shift を離す | `2ad 2au 1ad 53d 1au 53u 2ad 2au` | PASS |
| B-4b | 2 より先に BS を離す | `2ad 2au 1ad 53d 53u 2ad 1au 2au` | PASS |
| B-4c | BS より先に Shift を離す | `2ad 2au 1ad 0ed 0eu 1au` | PASS |
| B-5 | `LSFT(KC_1)` のキーを押したまま 2 | `2ad 02d 2au 1ad 1au 02u`（最後に押しっぱなしなし） | PASS |

A-1〜A-6 と B-1、B-5 は、旧結果（`.plans/key-override.md`）の説明から注入スクリプトを組み直した（B-1 と B-5 は、旧スクリプトが残っていないため、説明からの再構成）。

### M-13（既存のマクロ・コンボ・タップダンス・MT・LT と S1 の併用）

| 項目 | 内容 | 観測した順序 | 結果 |
|---|---|---|---|
| a | A = `MT(MOD_LCTL,KC_A)` のタップ | `1ed 1eu` | PASS |
| b | A を 400 ms 押し続ける | `1dd 1du`（Ctrl。A は出ない） | PASS |
| c | S = `LT(1,KC_S)` のタップ | `1fd 1fu` | PASS |
| d | D = `TAPDANCE(0)` の 1 タップ | `20d 20u` | PASS |
| e | D の 2 タップ | `12d 12u`（E） | PASS |
| f | F = `MACRO(1)`（X Y） | `2dd 2du 15d 15u` | PASS |
| g | コンボ J+K → L | `26d 26u` | PASS |
| h | Caps（S1） | `38d 29d 29u 38u` | PASS |
| i | Shift+Caps（S1） | `2ad 3ad 3au 2au` | PASS |
| j | Shift+B の後に c d | `2ad 30d 30u 2au 2ed 2eu 20d 20u` | PASS |
| k | C のキーリピート 21 回の直後に Shift+Caps | リピートの後も `2ad 3ad 3au 2au` | PASS |
| l | 緊急解除（LCtrl+RCtrl+LShift+RShift を 3.5 秒） | 離した後、A が普通に出る。MODSNAPSHOT は全修飾が離れている。`--list` が `Enabled=0` | PASS（注記付き） |
| m | `--watch-helper`（`HLP(1)` のキー） | `HLP(1) down／up` が 4 行（2 回の押下）。HLP のキーは何も出力しない | PASS |

l の注記: 受け側では、LShift と RShift を重ねた後の break が `2au` 1 回にまとまって見える（Windows が、片方の Shift を離しただけでは VK_SHIFT の break を送らないため）。MODSNAPSHOT で押しっぱなしがないことは確認した。

### M-14（耐久）

Verifier `/standard` の下で、Shift+Caps、Caps 単独、A を 1 組として、60 回を 2 段（合計 120 回ずつ）注入した。間隔は 40〜80 ms。

| 段 | Shift+Caps | Caps 単独 | A | 押しっぱなし | 結果 |
|---|---|---|---|---|---|
| 1 | 60 | 60 | 60 | なし | PASS |
| 2（`pnputil /restart-device` の後） | 60 | 60 | 60 | なし | PASS |

- 出力の順序は、全 120 組で M-1・M-2 と同じ。欠けも余分もなし。
- 試験の前後で、Verifier の違反なし。ミニダンプなし。System ログに kbdlayer・verifier・bugcheck の異常なし。
- `LicenseLastState` は試験の前後で 1。
- `pnputil /restart-device` は、上の 2. のとおり、実際の再起動にはならなかった。

## 注入まわりの注意（今後の試験のために）

注入したキーの先頭の数秒が、`testsink` に届かないことがあった（再現は不定。ログの先頭 1.8〜15 秒のあいだ、注入した最初のイベントが欠ける。最後の break だけ届く例もあった）。そのため、今回の試験では、注入の先頭に捨てのマーカー（F13）を 16 組と同期用の F14 を置き、F14 が届かなければ最大 5 回やり直した。上の表は、F14 が届いた回のものだけ。やり直しの回数は、グループごとに 0〜5 回だった。ドライバの動作とは関係なく、注入側または受け側（前面ウィンドウ）の問題と見ている。

## 後始末

- 設定を S1 に戻した。コンボ・タップダンス・マクロの試験用定義は消した。
- 昇格ランナーは、第 2 部の相談が済むまで動かしたまま。
- 注入スクリプトとログは、scratchpad（`mruns\log-*.txt`、`*.sink.log`）に残してある（コミットしない）。

## 第 2 部（L-1〜L-14）

まだ行っていない。再起動、`LicenseToken` の書き換え、`kasaneLicenseSvc` の停止、DebugView の設定、インストーラーの試験（L-12）、20 回以上の再起動（L-14）が絡むため、範囲と進め方を決めてから実施する。
