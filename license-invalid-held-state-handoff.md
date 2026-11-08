# 申し送り: ライセンス無効化のあとに残る押下状態（F-1 の原因の特定）

- 対象: **いま入っているドライバのまま**（`main` の `14a8864`、kbdlayer.sys D1E846B8A64D30F7）。ドライバの変更も、再ビルドも、入れ替えも要らない。試験の環境は、`license-decouple-results.md` の第 2 部と同じ（Driver Verifier `/standard`、内蔵 ACPI\LEN0071 の UniqId 55F70000、OIB のスロット 0 で注入、判定は `testsink`）。
- 書いた日: 2026-10-28（開発担当）。元: `license-decouple-results.md` の F-1。
- 依頼の目的: F-1（無効化→有効化のあとに、リマップが効かない状態が現れる）の原因を、**2 つの仮説の再現で確定する**。修正は、結果を見てから開発側で行う。

## 仮説

ライセンスが無効のあいだ、`ServiceCallback`（`driver/kbdlayer/Driver.c`）は、キー入力をレイヤーエンジンに通さず、そのまま素通しにする。その間に届く**物理キーの解放**は、エンジンの追跡に反映されない。
ところが、有効から無効に変わる時点（`AbortLiveStateForDevice`、`LayerEngine.c`）で消すのは、MT・コンボ・タップダンス・レイヤー・NICOLA の保留状態だけで、次の記録は消さない。

| 残る記録 | どこにあるか |
|---|---|
| 物理で押されている修飾キー | `RealModsDown`（`ForwardToClass` が、修飾キーの make/break を通すときだけ更新する） |
| 押下時に決めた処理の記録 | `HeldKeys[]`（そのキーの break をエンジンが処理したときだけ消える） |
| 修飾付きキーが足した修飾キーの数 | `AddedModCount[]` |

これらが、有効に戻った後も残って、次の 2 つが起きる、というのが仮説。

- **仮説 (a)**: 無効のあいだに離した修飾キー（Shift）が、`RealModsDown` に残る。有効に戻ると、Shift を押していないのに、Shift が押されているものとして扱われる。S1 のキー置換（条件: Shift + 半角/全角）が、Caps 単独の押下で発動し、Caps がリマップ（Alt+半角/全角）にならない。Shift を 1 回押して離すと、`RealModsDown` が更新されて直る。
- **仮説 (b)**: 通常のキーのリマップ（A→B）で、A を押したまま無効になると、B の make は OS に送られている。無効のあいだに A を離すと、A の素の break だけが OS に届き、B の break は出ない。B が、OS で押されたままになる。

## 準備

1. 第 2 部の試験と同じ方法で、ライセンスの有効・無効を切り替える。`kasaneLicenseSvc` を止める。
   - 無効にする: `LicenseToken` を削除して `License`=0 にし、`IOCTL_KBDLAYER_REVALIDATE_LICENSE`（DEFINITIVE）を送る。
   - 有効に戻す: 退避したトークンを書き戻して `License`=1 にし、同じ IOCTL（DEFINITIVE）を送る。
   - IOCTL の応答の状態が、無効で 0、有効で 2 であることを、毎回確認する。
2. 試験の前に、`LicenseToken` と `Parameters` を退避する。終了後に、元の値、サービスの Running、設定 S1 に戻す。
3. 注入の先頭が `testsink` に届かない問題の対策は、第 1 部・第 2 部と同じ（捨てのマーカー F13、同期用の F14、最大 5 回のやり直し）。
4. 設定は、次の表のとおり、項目ごとに入れ替える。

| 記号 | 設定 | 使う項目 |
|---|---|---|
| S1 | 第 1 部と同じ。Caps（0x3A）= `LALT(KC_ZKHK)`、キー置換 0 = `MOD_LSFT KC_ZKHK "LSFT(KC_CAPSLOCK)"` | F-a1〜F-a5、F-c1 |
| S5 | A（0x1E）= `KC_B`（通常のキーのリマップ。MT・修飾付きキーなし）。キー置換は空 | F-b1〜F-b4 |

## 試験項目

注入のスキャンコード: 左 Shift = `0x2A`、Caps = `0x3A`、A = `0x1E`、B = `0x30`、左 Alt = `0x38`、半角/全角 = `0x29`。
`testsink` の表記は、結果ファイルと同じ（`d` が make、`u` が break）。日本語 IME が有効だと、Caps の `up` が記録されない（`vk=0xe5`）ので、判定は down で行う。**IME は、可能なら無効にして試験する**。

