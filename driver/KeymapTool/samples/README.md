# KeymapTool サンプル

## test-keymap-tapdance.json — Phase 5 (タップダンス, v1: タップ回数3回まで) 動作確認用

10キー・1レイヤーで、位置0に`TAPDANCE(0)`を配置した最小テストキーマップ。タップダンス
自体はkeymap.jsonには含まれず、`--set-macro`/`--set-combo`と同じ方式で`--set-tapdance`
により別途デバイスへ登録する。

v1のスコープ限定（`common/kbdlayer_ioctl.h`参照）: タップ回数は3回まで区別
（SingleTap/SingleHold/DoubleTap/TripleTap）。ホールド判定は1回目の押下にのみ存在し
（2回目以降を押しっぱなしにしても専用のHoldアクションは無い）、SingleHoldのみ
KEY/MODTAP/LAYERTAPを許可、他3つはKEYのみ許可。

### 手順

```
KeymapTool.exe --list
KeymapTool.exe --calibrate-simple <UniqIdHex> 10 test-profile.json
KeymapTool.exe --apply-simple <UniqIdHex> test-keymap-tapdance.json test-profile.json

REM タップダンス0を「タップ=Esc、ホールド=Ctrl、ダブルタップ=Tab、トリプルタップ=CapsLock」
REM として定義する（SingleHoldはMT(mod,kc)を渡せる。タップ時キーコード引数(KC_NO)は
REM 無視される。ホールド時のmod部分だけが使われる）。
KeymapTool.exe --set-tapdance <UniqIdHex> 0 KC_ESC "MT(MOD_LCTL,KC_NO)" KC_TAB KC_CAPS

KeymapTool.exe --on <UniqIdHex>
```

TapDanceTermMs（タップ間・ホールド判定の閾値、既定200ms）を変えたい場合:
```
KeymapTool.exe --set-config <UniqIdHex> 200 50 300
```
（第2引数がTapTermMs、第3引数がComboTermMs、第4引数がTapDanceTermMs。他を変えたくない
場合は既存の値をそのまま渡すこと。0を渡すとその項目だけ既定値にリセットされる。）

### 確認項目

**SingleTap**
- 位置0を単体でタップ（素早く押して離し、そのまま何もしない） → `Esc`が出力される。

**SingleHold（他キー押下によるホールド確定）**
- 位置0を押しっぱなしで位置1(`b`)を押す → **Ctrl+B**になる（`b`単体は出力されない）。
- 離した後、Ctrlが張り付かないことを確認する（何度でも連続して繰り返せること）。

**SingleHold（タイムアウトによるホールド確定）**
- 位置0を押したまま、他のキーに一切触れずにTapDanceTermMs（既定200ms）以上待つ。
- その状態で位置1を押す → Ctrl+Bになる（タイムアウトの時点で既にホールド確定している）。
- クラッシュしないこと、離した後に張り付かないことを確認する。

**DoubleTap**
- 位置0を素早く2回連続でタップ（2回目の押下がTapDanceTermMs以内に来ること） → `Tab`が
  出力される。`Esc`は出力されない。

**DoubleTap（タイムアウト経由の確定）** *** DPCからpress+releaseの2イベントを連続送出する
新規経路。実機で重点的に確認すること ***
- 位置0を1回タップした直後、すぐに2回目を押して素早く離し、そのまま何もせず
  TapDanceTermMs以上待つ → `Tab`が出力される。

**TripleTap**
- 位置0を素早く3回連続でタップ → `CapsLock`が出力される（3回目の押下が来た時点で、
  それ以上待たずに即確定する）。

**通常キーへの影響なし**
- 位置1〜9（`b`〜`j`）がタップダンスと無関係に普段通り動くこと。

- `KeymapTool.exe --get-tapdance <UniqIdHex> 0` → 登録した4つのアクション
  （SingleTap/SingleHold/DoubleTap/TripleTap）がそのまま返ってくることを確認できる。

### 元に戻す

```
KeymapTool.exe --set-tapdance <UniqIdHex> 0
KeymapTool.exe --off <UniqIdHex>
KeymapTool.exe --clear <UniqIdHex>
```

(`--set-tapdance`に4つのアクションを1つも渡さないとタップダンス0がクリアされる)

## test-keymap-combo.json — Phase 4 (コンボ, v1: 2キー限定) 動作確認用

10キー・1レイヤーで、`KC_A`〜`KC_J`の基本キーのみを配置した最小テストキーマップ
（`test-keymap-macro.json`と同じ形）。コンボ自体はkeymap.jsonには含まれず、
`--set-macro`と同じ方式で`--set-combo`により別途デバイスへ登録する。

