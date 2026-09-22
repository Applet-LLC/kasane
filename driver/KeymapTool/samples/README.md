# KeymapTool サンプル

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
