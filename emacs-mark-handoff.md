# 申し送り: マークモード（MARK）の試験

対象ブランチ: `feature/emacs-mark`（`main` の `72c86ac` から分岐）。ドライバ（kbdlayer.sys）を入れ替える必要がある。ユーザーが Release|x64 のテスト署名ビルドを用意する。kasane・KeymapTool・トレイも同じブランチのビルドを使う。

## 何を確かめたいか

Emacs のマークモードを、ドライバが状態として持つ。キー置換の置き換え先（とレイヤーのキー）に `MARK(on|off|toggle)` を置ける。

- マークが有効の間、移動キー（←↑→↓、Home、End、PageUp、PageDown）の出力に Shift を足す。Ctrl+F → → のようなキー置換の出力にも足す。置き換えの無い、素の矢印キーにも足す。
- 移動キーと修飾キー以外のキーを送ったあとで、マークが終わる（C-w、Esc、文字、Enter など）。
- `MARK(on)` を有効のときに押しても、有効のまま。
- マップの OFF、緊急解除、ライセンス無効で終わる。前面のウィンドウが変わったとき、トレイが `IOCTL_KBDLAYER_CLEAR_MARK` を送って終わらせる。

## 前提の確認

1. `KeymapTool --set-key-override <ID> 40 MOD_LCTL KC_SPACE "MARK(on)"` が成功し、`--get-key-override` で `Type=17 Param1=0x00` と読めれば、新しいドライバ。古いドライバは失敗する（エラー 87）。
2. Driver Verifier（標準設定）が `kbdlayer.sys` に有効か。

## 試験

準備: `samples/emacs/apply-emacs.ps1` で設定例を書き込む（レイヤー 2 枚、マクロ 9 件、キー置換 34 件）。OIB 注入で、受け口が受けたキーを記録する。

| No | 操作 | 期待 |
|---|---|---|
| A1 | Ctrl+Space（マーク入）、Ctrl+F、Ctrl+F、Ctrl+N、Ctrl+A、Ctrl+E、Ctrl+G | 5 回の移動すべてに Shift が付く（`2ad … 2au`）。Ctrl+G は Esc を出し、マークが終わる |
| A2 | A1 のあと、Ctrl+F | Shift なしの → |
| A3 | マーク入、Alt+F、Alt+V | Ctrl+Shift+→、Shift+PageUp |
| A4 | マーク入、Ctrl+W（切り取り）のあと、Ctrl+F | 切り取りのあと、マークが終わり、Shift なしの → |
| A5 | マーク入、文字（A）のあと、Ctrl+F | A のあと、マークが終わり、Shift なしの → |
| A6 | マーク入、Ctrl+Space をもう一度、Ctrl+F | 有効のまま。Shift 付きの → |
| A7 | マーク入、素の矢印キー（←、Home など。キー置換の無いキー） | Shift が付く |
| A8 | マーク入、Shift を押したまま Ctrl+F | Shift は余計に押されない。取り残しなし |
| A9 | マーク入、`--off`（マップ OFF）、`--on`、Ctrl+F | マークは終わっている（Shift なし） |
| A10 | マーク入、`IOCTL_KBDLAYER_CLEAR_MARK`（トレイの前面ウィンドウの変更、または下の B1）、Ctrl+F | Shift なし |
| A11 | マーク入、Ctrl+X → OSL(1) → s（C-x C-s） | Ctrl+S が出てマークが終わる（`MACRO` の出力 = 文字キー） |
| A12 | マーク入、押したままのキーのオートリピート（Ctrl+F を押し続ける） | リピートでも Shift が付いたまま。離したあとに Shift が残らない |
| A13 | マークなし | 動作は変わらない（試験 2・5・K6 の回帰） |

各項目で、実行後に Ctrl・Shift・Alt が押されたまま残っていないこと。

## 試験 B: 前面のウィンドウの変更でマークを解除（トレイ）

| No | 内容 | 期待 |
|---|---|---|
| B1 | トレイが動いている状態で、マーク入にして、前面のウィンドウを替える（別のアプリをアクティブにする）。Ctrl+F | Shift なし |
| B2 | 昇格していないトレイでも動く | 同上（`FILE_READ_ACCESS`） |
| B3 | 同じウィンドウの中のフォーカスの変更（ボタンからテキスト欄など） | マークは続く |
| B4 | トレイが動いていないとき、ウィンドウを替える | マークは続く（仕様） |

## 試験 C: kasane（画面）と KeymapTool

1. 「キー置換...」の置き換え先に `MARK(on)`・`MARK(off)`・`MARK(toggle)` を入力して OK。`MARK(foo)` はエラー。
2. 保存して開き直しても同じ内容。
3. インポート／エクスポートで、`MARK(on)` が往復する。
4. `KeymapTool --set-key-override` で `MARK(on)` を設定でき、`--get-key-override` で `Type=17` と読める。
5. 古い KeymapTool（`main` のビルド）で `Type=17` の項目を読めるか（数値で表示される）。

## 安全

- Driver Verifier の下で A、B を繰り返す。A1 を高速に 200 回。マークの入り切りの途中で `--off`、緊急解除。USB キーボードの抜き差しをしながら `MARK` の設定と解除を連発。
- ユーザーの作業中のウィンドウに、注入したキーが入らないよう、受け口が最前面で受けているか確かめる。**同期が取れなかったとき（`sync marker lost`）は、試験を止めて、キーの行き先を確かめる**（前回、取り直しの間のキーが、ほかの窓に入った可能性があった）。

結果は `emacs-mark-results.md` に書く。