v1のスコープ限定（`common/kbdlayer_ioctl.h`参照）: メンバーは常に2キー、出力は
単純キーのみ、同一キーが複数コンボに属することは禁止、出力の解除は2つの
メンバーキーのどちらかが離された時点で行う。

### 手順

```
KeymapTool.exe --list
KeymapTool.exe --calibrate-simple <UniqIdHex> 10 test-profile.json
REM 案内の1番目・2番目で、物理的な A キー・B キーを押すこと
REM （--set-comboで使うKC_A/KC_Bの標準スキャンコードと実機の物理位置を一致させるため）
KeymapTool.exe --apply-simple <UniqIdHex> test-keymap-combo.json test-profile.json

REM コンボ0を「A+B同時押し → Esc」として定義する
KeymapTool.exe --set-combo <UniqIdHex> 0 KC_A KC_B KC_ESC

KeymapTool.exe --on <UniqIdHex>
```

ComboTermMs（コンボ判定の許容時間、既定50ms）を変えたい場合:
```
KeymapTool.exe --set-config <UniqIdHex> 200 100
```
（第2引数がTapTermMs、第3引数がComboTermMs。TapTermMsを変えたくない場合は
既存の値をそのまま渡すこと。0を渡すとその項目だけ既定値にリセットされる。）

### 確認項目

**成立（タップ）**
- Aを押してすぐBを押す（ComboTermMs以内） → `Esc`が出力される。`a`や`b`単体の
  文字は出力されない。
- A・Bのどちらを先に押しても成立すること。

**成立中の挙動**
- A+Bで成立させたまま押さえ続ける → オートリピートでEscが連打されたりしない
  （余分な出力が無いこと）。
- 成立中にA・B以外のキー（例: 位置2の`c`）を押す → `c`が普段通り出力される
  （コンボが他のキー処理に干渉しないこと）。

**解除**
- 成立中にAだけを離す → その時点でEscが解除される（Bがまだ押されていても）。
- 続けてBを離す → 何も出力されない（ドレイン中の解放は無害に飲み込まれる）。
- 逆にBを先に離した場合も同様に、その時点でEsc解除・Aの後離しは無音。

**不成立（相方が来る前に離す）**
- Aだけを押してComboTermMs以内に離す（Bに触れない） → `a`が出力される
  （保留していたタップが通常のキーとして復元されること）。

**不成立（タイムアウト）** *** DPCからの自発呼び出し経路。実機で重点的に
確認すること（Phase 2のタイムアウト確定と同じ検証観点） ***
- Aだけを押したまま、他のキーに一切触れずにComboTermMs（既定50ms、短いので
  意識して長めに待つこと）以上経過させる → その時点で`a`が出力される。
- その後Aを離す → 追加の出力は無い（通常のキー解放として処理される）。
- クラッシュしないこと、余分な文字が出ないことを確認する。

**不成立（無関係キーによる割り込み）**
- Aを押した直後（Bに触れる前）に、無関係な位置2(`c`)を押す → `a`と`c`の両方が
  この順で出力される（保留していたAのタップが先に復元され、割り込んだcはそのまま
  通常処理される）。

**通常キーへの影響なし**
- 位置2〜9（`c`〜`j`）がコンボと無関係に普段通り動くこと。

- `KeymapTool.exe --get-combo <UniqIdHex> 0` → 登録した内容
  （ScanCode1/ScanCode2/OutputScanCode）がそのまま返ってくることを確認できる。

### 元に戻す

```
KeymapTool.exe --set-combo <UniqIdHex> 0
KeymapTool.exe --off <UniqIdHex>
KeymapTool.exe --clear <UniqIdHex>
```

(`--set-combo`にキーコードを1つも渡さないとコンボ0がクリアされる)

## test-keymap-macro.json — Phase 3 (マクロ) 動作確認用

10キー・1レイヤーで、`MACRO(id)`を確認する最小テストキーマップ。位置0が
`MACRO(0)`。マクロの中身（何を送出するか）はkeymap.jsonには含まれず、
`--set-macro`で別途デバイスへ登録する必要がある点に注意。

### 手順

```
KeymapTool.exe --list
KeymapTool.exe --calibrate-simple <UniqIdHex> 10 test-profile.json
KeymapTool.exe --apply-simple <UniqIdHex> test-keymap-macro.json test-profile.json

REM マクロ0の中身を「hi」と定義する（各キーコードはタップ=押下+解放として展開される）
KeymapTool.exe --set-macro <UniqIdHex> 0 KC_H KC_I

KeymapTool.exe --on <UniqIdHex>
```

