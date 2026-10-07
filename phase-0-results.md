# P0 試験結果: ヘルパーイベント（CUSTOM / `HLP(id)`）

- 対象: 作業ブランチ `claude/tender-euler-xxfl7a`、コミット `f57644d`（ユーザーがビルド・インストール・再起動・アクティベート済み）
- 試験日: 2026-10-08（試験担当）
- 結論: **H1〜H17 すべて PASS。ドライバ・KeymapTool の不具合は見つからなかった。** FAIL に見えた箇所は、すべて試験側の誤り（下記「試験側の誤りと対応」）で、直して再実行した結果を載せている。

## 環境

| 項目 | 内容 |
|---|---|
| ドライバ | インストール済みの `kbdlayer.sys`（DriverStore の `kbdlayer.inf_amd64_5dae423e2a01c2be`、署名あり、2026-10-08 02:51）。ビルド成果物（署名前、02:42）とは、署名のぶんファイルの長さが違う（92,616 → 103,920 バイト）ので、ハッシュは一致しない。同一性は、機能（`WAIT_HELPER_EVENT` が動く）と時刻で確認した |
| Driver Verifier | 標準（フラグ 0x001209bb）。`kbdlayer.sys` が対象で、再起動後から有効（`verifier /query`: `kbdlayer.sys (load: 1 / unload: 0)`）。**H1〜H17 はすべて Verifier の下で実行した** |
| 注入 | OIB（`DeviceNameBase=oib`）。slot 0 = 内蔵 ACPI（UniqId `55F70000`）、slot 1 = USB 04FE（`CDEC0000`、H16 用） |
| 常駐ソフト | nodoka / PowerToys Keyboard Manager / AutoHotkey は動いていない（各試験の前に確認）。`kasane-tray` は動いていたが、P0 の機能は使わない |
| 手法 | dev-notes `input-injection-testing`（T-7〜T-9、T-12、T-13、T-14、T-15、T-17、T-23）。キャリブレーションは注入（`--calibrate-simple … --suppress`）。`--watch-helper` は、非昇格・Hidden・stdout リダイレクト。区切りは F13 |

## 試験項目

| # | 結果 | 内容（実測） |
|---|---|---|
| H1 | PASS | キー1をタップ → `HLP(1) down` / `HLP(1) up`（Seq 連続、`Mods=0x00`）。testsink は空 |
| H2 | PASS | キー2 → `HLP(258) down` / `up` |
| H3 | PASS | 30ms 間隔の make 9 回 → `down` 1 + `down (repeat)` 9 + `up` 1（TimeMs の間隔は約 31ms）。testsink は空 |
| H4 | PASS | MO(1)↓ キー1↓↑ MO↑ → `HLP(3) down` / `up` |
| H5 | PASS | MO(1)↓ キー4↓ MO↑ キー4↑（離す順序を逆に）→ `HLP(4) down` / `up`。testsink に `x` なし（空） |
| H6 | PASS | LShift↓ キー1 タップ LShift↑ → 両方 `Mods=0x02`。testsink は Shift の down / up だけ（`10d 10u`） |
| H7 | PASS | キー1↓ → `--off`（非昇格）→ キー1↑: `HLP(1) down` と、`--off` の後に `HLP(1) up`。testsink は空。`--on` で戻した |
| H8 | PASS | `--watch-helper` 2 つ: 同じ 2 行（Seq・TimeMs まで一致） |
| H9 | PASS | 片方を kill → 45ms でプロセスが消えた。残った方は、続けて表示 |
| H10 | PASS | 起動前のキー1の行は出ず、起動後のキー2（`HLP(258)`）だけ |
| H11 | PASS | `NtSuspendProcess` で止めて 40 回タップ（80 イベント）→ 再開後: 1 行（Seq=40）→ `(15 event(s) were dropped: the queue overflowed)` → 64 行（最後は Seq=119）。BSOD なし |
| H12 | PASS | 17 個起動 → 16 個が待ち続け、17 個目は `IOCTL_KBDLAYER_WAIT_HELPER_EVENT failed. Error: 1450`（exit=1）。16 個を止めた後、新しく起動したものは動いた |
| H13 | PASS | 非昇格で `--watch-helper` が動く。非昇格・`GENERIC_READ` で `\\.\KbdLayerControl` を開けた。0x81E のアクセス値 0 版（IOCTL コード 0x80202078。READ 版は 0x80206078）を送ると `ERROR_INVALID_FUNCTION`（1） |
| H14 | PASS | `--set-combo 0 KC_A KC_S KC_ESC` を設定し、キー1（HLP(1)）を単独でタップ → testsink に `a` が出ない、`HLP(1)` のイベントも出ない（仕様どおり）。終わりに `--set-combo 0`（引数なし）でクリアし、`--get-combo` が `(not set)` になることを確認。**参考（INFO）**: キー1とキー2（物理 A と S）を同時に押すと、コンボが成立して Esc（`1bd 1bu`）が出た。HLP のイベントは出ない。コンボのメンバーは、スキャンコードで照合されるため、HLP のキーもメンバーになれて、コンボが優先される |
| H15 | PASS | `--set-combo <ID> 0 'HLP(1)' KC_S KC_ESC` → `--set-combo: "HLP(1)" is not a plain key`（exit=1）。`--set-tapdance <ID> 0 'HLP(1)' KC_A KC_B KC_C` → `--set-tapdance: SingleTap "HLP(1)" must be a key, a modified key (LCTL(kc)), MACRO(n), or KC_NO`（exit=1）。拒否の後、コンボ 0 は `(not set)` のまま |
| H16 | PASS | Verifier 標準が有効。USB 04FE（`CDEC0000`、`HID\VID_04FE&PID_0016&MI_00\D&70295DF&0&0000`）にヘルパーのキーマップを適用し、`--watch-helper` を待たせたまま、`pnputil /restart-device` を 1 台ずつ 10 回（各回の後 3.5 秒待って、キー1をタップ）。**10 回とも**、`--watch-helper` は待ち続け（終了しない）、再接続の後のキー1が `UniqId=CDEC0000` で表示された（`HLP(1) down` / `up`）。BSOD なし。`pnputil` の出力は「デバイスが正常に再起動されました」（別途確認） |
| H17 | PASS | 既存機能の回帰（`test-keymap-simple.json`、`-modtap.json`、`-combo.json`、`-tapdance.json`、既定の設定: TapTerm 200 / ComboTerm 50 / TapDanceTerm 200）。**レイヤー 7/7**: 素の A、MO(1)、MO を先に離す（キーが残らない）、TG の ON/OFF、OSL（1 回だけ）。**MT/LT 5/5**: MT タップ → c、300ms 保持 → Ctrl のみ、MT + 割り込み → Ctrl+a、LT タップ → d、LT 保持 + 割り込み → 1。**コンボ 4/4**: A+B（20ms）→ Esc、B→A の逆順 → Esc、A 単独 → a、窓の外（150ms）→ a b。**タップダンス 3/3**: 単独 → Esc、2 回 → Tab、割り込み → Esc, b |