| # | 設定 | 操作 | 予測（仮説どおりなら） | 仮説が外れたとき |
|---|---|---|---|---|
| F-a1 | S1、有効 | Shift ↓（`2ad`）→ **無効化** → Shift ↑（`2au`、素通し）→ **有効化** → Caps ↓↑ を 1 回 | リマップされない。出力は `2ad 3ad …`（Shift が付いた Caps）か、Caps だけで、`38d 29d 29u 38u` にならない | `38d 29d 29u 38u` が出る |
| F-a2 | F-a1 の続き | Caps ↓↑ をさらに 5 回。途中で `--off`、`--on` も 1 回ずつ実行 | 5 回とも、リマップされない。`--off`/`--on` でも直らない | 途中で直る |
| F-a3 | F-a2 の続き | Shift ↓↑ を **1 回**（`2ad 2au`）。そのあと、Caps ↓↑ | Caps が `38d 29d 29u 38u` になる（直る） | 直らない |
| F-a4（対照） | S1、有効 | Shift ↓ → **無効化しない** → Shift ↑ → Caps ↓↑ | `38d 29d 29u 38u`（通常どおり） | リマップされない |
| F-a5（対照） | S1、有効 | **無効化** → Shift ↓↑ を 1 回（無効のあいだに完結する操作）→ **有効化** → Caps ↓↑ | `38d 29d 29u 38u`（残らない） | リマップされない |
| F-b1 | S5、有効 | A ↓（`1ed`。OS には B の make `30d` が出る）→ **無効化** → A ↑（`1eu`、素通し）→ **有効化** | A ↑ のあと、B の break（`30u`）が出ていない。有効に戻したあとも出ない | A ↑ のときに `30u` が出る |
| F-b2 | F-b1 の続き | キー入力を何も送らず、5 秒待つ。そのあと、B の状態を取る（`GetAsyncKeyState('B')` の最上位ビット、または `testsink` の押下状態） | B が押された状態のまま | 離れている |
| F-b3 | F-b2 の続き | A ↓↑ を 1 回（有効のまま） | 記録が消えて、B が離れる（`30d … 30u`） | 離れない |
| F-b4（対照） | S5、有効 | A ↓ → **無効化しない** → A ↑ | `30d 30u`（B の break が出る） | B が離れない |
| F-c1 | S1、有効 | Caps ↓（`38d 29d` が出る）→ **無効化** → Caps ↑（素通し）→ **有効化** | 左 Alt が OS で押されたまま残る可能性。`testsink` の MODSNAPSHOT で、Alt が押されているかを見る | Alt が離れている |

F-c1 は、(a)(b) と同じ原因の別の現れかを見る項目。結果が (a)(b) と異なっても、そのまま書く。

押しっぱなしの判定は、修飾キーは `testsink` の MODSNAPSHOT、通常のキー（B）は F-b2 の方法で行う。試験の最後に、押されたまま残ったキーを、注入で押して離して解除する。

## 取ってほしいもの

1. 各項目について、**予測と一致したか、外れたか**を、観測した出力の順序と一緒に書く（外れたこと自体が結果）。
2. F-a1〜F-a3 は、**同じ手順を 3 回**繰り返して、毎回同じ結果かを書く。
3. F-a1 でリマップされないときの出力が、素通しの Caps（`3ad` だけ）か、Shift が付いた Caps（`2ad 3ad` を含む）かを書く。(a) の仮説（Shift が残ってキー置換が発動する）を、区別する材料になる。
4. 結果は `license-invalid-held-state-results.md` に書く。

## 注意

- 試験のあいだ、`kasaneLicenseSvc` は止めたまま。トークンの書き込みは、試験側が直接行う。Gumroad には問い合わせない。
- 無効化の IOCTL は DEFINITIVE を使う。SOFT では、失敗しても状態が変わらないので、無効にならない。
- 昇格ランナーの寿命は 4 時間。長引くときは、途中で切れる。
- DebugView は使わない（`IHVDRIVER` の設定が無いため）。ログは、`testsink` のログと IOCTL の応答の状態だけでよい。
- ドライバ・設定・サービスを元に戻す。`LicenseToken` の控えは、終了後に削除する。

## 開発側の補足（試験の判断には使わない）

仮説の根拠となるソースの箇所。
- 無効のあいだの素通し: `Driver.c` の `ServiceCallback`（`licensed = DeviceId != 0 && LicenseIsEffective()`。偽のときは、`ResolveAndForward` を呼ばず `OriginalClassService` へ渡す）。
- 無効になるときの中止処理: `LayerEngine.c` の `AbortLiveStateForDevice`、`AbortLiveStateLocked`（`HeldKeys`、`RealModsDown`、`AddedModCount` は消さない）。
- OFF のとき（`--off`）は、`ServiceCallback` がエンジンを通し続け、`ResolveLayerAction` の「記録のあるキーの解放だけ処理する」経路（`LayerEngine.c` の 833 行目付近）が、解放を記録どおりに処理する。無効のときは、この経路を通らない。
- `RealModsDown` の更新: `ForwardToClass`（`LayerEngine.c` の 1135 行目付近）。キー置換の条件の判定: `ResolveModdedKey`（同 2279 行目付近）。