### 確認項目

- 位置0をタップ → `hi` が2文字続けて出力される（1回のキー入力から複数文字が
  出力されることを確認）。
- 位置0を連打 → 毎回`hi`が出力される（マクロが何度でも再実行できること）。
- 位置1〜9（通常キー`a`〜`i`）が普段通り動くこと（マクロ機構が他のキー処理に
  影響しないこと）。
- `KeymapTool.exe --get-macro <UniqIdHex> 0` → 登録した4ステップ
  （H押下, H解放, I押下, I解放）がそのまま返ってくることを確認できる。

マクロを別の内容に変えたい場合は、`--off`にしてから`--set-macro`をやり直すのではなく、
`--set-macro`はいつでも上書きできる（`LayerMapEnabled`の状態に依存しない）ので、
`--on`のまま再定義して構わない。

### 元に戻す

```
KeymapTool.exe --set-macro <UniqIdHex> 0
KeymapTool.exe --off <UniqIdHex>
KeymapTool.exe --clear <UniqIdHex>
```

(`--set-macro`にキーコードを1つも渡さないとマクロ0がクリアされる)

## test-keymap-modtap.json — Phase 2 (Mod-Tap/Layer-Tap) 動作確認用

10キー・2レイヤーで、`MT(mod,kc)`（Mod-Tap）と`LT(layer,kc)`（Layer-Tap）を
一通り確認できる最小テストキーマップ。位置2が`MT(MOD_LCTL,KC_C)`、位置3が
`LT(1,KC_D)`。それ以外は`test-keymap-simple.json`と同様の基本キー。

### 手順

```
KeymapTool.exe --list
KeymapTool.exe --calibrate-simple <UniqIdHex> 10 test-profile.json
KeymapTool.exe --apply-simple <UniqIdHex> test-keymap-modtap.json test-profile.json
KeymapTool.exe --on <UniqIdHex>
```

TapTermMs（タップ/ホールドの閾値、既定200ms）を変えたい場合:
```
KeymapTool.exe --set-config <UniqIdHex> 300
```

### 確認項目（重要: ホールドが「他キー押下」で確定したか「タイムアウト」で
確定したかで期待される挙動が変わるので、両方を区別して確認すること）

**MT（位置2）: タップ**
- 位置2を単体でタップ（素早く押して離す） → `c` が出力される。

**MT（位置2）: 他キー押下によるホールド確定**（実機で検証済みの経路。
phase2-spikeブランチと同じ仕組み）
- 位置2を押しっぱなしで位置0を押す → **Ctrl+A**（メモ帳なら全選択）になる。
  文字としての `a` は出力されない。
- 離した後、Ctrlが張り付かないことを確認する（何度でも連続して繰り返せること）。

**MT（位置2）: タイムアウトによるホールド確定**（*** phase2-spikeブランチでは
未検証の経路。ここが今回初めて実機で確認する部分 ***）
- 位置2を押したまま、**他のキーに一切触れずに** 200ms（既定値）以上待つ。
- その状態で位置0を押す → この時点では既にタイムアウトでホールドが確定して
  いるはずなので、位置0はレイヤーエンジンを経由した通常のキーとして解決され、
  Ctrlと組み合わさって **Ctrl+A** になる（他キー押下確定の場合との違い:
  こちらは最初に押した位置0からCtrl+Aとして機能するはずで、"生のaがまず出て
  から" ということにはならない）。
- クラッシュしないこと、Ctrlが正しく反映されること、離した後に張り付かないことを確認する。

**LT（位置3）: タップ**
- 位置3を単体でタップ → `d` が出力される。

**LT（位置3）: 他キー押下によるホールド確定**
- 位置3を押しっぱなしで位置0を押す → 割り込んだ位置0はレイヤーエンジンを
  経由しない生のスキャンコードとして転送される設計のため、`1`ではなく`a`が
  出力される（既知の仕様。プラン記載の「割り込んだキー自身は現在アクティブな
  レイヤーの影響を受けない」という制約）。
- 位置3を押しっぱなしのまま、続けてもう一度位置0を押す → 今度はレイヤー1が
  既に有効になった状態での通常のキー解決になるため、`1`が出力される。
- 位置3を離す → 特に何も出力されない（LTのホールド解除はActiveLayerMaskの
  ビットを戻すだけで、キーイベントの合成は不要なため）。

