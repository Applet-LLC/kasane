# Ctrl+K（行カットのマクロ）のあと、Ctrl を押したままの操作が効かない件の再現結果（2026-11-03）

対象: 顧客からの要望「Ctrl+K で行カットはできるが、そのあと Ctrl を押したままの操作ができない。Ctrl を一度離して押し直すと操作できる」。申し送りの `key-override-v2-handoff.md` の試験ではなく、**今のドライバ**で確かめた別件。

## 結論

1. **再現した。仕様ではなく、ドライバの不具合と読める。** キー置換の置き換え先が、Ctrl を含むマクロ（`LSFT(KC_END)`、`LCTL(KC_X)`）のとき、マクロのあと、物理の Ctrl は押したままなのに、OS には Ctrl が戻らない。続けて押したキーは、Ctrl なしで届く。
2. **続けてもう一度 Ctrl+K を押しても、置き換えが発動しない。** 普通の `k` が届く。ドライバ側も、Ctrl が押されていないと見ている。顧客の「一度離して押し直すと効く」と一致する。
3. **条件の修飾キーと同じ修飾キーをマクロの中で使うと起きる。** Ctrl の条件のマクロの中の Ctrl、Alt の条件のマクロの中の Alt。マクロの中に Ctrl がなければ、Ctrl は戻る。
4. **今の設定でできる回避策がある**: 行カットを `LSFT(KC_DELETE)`（Shift+Delete）にする。マクロは `LSFT(KC_END)`、`LSFT(KC_DELETE)`。OS に届くキーの並びで確かめた（アプリでの切り取りの動作は未確認）。

## 環境

| 項目 | 値 |
|---|---|
| ドライバ | インストール済みの `kbdlayer.sys`（DriverStore、2026-11-03 02:25）。**`feature/release-1.01` の先頭（`6ba10e5`）のビルドで、キー置換は 32 件の旧版**（Id 127 の読み出しがエラー 87） |
| トレイ・KeymapTool | 同じビルド（`1.1.0+6ba10e58…`） |
| キーボード | ACPI\LEN0071（`55F70000`）。ユーザーのレイヤーマップとキー置換（Id 0、1）はそのまま |
| 方法 | 昇格ランナーで、空いている Id 26〜31 とマクロ 122〜126 だけを設定し、OIB で注入。受け口（testsink）が最前面で受けた、OS に届くキーの並びを記録した。終了時に、設定した分をすべて消した（残りは Id 0、1 のみ。`tray.json` のハッシュも一致） |

以下、キーの並びの読み方: `1dd` は Ctrl（スキャンコード 1D）の押下、`1du` は解放。`2a` は Shift、`4f` は End、`2d` は X、`2c` は Z。

## 結果

設定: Ctrl+K → `MACRO(126)` = [`LSFT(KC_END)`, `LCTL(KC_X)`]。Ctrl は注入で、押したままにする。

| No | 操作 | OS に届いた並び | 判定 |
|---|---|---|---|
| C1 | Ctrl 押下のまま K、続けて Z | `1dd 1du 2ad 4fd 4fu 2au 1dd 2dd 2du 1du` **`2cd 2cu`** `1du` | **Z に Ctrl が付かない**（マクロの `1du` のあと、`1dd` が無い） |
| C2 | 同じ、K のあと F（Ctrl+F → `KC_RIGHT` の置き換え） | `… 1dd 2dd 2du 1du 21d 21u 1du` | F の置き換えも発動しない。普通の `f`（`21d 21u`）が届く |
| C6 | 同じ、K を 2 回、続けて Z | `… 1du` **`25d 25u 2cd 2cu`** `1du` | **2 回目の Ctrl+K も置き換わらず、普通の `k`**（`25d 25u`） |
| C7 | K の 30 ms 後に Z | C1 と同じ | 間隔には関係しない |
| C3 | （対照）K、Ctrl を離して押し直し、Z | `… 1du 1du 1dd 2cd 2cu 1du` | 押し直せば、Z に Ctrl が付く（顧客の言うとおり） |

対照（マクロに Ctrl を含めない、またはマクロではない）:

