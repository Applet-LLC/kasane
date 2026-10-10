# のどか（nodoka4）の設定を取り込む

のどかの設定ファイル（`.nodoka`）を、kasane の設定に変換して取り込めます（2026-10-09、P6）。

## 使い方

1. kasane の **インポート／エクスポート...** → **インポート** タブ → **のどかの設定を取り込む...**
2. のどかの設定一覧（レジストリ `HKCU\Software\appletkan\nodoka`。★は今使っているもの）から選ぶか、**ファイルを選ぶ...** で `.nodoka` を指定します。
   シンボル（のどかの `-D`。`USE109` など）は、一覧から選ぶと入ります。直してかまいません。
3. **変換する**。結果は「変換できた／近似した／変換できない」に分けて、`ファイル名:行` つきで出ます（**表示** で切り替え）。
4. **この内容で取り込む** → インポート画面に戻り、ほかのインポートと同じように、項目と行き先を選んで取り込みます。
   のどかの設定は、**今の設定に足す（追加）** のが既定です（「置き換え」だと、のどかに無い項目も消えます）。
   取り込んだ内容は編集中の状態に入るだけなので、保存と「キーボードに適用」はそのあと行います。

include されているファイルも読みます（探す順は、のどかと同じ: 設定ファイルのフォルダ → のどかの今の設定のフォルダ → `%NODOKA%` → `%HOME%` → `%USERPROFILE%` → `%HOMEDRIVE%%HOMEPATH%` → のどかをインストールしたフォルダ〔レジストリの `currentPath`、無ければ Program Files の nodoka〕）。
文字コードは、BOM（UTF-16・UTF-8）→ UTF-8 → CP932 の順に判定します。

## 変換の考え方

のどかの設定は「キー × 修飾キーの状態 × キーマップ」の表、kasane は「レイヤーごとの、キー → 動作」の表です。そこで、次のように平らにします。

| のどか | kasane |
|---|---|
| `key *X = *Y`（Global） | レイヤー0: X → Y |
| `key X = Y`（修飾キーの指定なし） | 同じ。ただし、のどかは「修飾キーを押していないとき」だけ。kasane は Shift+X も Y にする → **近似**として報告 |
| `key C-X = Home`（修飾キーを押しながら） | キー置換（Mods=Ctrl、X → Home） |
| `key W-N = &ShellExecute(…)`（修飾キーを押しながら、ヘルパーの動作） | ヘルパーの**トリガー**（キーフック。押したキーはアプリに届かなくなる。トレイが必要）→ **近似** |
| 右辺に修飾キー（`S-_8`） | `LSFT(KC_8)`（修飾付きキー） |
| 右辺が複数のキー | マクロ（押した時に全部を順に打つ。64ステップまで）。長いもの・待ちや文字列を含むものはヘルパーの Sequence |
| `mod M0 = !Up` と `key M0-Left = …` | Up に `MO(レイヤー)`、そのレイヤーに Left の割り当て |
| `mod M0 = !!Space` | `LT(レイヤー, Space)`（タップなら Space、押し続けるとレイヤー） |
| `mod shift += !!Space`（SandS） | `MT(MOD_LSFT, KC_SPC)` |
| `mod ctrl += !英数` | 英数 → 左 Ctrl |
| `&Toggle(Lock0)` と `L0-` の割り当て | `TG(レイヤー)` と、そのレイヤーの割り当て。`on` / `off` はヘルパーの AppLayer |
| `&Prefix(km)` | `OSL(レイヤー)`（次の1キーだけ）と、km の割り当てを置いたレイヤー |
| `&EditNextModifier(M-)` | `OSM(MOD_LALT)` |
| `&Default` `&KeymapParent` `&OtherWindowClass` | 何もしない（そのまま通す／下のレイヤーに落とす） |
| `&Ignore` `&Undefined` | `KC_NO` |
| `&VK(…)` | そのキー。`LBUTTON` などはマウスのクリック（ヘルパー） |
| `&ShellExecute` `&SendText` `&Wait` `&MouseMove` `&MouseWheel` | ヘルパーの動作（Launch / Sequence）を `HLP(n)` にする |
| `combo A B = X`（2キー） | コンボ |
| `taphold X tap=A hold=LShift` | `MT(MOD_LSFT, A)`。修飾キー以外の hold は、タップダンスの「1回タップ / ホールド」 |
| `tapdance X tap1= tap2= tap3=` | タップダンス |
| `def subst` | 置き換え（修飾キーつきならキー置換） |
| `window W /class/ [: 親]` | W 用のレイヤーと、**アプリ別レイヤーの規則**（のどかと同じ照合: `NodokaClass` / `NodokaTitle`、`(/a/ \|\| /b/)` は `NodokaOr`）。後に定義した window から調べるので、規則は逆順に並ぶ。親が Global 以外なら、親の割り当てを引き継ぐ |
| `def option UnitID = K1 ExtraInfo 0xHHHH0000` と `K1-` の割り当て | そのキーボード（UniqId = ExtraInfo）向けの設定として別に作る。共通の設定も入る。インポートの対応表で行き先を確かめる |
| `TapHoldThreshold` `ComboWindow` `TapDanceTimeout` `TapHoldQuickTapTerm` `TapHold*` | タイミング（TapTerm・ComboTerm・TapDanceTerm・QuickTapTerm・TapHold の方式） |

## レイヤーの割り当て

Global がレイヤー0。ほかは、割り当てに出てきた順に 1, 2, … と振ります（M0〜M9、L0〜LF、`&Prefix` で呼ばれるキーマップ、window）。
16枚を超えたものは変換せず、報告に出ます。取り込むと、**今のレイヤーに同じ番号で重なります**（追加のとき）。今のレイヤー1以降を使っている場合は、
先に退避するか、行き先を確かめてください。

## 変換できないもの

次のものは変換せず、`ファイル名:行` つきで報告します（その行だけが無かったことになります。ほかの行は変換されます）。

- 条件: `IL-`（IME オン）・`IC-`（変換中）・`NL-` `CL-` `SL-` `KL-`・`MAX-` `MIN-` など・`DP-`（2回押し）・`U-` `D-`・`R-`・`T-`（タッチパッド）、`K0-`
- 関数: `&Window*`・`&Clipboard*`・`&Emacs*`・`&PostMessage`・`&SendPostMessage`・`&SetForegroundWindow`・`&LoadSetting`・`&HelpMessage`・`&Repeat`・`&MouseHook`・`&SetIme*` など
  （`$ToggleIME` のように、中身が普通のキーの keyseq は変換できます）
- マウス・ゲームパッドの疑似キー（`LButton`、`GP_*` など）を左辺や右辺に使うもの
- 3キー以上の combo、Global 以外の combo・taphold・tapdance、`event`、`mod !修飾キー名`、修飾キーから外す指定（`-=`）
- 修飾キーを押しながらの「複数キーの列」「レイヤーの操作」

## 近似（のどかと少し違う動き）

- 「修飾キーを押していないときだけ」という条件は、kasane では見分けません。
- 複数のキーの列は、最後のキーを押している間だけ押し続ける、という動きにはなりません。
- 「!」の付かない `mod M0 = X` は、のどかではキー自体の動作も残りますが、kasane ではレイヤーキーとして働きます。
- `&Prefix` は `OSL`（次の1キーだけ）です。
- 修飾キーを押しながらのヘルパー動作は、キーフックのトリガーです（アプリには届かず、トレイが動いている必要があります）。
