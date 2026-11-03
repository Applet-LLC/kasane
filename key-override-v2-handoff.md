# 申し送り: キー置換の拡張（128 件、置き換え先にレイヤー操作・ヘルパー動作）の試験

対象ブランチ: `feature/key-override-v2`（作業ツリー `C:\Users\applet\Documents\GitHub\LayerDriver-ko2`。**`main`（`fafb8a9`）から分岐**）。注意: `main` には、`feature/release-1.01` の最近の変更（ライセンスの署名のサーバー化、バージョン 1.01、Release のドライバでのキー送出の有効化など）が、まだ入っていない。このブランチのビルドは、それらを含まない。**ドライバ（kbdlayer.sys）を入れ替える必要がある**ため、試験の前にユーザーが Release|x64 のテスト署名ビルドを用意する（Subscription 版と Pro 版がある）。kasane・KeymapTool・トレイも同じブランチのビルドを使う。コミットは、まだしていない（ユーザーの確認待ち）。

## 何を確かめたいか

Emacs 風のキー操作（のどかの `emacsedit.nodoka` 相当）を作るための、段階 1。次の 2 点を、ドライバで確かめる。

1. キー置換の上限を 32→128 件にした。旧ドライバが書いた 32 件分の保存値（384 バイト）が、アップグレードで消えないこと。
2. キー置換の置き換え先に、`OSL(n)`・`TG(n)`・`TO(n)`・`DF(n)`（レイヤー操作）と `HLP(n)`（ヘルパー動作）を指定できる。キーは出力せず、レイヤーのキーに同じ動作を割り当てたのと同じ処理をする。例: Ctrl+X → `OSL(1)`（次の 1 キーだけレイヤー 1。C-x の前置キー）。

## 変更の概要

- `driver/common/kbdlayer_ioctl.h`: `KBDLAYER_MAX_KEY_OVERRIDE_COUNT` を 128 に。
- `driver/kbdlayer/LayerEngine.c`:
  - 保存値 `KeyOverrides` は、1 件 12 バイトのまま件数だけ増え、1536 バイトになる。読み込みと、キーボード間の設定コピー（`CopyPersistedSettings`）は、旧サイズ 384 バイトも受け付ける。
  - `IsValidKeyOverrideEntry`: 置き換え先に `ONESHOT_LAYER`・`TOGGLE`・`LAYER_TO`・`DEFAULT`（レイヤー番号 0〜15、Param2 は 0）と `CUSTOM`（Param1 は 0）を許す。`Flags` は 0（REPEAT はマクロだけ）。
  - TG・DF・OSL・TO・CUSTOM の処理を `ApplyStateActionLocked` に切り出した。レイヤーマップのキー割り当て（`LayerEngineShouldDropEntry`）と、キー置換（`ResolveModdedKey`）の両方が呼ぶ。**既存の動作は変えていない**（機械的な置き換え）。ただし 1 点だけ変えた: OSL が次のキーを待っている間に、別のレイヤーの OSL が来たとき、前のレイヤーを先に落とす（以前は、前のレイヤーのビットが残った）。
  - キー置換が `HLP` のとき、押下の記録に動作を残し、リピートと解放を、ヘルパーイベントとして通知する。ほかの置き換え先（OSL・TG・TO・DF）は、リピートと解放を飲み込む（記録は `SWALLOWED`）。条件の修飾キーは、離さない（何も出力しないため）。
- KeymapTool: `--set-key-override` の置き換え先に、上の動作を許す。Id は 0〜127。
- kasane: 件数の定数、保存値の大きさ、置き換え先の検証（`ProjectService.KeyOverrideToEntry`）、存在しないレイヤーを指す置き換え先のエラー。

## 前提の確認（最初に）

1. インストールされているドライバが、このブランチのビルドか。ファイルのバージョンが同じでも、置き換わらないことがある（dev-notes の H-5）。`KeymapTool --set-key-override <ID> 127 MOD_LCTL KC_X KC_A` が成功し、`--get-key-override <ID> 127` で読めれば、新しいドライバ。旧ドライバでは Id 32 以上が失敗する（Win32 エラー 87）。
2. Driver Verifier（標準設定）が `kbdlayer.sys` に有効か。