| No | 設定と操作 | OS に届いた並び | 判定 |
|---|---|---|---|
| C4 | Ctrl+L → [`LSFT(KC_END)`, `KC_DELETE`]。L のあと Z | `1dd 1du 2ad 4fd 4fu 2au 53d 53u` **`1dd`** `2cd 2cu 1du` | Ctrl が戻る。Z は Ctrl 付き |
| W1 | Ctrl+J → [`LSFT(KC_END)`, `LSFT(KC_DELETE)`]（回避策）。J のあと Z | `1dd 1du 2ad 4fd 4fu 2au 2ad 53d 53u 2au` **`1dd`** `2cd 2cu 1du` | Ctrl が戻る |
| W2 | 同じ、J を 2 回、続けて Z | 2 回目も、`2ad 4fd 4fu 2au 2ad 53d 53u 2au` が出て、最後の Z に Ctrl が付く | 連続して使える |
| C5 | Ctrl+F → `KC_RIGHT`（マクロなし）。F のあと Z | `1dd 1du 4dd 4du 1dd 2cd 2cu 1du` | Ctrl が戻る |
| Z1 | Ctrl+L → `LCTL(KC_X)`（単独の置き換え。マクロなし）。L のあと Z | `1dd 2dd 2du 2cd 2cu 1du` | Z は Ctrl 付き |
| Z2 | 同じ、L を 2 回、続けて Z | `1dd 2dd 2du 2dd 2du 2cd 2cu 1du` | 連続して使える |

マクロの中の修飾キーが、条件の修飾キーと同じとき（Ctrl の条件で、マクロの途中に Ctrl）:

| No | 設定と操作 | OS に届いた並び | 判定 |
|---|---|---|---|
| X1 | Ctrl+H → [`LSFT(KC_END)`, `LCTL(KC_C)`, `KC_DELETE`]。H のあと Z | `1dd 1du 2ad 4fd 4fu 2au 1dd 2ed 2eu 1du 53d 53u` **`2cd 2cu`** `1du` | Ctrl が戻らない |
| Y1 | Alt の条件: Alt+Q → [`LALT(KC_F9)`]。Q のあと Z | `38d 1dd 1du 38u 38d 43d 43u 38u` **`2cd 2cu`** `38u` | Alt が戻らない（Z に Alt が付かない） |

どの場合も、試験の最後に、Ctrl・Shift・Alt・Win は、押されたまま残らなかった。

## 原因の見当（ソースを読んだだけで、確認は未了）

`driver/kbdlayer/LayerEngine.c` の `SendKeyOverrideMacro`（2044 行付近）は、マクロを送ったあとに、`restore = Hide & devExt->RealModsDown`（2065 行）で、「離した条件の修飾キーのうち、まだ押されているもの」を求めて、戻している。マクロの中の Ctrl の解放（`1du`）も、送出の入口 `ForwardToClass` を通るので、`RealModsDown` の Ctrl が落ち、`restore` が 0 になる、と読める。
- 物理の Ctrl は押したままなのに、`RealModsDown` が空になるため、次のキー置換の判定（`MatchKeyOverrideLocked`。`down = RealModsDown | AddedModsUnion`）にも、Ctrl が見えない。C6 の「2 回目が普通の `k`」と合う。
- C1 の最後の `1du`（物理の Ctrl を離したときの解放）は、OS 側では二重の解放になる（害はない）。

直し方の案（試験担当の範囲外。開発側の判断）: マクロを送る前の物理の修飾キーの状態を控えて、`restore` と `RealModsDown` を、その控えから戻す。あるいは、マクロの送出中は、マクロ自身が出した修飾キーの解放で `RealModsDown` を変えない。

## 顧客への答え（案）

- 現状の仕様での可能性: 「Ctrl+K のあと、Ctrl を押したまま操作する」ことは、**マクロの中に Ctrl を含めなければ、今の設定でできる**。行カットを `Ctrl+X` の代わりに `Shift+Delete` にする（マクロ: `LSFT(KC_END)`、`LSFT(KC_DELETE)`）。Shift+Delete が切り取りとして働くかは、アプリごとに異なる（メモ帳や Word などの標準的なテキスト欄は、働くことが多い。確認はしていない）。
- マクロの中に Ctrl+X を入れる形は、ドライバの不具合で、Ctrl が戻らない。直すにはドライバの修正が要る。

## 未実施（別件）

`key-override-v2-handoff.md` の試験（128 件、置き換え先のレイヤー操作・ヘルパー動作）は、**まだ実施できていない。** インストール済みのドライバが、このブランチのビルドではないため（上の環境の欄）。ユーザーが `feature/key-override-v2`（`main` から分岐、未 commit、作業ツリー `LayerDriver-ko2`）をビルドして、入れ替えてから行う。K4（旧サイズからのアップグレード）のために、今の状態（`KeyOverrides` が 384 バイトで、Id 0、1 が入っている）は、そのまま残してある。

## 後始末

昇格ランナーは止め、`C:\Users\Public` の一時ファイルを消した。設定した Id 26〜31 とマクロ 122〜126 は、すべて消した。ユーザーのレイヤーマップ、キー置換（Id 0、1）、`tray.json` は、変えていない。
