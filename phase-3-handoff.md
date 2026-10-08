# P3 申し送り: ヘルパー動作（HLP の受信、F13〜F24 のキーフック、動作の実行）

- 作業ブランチ: `claude/tender-euler-xxfl7a`、対象コミット: `d0e54d6`
- 書いた日: 2026-10-08（開発担当）。**こちらでは未ビルド**（.NET SDK なし）。C# の構文だけ、パーサーで確かめた（意味の検査はしていない）。コンパイルエラーが出たら、そのまま書いてほしい。
- ドライバの変更は無い（P0 の `WAIT_HELPER_EVENT` を、トレイが使う）。**トレイ（kasane-tray）、エディタ（kasane。クラスを partial にしただけ）、テスト**の変更。

## インストールの確認（P2.2 の所見の続き）
- インストール後に、`kasane-tray.exe` と `kasane.exe` の ProductVersion が **`…+d0e54d6…`** になっていることを、試験の前に確かめる。
- 上書きインストールでファイルが置き換わらなかった件（`phase-2-fix2-results.md`）は、まだ原因が分かっていない。**置き換わらなかったら**、`msiexec /i <msi> /l*v install.log` で入れ直し、ログの該当コンポーネントの行（置き換え／スキップ）と、そのときトレイが動いていたか、を `findings.md` に書いてほしい。

## 何が変わったか

トレイに、キー1つで動作を動かす仕組みを足した。動作は helper.json の `Actions`（HLP(id) から呼ぶ）と `Triggers`（キーフックで受ける）に書く。**設定画面はまだ無い（P4）。helper.json を手で書く。** 例は `samples/helper.sample.json`。

- **HLP(id)（CUSTOM）の受信**（`HelperEventService`）: ドライバのヘルパーイベントを専用スレッドで待ち、`Actions` の `Id` で動作を引く。キーボードごとの helper.json → 共通（tray.json の `Helper`）の順。
  - 待つのは、`Actions` が1つでもあり、セッションがアクティブなときだけ。
  - 古いドライバ（`ERROR_INVALID_FUNCTION`）なら止めて、`tray.log` に書く。
- **キーフック**（`KeyHookService`）: `Triggers` が**1つでもあるときだけ**、低レベルキーボードフックを張る。0 件なら張らない（設定を変えると、3 秒ほどで張る・外す）。
  - 注入されたキーは見ない。キーボードは `dwExtraInfo` の上位16bit で区別する。修飾キーは左右を区別せず、押している分と過不足なく一致したときだけ。
  - 一致した押下と、対応する解放・オートリピートを飲み込む。
- **動作**（`ActionRunner` の専用スレッド）
  - `Launch`: プログラムを起動（引数、作業フォルダ、`%VAR%` の展開）。`Open`: URL・ファイルを開く。昇格はしない。
  - `Sequence`: `Steps` を順に実行。`Key`（`LCTL(KC_C)` など）、`Down`/`Up`、`Text`（Unicode。改行は Enter、タブは Tab）、`DelayMs`（最大 10 秒）、`Mouse`（Left/Right/Middle/X1/X2 と Click/Down/Up）、`MoveX`/`MoveY`（`Absolute` で画面座標）、`Wheel`/`HWheel`。キーを離したときは `ReleaseSteps`。
  - `AppLayer`: `Layers` を `LayerOp`（On/Off/Toggle/Hold）で操作。入力源は "action"（IME 連動・前面のアプリの規則と共存）。
  - `PauseToggle`: トレイの一時停止・再開。
  - `Repeat: true` のときだけ、オートリピートで繰り返す。
- **許可**: 自分のセッションがアクティブなときだけ実行する。**一時停止中は `PauseToggle` だけ**実行する（ホットキーで再開できるように）。許さないときは、キーフックは飲み込まずに素通しする。

## ビルドとインストール
- `kasane.sln` をビルドする。ドライバは P0 のまま（再署名は不要）。インストール済みのトレイを入れ替えるときは、トレイを終了してから置き換え、起動し直す。
- `dotnet test kasane.Tests`（新規: `HelperActionsTests`）。