**LT（位置3）: タイムアウトによるホールド確定**
- 位置3を押したまま、他のキーに触れずに200ms以上待つ。
- その状態で位置0を押す → 割り込みキー方式との違いとして、こちらは**最初から**
  `1`が出力されるはず（タイムアウトの時点で既にレイヤー1が有効になっている
  ため、位置0は最初から通常のレイヤー解決を経由する）。

### 元に戻す

```
KeymapTool.exe --off <UniqIdHex>
KeymapTool.exe --clear <UniqIdHex>
```

## test-keymap-us104.json / test-keymap-jis.json — 実サイズの104/JISキーボード用

「日本語配列の基本的な109キーボード用」「英語配列の104キーボード用」のテストキーマップ。
`test-keymap-simple.json`（下記）は10キーの最小スモークテストで、英語配列専用ではなく
物理レイアウトに依存しない（US/JISどちらの実機にも共通して存在する基本的な英数字キーだけを
使っている）。実サイズのキーボード全体を通しで確認したい場合はこちらを使う。

- **test-keymap-us104.json**: 標準的な US ANSI 104キー配列。全キーがレイヤー0では
  「そのキー本来の動作」になっている（素通し確認用）。レイヤー1はほぼ透過だが、
  右Alt位置(71番目)だけ `MO(1)` に差し替えてレイヤー切替キーとして使い、数字キー列を
  F1〜F10に、QキーをEscに一時的に変える小さなデモを仕込んである（レイヤー切替が
  実際に効くことを目視確認するため）。
- **test-keymap-jis.json**: 上記104キーの並びに、JIS配列固有の6キー
  （半角/全角=`KC_ZKHK`、ろ=`KC_RO`、かな=`KC_KANA`、¥=`KC_JYEN`、変換=`KC_HENK`、
  無変換=`KC_MHEN`）を105〜110番目として追加したもの。

  **「109キー」の数え方についての注記**: 一般に言う「日本語109キーボード」は、US 104キーに
  対して物理キー5個の追加（一部のJIS専用キーがUS配列のキーと同じ物理位置を共有するため）
  という数え方が広く使われている。このテストキーマップは、どのキーがどの物理位置と共有される
  かを機種ごとに正確に特定するのが難しいため、安全側に倒して6キー全てを独立したスロット
  として並べており、結果として110スロットになっている。お使いの実機のキー数が109個しかなく
  1つのキーで2つの役割を兼ねている場合は、キャリブレーション時にその物理キーを2回（重複する
  スロットの分だけ）押せばよい（同じスキャンコードが2つのスロットに記録されるだけで、動作上は
  問題ない）。

### キャリブレーション時に押すキーの順番

`--calibrate-simple <UniqIdHex> 104 profile.json`（JIS版は`110`）を実行すると、
以下の順で1つずつ押すよう案内される。この順番は上記2つのJSONファイルの配列順と完全に
一致させてあるので、案内された順に押していけばよい（キーボードの物理配置を見ながら
追う必要はない）。

1. Esc
2. F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12
3. `（半角/全角・グレイブ）, 1, 2, 3, 4, 5, 6, 7, 8, 9, 0
4. -, =, Backspace
5. Tab
6. Q, W, E, R, T, Y, U, I, O, P
7. [, ], \
8. CapsLock
9. A, S, D, F, G, H, J, K, L
10. ;, ', Enter
11. 左Shift
12. Z, X, C, V, B, N, M
13. , , . , /, 右Shift
14. 左Ctrl, 左Win, 左Alt, Space, 右Alt（＝MO(1)）, 右Win, Menu/App, 右Ctrl
15. PrintScreen, ScrollLock, Pause
16. Insert, Home, PageUp
17. Delete, End, PageDown
18. ↑, ←, ↓, →
19. NumLock, テンキー/, テンキー*, テンキー-
20. テンキー7, テンキー8, テンキー9, テンキー+
21. テンキー4, テンキー5, テンキー6
22. テンキー1, テンキー2, テンキー3, テンキーEnter
23. テンキー0, テンキー.
24.（JIS版のみ）半角/全角, ろ, かな, ¥, 変換, 無変換

### 使い方

```
KeymapTool.exe --list
KeymapTool.exe --calibrate-simple <UniqIdHex> 104 profile-us104.json
KeymapTool.exe --apply-simple <UniqIdHex> test-keymap-us104.json profile-us104.json
KeymapTool.exe --on <UniqIdHex>
```

JIS版はキー数を`110`にして`test-keymap-jis.json`を使う。

### 期待される挙動

- レイヤー0: 全キーが素の英数字配列として機能する（メモ帳等で確認）。
- 右Altを押しっぱなしで数字キー(1〜0)を押す → F1〜F10が入力される。
- 右Altを押しっぱなしでQを押す → Escとして動作する。
- それ以外のキーは右Altを押していても変化しない（透過）。

## test-keymap-simple.json — Phase 1 動作確認用

実機のinfo.jsonを用意しなくても試せる、10キー・2レイヤーの最小テストキーマップ。
`--calibrate-simple` / `--apply-simple`（簡易モード）専用。MO/TG/OSL/DF/KC_TRNS/基本キーを
一通り含み、Phase 1のレイヤーエンジンの主要な分岐を1回の手順で確認できる。

物理キーボードのどの10キーを使うかは自由（キャリブレーション時に押した順で位置0〜9が決まる）。
テストしやすいよう、誤操作しても被害が小さいキー（英数字キー中心）を選ぶことを推奨する。

### 手順

```
# 1. 対象デバイスのUniqIdを確認
KeymapTool.exe --list