## 試験1: 件数と保存

| No | 内容 | 期待 |
|---|---|---|
| K1 | Id 0・31・32・127 に、違う内容のキー置換を設定して読み出す | すべて設定・読み出しできる |
| K2 | Id 128 を設定する | 失敗する（87）。Id 127 までの設定は変わらない |
| K3 | 128 件すべてを設定し、再起動（またはキーボードの再接続: `pnputil /restart-device`）のあとに `--get-key-override` で全件を読む | 全件が残っている。`reg query` で `Parameters\LayerMaps\M_<ModelKey>` の `KeyOverrides` が 1536 バイト |
| K4 | **アップグレード**: 旧ドライバ（`main` のビルド、またはインストール済みの 1.01 のドライバ）で Id 0〜2 を設定し、新しいドライバに入れ替える（`KeyOverrides` が 384 バイトの状態から） | Id 0〜2 が残る。新しく Id 3 以降を設定すると、保存値が 1536 バイトに書き直される |
| K5 | 同型番 2 台での共有（`--set-scope`）、または「この 1 台だけ」への切り替えで、`CopyPersistedSettings` が動くとき、旧サイズの `KeyOverrides` をコピーできる（K4 の状態で） | コピー先でも読める（旧サイズのままコピーされる） |
| K6 | 既存の動作の回帰: Shift+2 → `@`、Shift+BS → Delete、`MACRO(n)` への置き換え（リピートの有無）、修飾付きキーの割り当てへの適用 | 変わらない |

## 試験2: 置き換え先がレイヤー操作・ヘルパー動作

準備: レイヤー 2 枚以上のキーマップ（`--apply-simple`、またはエディタ）。レイヤー 1 で、キー S を `KC_Z` に割り当てる。OIB 注入で、窓が受けたキーを記録する。

| No | 設定 | 操作 | 期待 |
|---|---|---|---|
| S1 | Ctrl+X → `OSL(1)` | Ctrl を押したまま X、続けて S（Ctrl は押したまま） | X は出力されない。S は、レイヤー 1 の `KC_Z` になる（Ctrl が掛かって Ctrl+Z）。次の S は、レイヤー 0 の `s`。`--get-layer-state` の ActiveLayerMask が、1 キーのあとにレイヤー 1 のビットを失う |
| S2 | 同じ設定 | Ctrl を押して X、Ctrl を離して S | S はレイヤー 1 の `KC_Z`（Ctrl なし）。その次の S は `s` |
| S3 | 同じ設定 | Ctrl+X のあと、次のキーを押さずに、もう一度 Ctrl+X | レイヤー 1 は、待ちのまま（残り続けない）。次のキーで解除される |
| S4 | Ctrl+X → `OSL(1)`、Ctrl+C → `OSL(2)` | Ctrl+X、続けて Ctrl+C、続けて S | レイヤー 1 は落ちている。S はレイヤー 2 で解決される。そのあと、レイヤー 1・2 とも落ちる（取り残しの確認。`--get-layer-state`） |
| S5 | レイヤー 1 の S を `LCTL(KC_S)`、キー置換に Ctrl+S → `LCTL(KC_F)` も置く | S1 と同じ操作 | **この組み合わせは、置き換えに再び当たる**（Ctrl+S が Ctrl+F になる）ことを確認して記録する。これは仕様（出力キーでキー置換の表を見る）。続けて、レイヤー 1 の S を `MACRO(n)`（Ctrl+S を打つマクロ）にしたとき、置き換えに当たらず Ctrl+S になることを確認する。**マクロの Ctrl が、物理で押している Ctrl を離したままにしないか**（実行後に `GetAsyncKeyState` で Ctrl が押されたままか、次のキーに Ctrl が掛かるか）も記録する |
| S6 | Ctrl+G → `TG(1)` | Ctrl+G を押したままにする（オートリピート）、離す | 押している間も 1 回だけ切り替わる。もう一度押すと戻る |
| S7 | Ctrl+T → `TO(1)`、Ctrl+D → `DF(1)` | 押して、`--get-layer-state` で確認 | TO: レイヤー 1 だけが有効。DF: 既定のレイヤーが 1 |
| S8 | Ctrl+K → `HLP(5)`（ヘルパー動作 5 に、押下・解放を記録する動作（例: テキスト入力）を置く）、トレイ動作中 | 押す、押したまま、離す | 押下で動作が動く（トレイの記録）。押している間のリピート、解放も通知される（`tray.log`）。キーは出力されない |
| S9 | S8 の状態で、Ctrl+K を押したままレイヤーマップを OFF にする（`--off`）、緊急解除 | 離す | 解放が通知され、押しっぱなしの状態が残らない（D-6） |
| S10 | Ctrl が押されていないとき | X、S、K を普通に打つ | 置き換えは発動せず、通常のキーが出る |