## 試験項目

準備
- キーボードを1台決め、`driver/KeymapTool/samples/test-keymap-helper.json`（HLP(1)・HLP(0x102)・MO(1)・KC_X。レイヤー1に HLP(3)・HLP(4)）を適用する（`README.md` の手順）。
- helper.json（`%LOCALAPPDATA%\applet\kasane\<UniqId:X8>\helper.json`）を手で書く。**書いたら 3〜4 秒待つ**（トレイの読み直し）。
- 使う道具: `KeymapTool --watch-helper`、`--watch-layer`、testsink（文字の確認）、OIB の注入（T-7、T-9、T-14）。

| # | 内容 | 期待値 |
|---|---|---|
| B1 | `dotnet test kasane.Tests` | すべて合格 |
| B2 | `Actions` に `{Id:1, Launch notepad.exe}`。HLP(1) のキーを押す | メモ帳が起動する。同時に `--watch-helper` に押下・解放が出る。testsink には何も届かない。解放では何も起きない |
| B3 | B2 の `Launch` で `Args`・`WorkDir`・`%USERPROFILE%` を使う。`Open` で URL とファイル | それぞれ期待どおり。存在しないパスは `tray.log` に出て、トレイは落ちない |
| B4 | `Sequence`: `Key`（`KC_A`、`LCTL(KC_C)`、`SC_E06A`）、`Down`/`Up`、`DelayMs` を並べ、testsink で見る | 順序・間隔（`DelayMs` ±20ms ほど）が期待どおり。修飾キーが残らない（最後に Ctrl が押しっぱなしにならない） |
| B5 | `Sequence` の `Text`: 日本語・記号・改行・タブ・サロゲートペア（絵文字）を含む | testsink で文字がそのまま届く（改行は Enter、タブは Tab） |
| B6 | `Sequence` のマウス: `MoveX`/`MoveY`（相対・`Absolute`）、`Mouse` の Click/Down/Up、`Wheel`/`HWheel` | カーソル位置・クリック・スクロールを、別の窓や `GetCursorPos` で確かめる。`Absolute` は複数モニターの仮想座標 |
| B7 | `ReleaseSteps`: 押す=マウス Left Down、離す=Left Up（ドラッグ） | 押している間だけボタンが押され、離すと上がる。トレイを kill してもボタンが残らない（残ったら記録） |
| B8 | `Repeat` の有無: キーを押し続ける | `Repeat:false` は押下で1回だけ。`true` は、オートリピートで繰り返す（HLP のオートリピートはドライバが Flags で知らせる） |
| B9 | `AppLayer`: On / Off / Toggle / Hold を `--watch-layer` の `App=` で見る | それぞれ期待どおり。Hold は押している間だけ。IME 連動・前面のアプリの規則と同じレイヤー／別のレイヤーを同時に使い、片方が外れても残る |
| B10 | `PauseToggle`（HLP）。一時停止中にもう一度（トレイメニューで再開する代わり） | 一時停止・再開が切り替わる。**一時停止中は、HLP のキー自体がマップ OFF で届かない**ので、HLP では再開できない（仕様）。B13 のフックなら再開できる |
| B11 | `Triggers`: `KC_F13`（修飾なし）と `KC_F13` + `MOD_LSFT` に別の動作。F13 を出すキー（P1 の設定）を押す | 動作が動く。**F13 は testsink に届かない**（飲み込む）。Shift+F13 は別の動作。Ctrl+F13 は一致しないので素通し |
| B12 | キーボード2台: それぞれの helper.json に、別の動作を持つ F13 の `Triggers`。共通（tray.json の `Helper`）にも1つ | それぞれの台で別の動作になる。持たない台は共通の動作。ドライバを通らない入力（OIB でない合成など）は共通だけ |
| B13 | `Triggers` に `PauseToggle` の F13。一時停止中に F13 | 再開できる。ほかの `Triggers` の動作は、一時停止中は動かず、**飲み込まず素通し**になる |
| B14 | `Triggers` が 0 件のとき | F13 は素通し（フックが無い）。`Triggers` を足す→3〜4 秒で飲み込むようになる。消す→素通しに戻る。`Actions` だけの設定では、フックを張らない（可能なら、別の LL フックのツールで順序の影響が無いことも） |
| B15 | トレイを kill（`taskkill /f`） | フックが外れる（F13 が素通し）。保留中の WAIT_HELPER_EVENT が取り消される（T-27 のハンドル確認で、`kasane-tray` がコントロールデバイスのハンドルを持たない）。起動し直せば、また動く |
| B16 | トレイをメニューの「終了」で終わらせる | 3 秒以内に終了し、プロセスが残らない。フックと待機が止まる |
| B17 | ロック（Win+L）→ 解除、ユーザーの切り替え | ロック中は HLP も `Triggers` も実行されない（`Triggers` は素通し）。解除後は動く。ロック中に押した分が、解除後にまとめて動かない |
| B18 | 管理者として動くウィンドウが前面（T-26） | `Launch`/`Open` は動く。`Sequence` の入力は**届かない**（UIPI。仕様として確認。ほかのアプリに誤って届かない） |
| B19 | 連打・長い `Sequence`: HLP を 100 回ほど連打。10 秒の待ちを含む手順を動かしながら、トレイを終了 | クラッシュしない。キューが満杯になったら `tray.log` に「捨てました」が出る。終了時に待ちが打ち切られる |
| B20 | 壊れた設定: helper.json の `Steps` に存在しないキー、`Mods` の不正、`Type` の不正、JSON の構文エラー | その手順だけ飛ばして残りを実行／その動作を無視。`tray.log` に理由が出る。構文エラーの helper.json は「設定なし」として扱われ、トレイは落ちない |
| B21 | 古いドライバ（P0 より前の kbdlayer）があれば: トレイを動かしたまま `Actions` を足す | `tray.log` に「WAIT_HELPER_EVENT に対応していません」が1回出て、以後は静か。`Triggers` は動く（なければ未実施で可） |
| B22 | 回帰: P0（HLP の `--watch-helper`）、P1（F13 などの出力）、P2（A3・A9 の短いもの、IME 連動）、一時停止（T10） | 既存どおり |