# 2. キャリブレーション: 案内に従って好きな10個の物理キーを順番に押す
KeymapTool.exe --calibrate-simple <UniqIdHex> 10 test-profile.json

# 3. キーマップを適用（この時点ではまだ無効。--onするまでは素通しのまま）
KeymapTool.exe --apply-simple <UniqIdHex> test-keymap-simple.json test-profile.json

# 4. 有効化
KeymapTool.exe --on <UniqIdHex>
```

### 期待される挙動（メモ帳等で確認）

キャリブレーションで押した順を位置0〜9とすると:

| 位置 | レイヤー0（基底） | レイヤー1 |
|---|---|---|
| 0 | `a` | `1` |
| 1 | `b` | `2` |
| 2 | `c` | `3` |
| 3 | `d` | `4` |
| 4 | MO(1)（押している間だけレイヤー1） | — |
| 5 | TG(1)（押すたびにレイヤー1のON/OFFを切替） | — |
| 6 | OSL(1)（次の1キーだけレイヤー1が有効） | — |
| 7 | DF(1)（基底レイヤーを1に変更） | DF(0)（基底レイヤーを0に戻す） |
| 8 | `e` | 透過（レイヤー0の`e`がそのまま出る） |
| 9 | `f` | `g` |

具体的な確認シナリオ:
- **基本キー**: 位置0〜3を押す → `a`,`b`,`c`,`d` が出る（レイヤー切替前の素の状態）。
- **MO (Momentary)**: 位置4を押しっぱなしで位置0を押す → `1` が出る（レイヤー1に一時切替）。
  位置4を離すと位置0はまた `a` に戻る。
- **TG (Toggle)**: 位置5を1回タップ → 以後、位置0〜3を押すと `1`〜`4` が出続ける
  （レイヤー1がONのまま）。位置5をもう1回タップするとレイヤー0に戻る。
- **OSL (One-shot layer)**: 位置6を1回タップした直後に1回だけ位置0を押す → `1` が出るが、
  その次に位置0を押すと `a` に戻っている（ワンショットが消費された）。
- **DF (Default layer)**: 位置7を押す → 基底レイヤーが1に切り替わり、以後MO/TG/OSLを何も
  操作していなくても位置0〜3は `1`〜`4` を出し続ける。位置7をもう一度押す
  （レイヤー1側のDF(0)が発動）と基底レイヤー0に戻る。
- **透過 (KC_TRNS)**: レイヤー1がアクティブな状態で位置8を押す → レイヤー1側は透過なので
  レイヤー0の `e` がそのまま出る。

### 元に戻す

```
KeymapTool.exe --off <UniqIdHex>
KeymapTool.exe --clear <UniqIdHex>
```

## info.jsonを使った本番相当のテスト（単一レイアウトの実機向け）

対象キーボードがレイアウトオプション（ANSI/ISO切替、Split Spacebarなど）を持たない
単一レイアウトの場合、実機のQMK info.jsonをそのまま使う `--calibrate` / `--apply`
（info.jsonモード）でも同様に安全に試せる。手順は上記の `-simple` を外すだけで、
info.jsonの `layouts.<name>.layout` 配列の並び順がそのままキャリブレーションの案内順になる。

```
KeymapTool.exe --calibrate <UniqIdHex> path\to\info.json test-profile.json
KeymapTool.exe --apply <UniqIdHex> path\to\info.json path\to\keymap.json test-profile.json
```

レイアウトオプションを持つキーボード（VIAで複数のレイアウト選択肢がある機種）の場合は、
VIAがエクスポートするkeymap.jsonのフラット配列順が選択中のレイアウトオプションに依存し、
info.jsonの宣言順と食い違う可能性があるため、まずは上記の簡易モードで動作確認することを
推奨する。
