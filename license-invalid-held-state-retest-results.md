# 無効化のあとに残る押下状態の修正の再試験結果（2026-10-28〜29）

対象: `license-invalid-held-state-retest-handoff.md`。`feature/license-invalid-track-held`（f0e394a）のドライバ。

## 結論

**修正は効いている。F-1 の原因だった (a)（修飾キー）と (b)（通常のキー）は、どちらも再現しなくなった。** 実施した範囲で、予測と外れた項目はなかった。回帰（第 1 部・第 2 部）も、FAIL はなかった。

| 修正前（`license-invalid-held-state-results.md`） | 修正後 |
|---|---|
| F-a: 有効に戻したあと、Caps がリマップされない。Shift を 1 回押して離すまで直らない | 有効に戻した最初の Caps から `38d 29d 29u 38u`。3 回とも |
| F-b: A を無効のあいだに離すと B が押されたまま | A を離した時点で B の break が出る。B は離れている。20 回繰り返しても毎回 |

## 環境

| 項目 | 値 |
|---|---|
| kbdlayer.sys | 52C00732F38E1783（インストール済み、22:03 の再起動後に読み込み。DriverStore と一致） |
| 版 | Subscription。`LicenseToken` は退避と一致、`License`=1、`LicenseLastState`=1 |
| Driver Verifier | `/standard`（kbdlayer.sys 対象、`load: 1 / unload: 0`） |
| 方法 | 前回と同じ。トークンの削除と `License`=0、書き戻しと `License`=1 を直接行い、`IOCTL_KBDLAYER_REVALIDATE_LICENSE`（DEFINITIVE）を送る。応答は、無効で 0、有効で 2（毎回確認）。`kasaneLicenseSvc` は試験中、止めた |
| 注入と判定 | OIB のスロット 0、`testsink`。注入の先頭が届かない現象のため、同期用の F14 が無い回はやり直した（最大 5 回）。今回も、この現象は出た |
| IME | 有効のまま。Caps の素通しの up が記録されないことがある（down で判定） |

試験の開始時、デバイスには、ユーザーのキー配置（109 キー）が入っていた。試験の設定（S1 など）で上書きしたため、終了時の配置は S1（Caps = `LALT(KC_ZKHK)`、キー置換 0 = Shift+半角/全角）になっている。

## 1. 同じ項目の再実行

| # | 修正後の予測 | 観測 | 結果 |
|---|---|---|---|
| F-a1 | 有効化の直後の Caps からリマップ | 3 回とも `38d 29d 29u 38u`（rep 3 は全体が取れた。rep 1、rep 2 は、先頭の `38d` が注入の欠落で記録されず、`29d 29u 38u` が見えた。リマップされていることは分かる） | **予測どおり** |
| F-a2 | Caps 5 回ともリマップ（`--off`／`--on` も不要） | 3 回とも、`38d 29d 29u 38u` が 5 回。途中の `--off`／`--on`（`Enabled=0`→`1`）のあとも変わらない | **予測どおり** |
| F-a3 | 修正前と同じ（リマップされる） | 3 回とも、Shift の `2ad 2au` のあと、Caps が `38d 29d 29u 38u` ×2 | **予測どおり** |
| F-a4（対照） | `38d 29d 29u 38u` | `2ad 2au 38d 29d 29u 38u` | 予測どおり |
| F-a5（対照） | `38d 29d 29u 38u` | `2ad 2au`（無効のあいだ）→ `38d 29d 29u 38u` ×2 | 予測どおり |
| F-b1 | A を離した時点で B の break が出る | `30d 30u`（A を離したとき `30u` が出る。A の素の `1eu` は出ない） | **予測どおり** |
| F-b2 | 有効化のあとも、B が離れている | `GetAsyncKeyState('B')` は、有効化の 3 秒後、6 秒後とも **False** | **予測どおり** |
| F-b3 | 修正前と同じ | A 1 回で `30d 30u`、B は False | 予測どおり |
| F-b4（対照） | 修正前と同じ | `30d 30u`、B は False | 予測どおり |
| F-c1 | Alt が残らない | Caps を押す（`38d 29d`）→ 無効化 → Caps を離す: 無効化の時点で `29u 38u` が出る。MODSNAPSHOT はすべて shift=0 ctrl=0 alt=0 win=0（有効化のあと、最後まで） | **予測どおり** |