## 「静的にしか確認していない点」への答え

| 点 | 結果 |
|---|---|
| コンパイル | 私は、ビルドしていない（ユーザーがビルド済み）。コンパイル警告・エラーの報告は、ユーザーから無い |
| `--watch-helper` を Ctrl+C で止めたとき、プロセスがすぐ終わるか | 1 回: 56ms で終了（終了コード `STATUS_CONTROL_C_EXIT`）。`KeymapTool` は、Ctrl+C のハンドラを持たず、OS が終了させる。ドライバから見ると、プロセスの終了でハンドルが閉じる経路で、kill（H9、45ms）と同じ。試験スクリプトの自分自身も Ctrl+C を受けて止まったので、2 回目以降は実行していない。保留 IRP が残っていないことは、H9・H12（kill の後に 16 個すべて待てる）で確認済み |
| オートリピートの make が `(repeat)` になるか | PASS（H3: 9 回すべて） |
| `Mods` が、押下・解放それぞれのときの `RealModsDown` か | PASS（H6: 押下・解放とも 0x02。ほかは 0x00） |
| `TimeMs` | 起動からのミリ秒（約 6,260,000ms = 約 104 分 = 起動からの経過時間と一致）。イベント間隔は、実際の入力間隔と一致 |

## 試験側の誤りと対応（ドライバの不具合ではない）

1. H17 の最初の実行で 14 件 FAIL: 私の試験スクリプトで、関数名 `Sc` が PowerShell の別名 `sc`（`sc.exe`）と衝突し、注入のスクリプトが壊れた。関数名を変えて再実行し、レイヤー・MT/LT は全件 PASS。
2. H17 のコンボが FAIL: コンボはスキャンコード（KC_A = 0x1E、KC_B = 0x30）で定義される（サンプルの README にも書いてある）のに、私のプロファイルが 2 番目のキーを 0x1F にしていた。0x30 にして PASS。
3. H17 のタップダンスが FAIL: 直前のコンボ（A+B → Esc）が残っており、A のキーがコンボの片方として扱われ、`a` に戻っていた。コンボをクリアして PASS（単独で実行すると、最初から PASS）。
4. 試験用の補助関数で、変数名 `$r` が出力フォルダ `$R` を隠し、監視プロセスの出力を読めなかった（大文字小文字の衝突）。直した。

## 所見・提案（任意）

- ドライバ・KeymapTool: 指摘なし。
- ドキュメント: `samples/README.md` のヘルパーの節に、「別のコンソールで（昇格は不要）」とあるとおりで、試験でも非昇格で動いた。コンボと併用するときの注意（コンボのメンバーはスキャンコードで照合される。HLP のキーも、コンボのメンバーになれて、コンボが優先される）を書き足すとよいかもしれない（H14 の INFO）。
- P0 の範囲で、新しい種類の問題は見つかっていない。dev-notes の「ドライバ別の状況」表は、開発担当の判断で更新してよい。

## 後始末の状態

- 試験に使った 2 台（`55F70000`、`CDEC0000`）: `--off`、`--clear`、コンボ・タップダンスを削除。全キーボードが `HasLayerMap=0 Enabled=0`。
- 緊急解除の組み合わせ: 既定（`0x001D,0xE01D,0x002A,0x0036`、3000ms）。
- `--watch-helper`・管理者のヘルパーなどの試験用プロセス: すべて終了。
- リポジトリの作業ツリー: 変更なし。