各項目で、実行後に Ctrl・Shift・Alt が押されたまま残っていないこと、メニューが開かないことも確かめる。

## 試験3: kasane（画面）と KeymapTool

1. エディタの「キー置換...」で、置き換え先に `OSL(1)`・`TG(1)`・`TO(1)`・`DF(1)`・`HLP(3)` を入力して、**適用**できるか。`OSL(5)`（存在しないレイヤー）はエラーになり、適用されないか。`MO(1)`・`LT(1, KC_A)`・`KC_NO` はエラーになるか（メッセージの文面を記録）。
2. 画面で Id 32 以上の項目を作れるか（一覧が 128 件まで出るか）。保存して開き直して、同じ内容か。
3. インポート／エクスポート: 129 件目は「空きが無い」警告になるか。128 件のファイルを、読み込めるか。
4. 古い KeymapTool（`main` のビルド、またはインストール済みのもの）で、新しいドライバの Id 0〜31 を読み書きできるか。置き換え先が `OSL` の項目を `--get-key-override` で読んだとき、Type=6 で表示されるか（古いツールは名前を持たない）。

## 試験4（余裕があれば）: 安全性

Driver Verifier（`/standard`）の下で試験 1〜2 を繰り返す。BSOD がないこと。S1〜S4 を高速に繰り返す（OIB で 200 回）。抜き差し（`pnputil /restart-device`）をしながら、`--set-key-override` を連発する。

## 気をつけること

- ユーザーのメモ帳や作業中のウィンドウには触れない。`tray.json` は退避して、終わったら戻す（ハッシュで確認）。昇格ランナーは終わったら止める。設定したキー置換は、試験の最後に全件を消す（`--set-key-override <ID> <Id>` を引数なしで、0〜127）。
- 結果は `key-override-v2-results.md` に書く。

## 試験5: Ctrl+K のマクロのあと、Ctrl が戻らない不具合の修正（`ctrl-k-modifier-restore-results.md` の再試験）

修正: `SendKeyOverrideMacro`（`LayerEngine.c`）が、マクロを送る前の `RealModsDown` を控え、送った後に条件の修飾キーの分を戻す。マクロの中に条件と同じ修飾キーがあると、そのbreakで `RealModsDown` のビットが落ち、Ctrl が戻らず、次のキー置換にも見えなくなっていた。

`ctrl-k-modifier-restore-results.md` の C1・C2・C6・X1・Y1 を、同じ手順（`m-ck1.ps1`、`m-ck2.ps1`）で繰り返す。期待:

| No | 期待 |
|---|---|
| C1 | マクロのあと `1dd` が出て、続く Z は Ctrl 付き |
| C2 | Ctrl+F の置き換え（`KC_RIGHT`）が発動する |
| C6 | 2 回目の Ctrl+K も置き換わる（普通の `k` にならない） |
| X1 | Ctrl が戻る |
| Y1 | Alt が戻る。Alt のあとの Menu 起動（単独の Alt 解放によるメニュー）が起きない |
| 回帰 | C3〜C5、W1・W2、Z1・Z2 は変わらない。最後に Ctrl・Shift・Alt・Win が押されたまま残らない |

加えて、マクロの実行中に物理の Ctrl を離す場合（Ctrl+K を押してすぐ Ctrl を離す、リピート付き `MACRO` の Param2=1 で押し続けて離す）で、Ctrl が OS で押されたままにならないこと。