## 静的にしか確認していない点
- C# のコンパイルと、単体テストの合否（全部）。
- `CancelSynchronousIo` で保留中の WAIT_HELPER_EVENT を取り消せること（ドライバ側は IoCsq でキャンセルに対応している。B15・B16 で確認）。
- 低レベルフックのタイムアウト（フックの中は表を引くだけだが、`GetAsyncKeyState` を修飾キー分呼ぶ）。重い環境でフックが外されないか。
- `Absolute` のマウス座標が複数モニターで正しいか（B6）。
- `SendInput` のスキャンコード入力が、一部のアプリ（ゲームなど）で効くか。

## 試験担当への注意
- HLP は `Actions` が1つでもあるときだけトレイが待つ。helper.json を書いたら、3〜4 秒待ってから押す。
- F13 のトリガーの試験では、F13 を出す手段（P1 の `KC_F13` をレイヤーマップに置く）が要る。注入先のキーボードの `Enabled` に注意（T-20）。
- 試験の後は、helper.json・tray.json の `Helper`・`tray-mapoff.json` を消す。

## チェックリストに照らした確認
- ドライバの変更は無い。
- 外の状態を変える前に戻す手段を用意する（write-ahead）という考え方は、ここでは `Hold`（解放で必ず Off）と、キーフックの飲み込みの記録（解放の対応）に当たる。トレイが落ちると、ドライバが `AppLayerMask` を戻す（D-8）。マウスのボタンを押したままになる `Down` は、`ReleaseSteps` が実行されないまま落ちると残りうる（B7 で確認）。