「時間をおいたあと」の確認は、R-4 で行った（下の表）。

## 2. 追加の項目

| # | 観測 | 結果 |
|---|---|---|
| R-1 | Shift ↓ → 無効化 → 無効のあいだに Shift ↓↑ → Shift ↑ → 有効化 → Caps ×2: Shift の操作は素通し（`2ad 2ad 2au 2au`）、そのあと `38d 29d 29u 38u` ×2 | **PASS** |
| R-2 | 無効のまま Shift+Caps: `2ad 3ad 3au 2au`（素通し。リマップされない） | **PASS** |
| R-3 | 無効のまま A を 20 回: `1ed 1eu` が 20 組（make 20、break 20）。欠落なし。1 回目の試行は、注入の先頭の欠落で、make が 19 回に見えたため、開始を遅らせてやり直した | **PASS**（`ExtraInformation` は `testsink` が出せないので省略） |
| R-4 | 無効のまま 5 分 10 秒放置（`state=0` を確認）→ 有効化。S1 の Caps と A→B を、有効化の約 15 秒後、30 秒後、60 秒後に試した: いずれも `38d 29d 29u 38u 30d 30u` | **PASS** |
| R-5 | S5。A ↓ → 無効化 → A ↑ → 有効化 を 20 回: B の make 20、break 20（`30d 30u` ×20）。各有効化の 1.4 秒後の `GetAsyncKeyState('B')` は、20 回とも **False** | **PASS** |

## 3. 回帰

| 項目 | 結果 |
|---|---|
| L-5（無効化、`--list` から消える、`--set-features` は `Error: 1168`、Caps・Shift+Caps は素通し、`LicenseLastState`=0） | PASS |
| L-6a（Shift+Caps を押したまま無効化） | PASS（`2ad 3ad 3au 2au`、そのあと A・B が普通、押しっぱなしなし） |
| L-6b（Caps を押したまま無効化） | PASS（無効化の時点で `29u 38u` が出る。そのあと A・B が普通） |
| L-6c（MT 保持中に無効化） | PASS（`1dd 1du`、そのあとのキーは普通） |
| L-6d（有効化のあと、設定がそのまま使える） | PASS |
| L-7（24 回の Caps 中に有効化） | PASS（素通し 8 回のあと、リマップ 16 回。合計 24、欠落なし。素通しからリマップへの間隔は 0.64 秒で、有効化の直後の最初の押下も捨てられていない。試験スクリプトの自動判定は、素通しの並びの数え方が合わず FAIL と出たが、Caps の make を数え直して確認した） |
| L-8（無効のあいだ） | PASS（一部。`ExtraInformation` は未確認） |
| L-11（HID キーボード 391B0000 の再列挙） | PASS（一部。無効のあいだは `--list` に出ない。有効化の直後に、再接続なしで 2 台とも出る） |
| M-1、M-2、M-3、M-4、M-7、M-11、M-12 | PASS（第 1 部と同じ出力） |
| M-13a〜k、M-13m（HLP） | PASS（M-13m は、初回は注入が届かず判定できず、再実行で PASS: `HLP(1)` の down／up が 4 行、HLP のキーは何も出力しない） |
| M-13l（緊急解除） | PASS（出力の並びは第 1 部と同じ。`Enabled=0` になる。自動判定は、RShift の break の見え方で FAIL と出る。第 1 部と同じ受け側の事情） |
| M-14（耐久、Verifier `/standard`） | PASS（Shift+Caps、Caps 単独、A を各 60 回ずつ、2 段、合計 120 組。欠落・押しっぱなしなし。`pnputil /restart-device` は、前回と同じく「再起動が必要」と返り、実際には再起動されなかった） |

試験の前後で、Verifier の違反なし。新しいミニダンプなし。

## 後始末

- ライセンスは元に戻した（`License`=1、`LicenseLastState`=1、状態 2、トークンは退避と一致）。`kasaneLicenseSvc` は Running。
- 設定は S1。
- 昇格ランナーを止め、トークンの控えとヘルパーを削除した。
