# Sequence / Text の IME ON/OFF 制御 申し送り: 実機の試験

- 対象: `main` の `10d524c`（マージコミット。実装は `5f3bd78`、ブランチ `feature/ime-control-steps`）。**ドライバ（kbdlayer.sys）は変更していない。** 変更は kasane（GUI）と kasane-tray だけ。
- 書いた日: 2026-10-31（開発担当）。
- 試験には、**日本語 IME（Microsoft IME）を使える環境**が要る。ユーザーが一度ビルド・インストールして、Sequence と Text で IME 操作が動くことは目視で確認済み。ここでは、方式ごと・アプリごとの確認と、失敗時の扱いを見てほしい。

## 何が問題で、何を足したか
日本語環境で IME が ON のとき、Sequence の `key`（スキャンコード）は、かな入力や変換の対象になり、意図した文字にならない。英語環境では起きない。

- Sequence に `ime on|off|toggle [方式] [abort]` の手順を足した（`HelperStep.Ime` / `ImeMethod` / `ImeOnFail`）。IME の今の状態を IMM32 で読み、目標と同じなら何もしない。違うときだけ変える。
- Text に `ImeOffDuring`（入力の間だけ IME を OFF にして、終わったら戻す）、`ImeMethod`、`ImeOnFail` を足した。Text の文字送出は `KEYEVENTF_UNICODE` のままで、変えていない。
- 実装は `kasane-tray/ImeController.cs`。TSF の書き込みは `kasane-tray/TsfImeWriter.cs`。

### 方式

| 書き方 | 動作 |
|---|---|
| 書かない（自動） | IMM32（IME ウィンドウへ `IMC_SETOPENSTATUS`）で変える。だめなら IME キー（`VK_IME_ON` / `VK_IME_OFF`）を送る。**フォールバックするのはこれだけ** |
| `imm` | IMM32 だけ |
| `key` | IME キーだけ。状態を読めなくても送る（冪等） |
| `altkanji` | Alt+半角/全角（`VK_KANJI`）だけ。切り替えのキーなので、状態を読めないときは送らない |
| `tsf` | TSF のグローバルコンパートメント `KEYBOARD_OPENCLOSE` への書き込みだけ（実験的） |

方式を書いた行は**固定**。失敗しても別の方式へ切り替えない。失敗時は、既定で `tray.log` に残して続行。`abort` を付けると、その動作の残りをやめる。変えたあとは IMM32 で読み直して確かめる（`key` と `tsf` は、読み直しでも状態が取れなければ、送ったことで成功とする）。

## 準備
- ビルド: `main` を kasane / kasane-tray ともビルドしてインストールする。`dotnet test kasane.Tests` は 863 件通過（IME まわりは `HelperImeTests`、18 件）。
- 設定の書き方は、GUI（設定画面のヘルパー動作。Sequence の入力欄の上の **IME 操作を挿入 ▾**、Text の **入力の間だけ IME を OFF にして、終わったら元に戻す**）か、`helper.json` / `tray.json` の `Helper` を直接書く。
- 手順の書式の例:
  - `ime off` / `ime on` / `ime toggle`
  - `ime off key`、`ime on tsf`、`ime off altkanji abort`
- 前面アプリの IME の状態は、`kasane-tray.exe --probe-ime`（IMM32 の診断）で読める。試験の前後に比べる。
- 注入は、`input-injection-testing` の手法（OIB。スロット 0）で、トリガーか HLP を押す。トリガーは `Helper.Triggers` に書く（KeepMods の試験と同じ要領）。
- 設定の保存先は、環境変数 `KASANE` があればそちら（keepmods-results.md の環境欄を参照）。試験後は元の設定に戻す。

## 試験項目

IME の状態は、各項目の前に **手で揃える**（ON または OFF）。前面は、特に断りが無ければメモ帳。

| # | 内容 | 期待値 |
|---|---|---|
| IM1 | IME ON で、手順 `ime off` → `key KC_A` を実行 | IME が OFF になり、`a` が入る（`あ` や下線付きの未確定にならない） |
| IM2 | IME が**すでに OFF**で、同じ手順 | 何も変わらない。`tray.log` にエラーが出ない。`a` が入る |
| IM3 | IME OFF で `ime on` | IME が ON になる。すでに ON なら変わらない |
| IM4 | `ime toggle` を、ON から、OFF から、それぞれ実行 | 反対になる。2 回で元に戻る |
| IM5 | 方式の固定を、`imm`、`key`、`altkanji`、`tsf` それぞれで `ime off`（IME ON から） | 各方式が効くか／効かないかを記録する。**効かない方式は、別の方式にならずに失敗する**（`tray.log` に理由が 1 行出る。別の方式でキーが飛ばない） |
| IM6 | IM5 の `altkanji` を、IME の状態が読めないウィンドウ（下の IM9）で実行 | 半角/全角のキーが**送られない**（トグル動作のキーを、状態を読めずに送らない） |
| IM7 | 自動（方式を書かない）で、IMM32 が効かないウィンドウで `ime off` | IME キーにフォールバックする（`tray.log` に「IMM32 で変えられなかったため、IME キーで試します」） |
| IM8 | 失敗時の扱い: IMM32 が届かないウィンドウ（IM9）で `ime off imm abort` → `key KC_A` | `a` が**入らない**（残りをやめる）。`tray.log` に「IME を切り替えられなかったため、残りをやめました」。`abort` なし（`ime off imm`）なら `a` が入る |
| IM9 | 届かないはずのウィンドウ: 管理者として動くメモ帳（uiAccess 版のトレイでは届く可能性がある）、Windows ターミナルの管理者、ストアアプリ | どう動いたかを記録する（届く・届かない、各方式）。**届かないときに固まらない／トレイが落ちない** |
| IM10 | 主要アプリでの `ime off`（IME ON から）: メモ帳（新旧）、Edge、Chrome、VS Code、Word、Windows ターミナル | アプリごとに、各方式の可否を表にする。特に `tsf` と `altkanji` |
| IM11 | Text: `ImeOffDuring` を ON にして、IME ON の状態で `Apple 漢字` を入力 | 文字がそのまま入る。**入力のあと IME が ON に戻る**。IME が元から OFF なら、OFF のまま |
| IM12 | Text: `ImeOffDuring` を OFF（既定）で、IME ON の状態で同じ文字列を入力 | 入るか、崩れるかを記録する（アプリごと）。崩れるアプリがあれば、IM11 で直ることを確認する |
| IM13 | Text: `ImeOffDuring` で、入力の途中に前面のウィンドウを切り替える（Alt+Tab。入力が長いときに） | 切り替え先の IME は**変わらない**（前面が変わったときは戻さない。`tray.log` に「前面のウィンドウが変わったため、元の状態へは戻しません」） |
| IM14 | Text: `ImeOffDuring` + 固定方式（`Imm`、`Key`、`AltKanji`、`Tsf`）+ 失敗しやすいウィンドウ（IM9）。`ImeOnFail` は既定と `Abort` | 既定は入力される。`Abort` は入力されない。どちらも、そのあと IME の状態が壊れていない |
| IM15 | 設定の検証: GUI で Text の `ImeMethod` を不正にする、または `helper.json` に `"Ime": "maybe"`、`"ImeMethod": "ouija"`、`"ImeOnFail": "retry"` を書く | GUI／設定の検証がエラーを出す。トレイが落ちない |
| IM16 | トリガーと修飾キー: `Ctrl+F1` のトリガーで `ime off` → `key KC_A` | Ctrl が掛からない（`ime off` の前後でも）。`a` が入る |
| IM17 | 回帰: 既存の Sequence（key、down、up、text、delay、mouse、move、wheel）と Text | 従来どおり |
| IM18 | 回帰: IME 連動（`ImeLayers`）を有効にして、`ime off` / `ime on` の手順を実行 | レイヤーが IME の状態に追従する（手順で変えた IME の状態を、連動が拾う） |
| IM19 | 耐久: `ime off` / `ime on` を交互に 200 回（Verifier 下の環境があれば有効のまま） | トレイが落ちない・ハングしない。入力が正常。`tray.log` のエラーが出続けない |
| IM20 | 画面ロック・リモートデスクトップ切断中に、手順を実行 | 実行されない（既存の「操作中のセッションだけ実行」のまま）。戻ったあと、IME の状態が不整合になっていない |

## 静的にしか確認していない点
- Windows 11 の新しい Microsoft IME で、`IMC_SETOPENSTATUS` が全アプリに効くか。**ユーザーの環境では動いたが、アプリ別には未確認。**
- TSF の書き込みが前面アプリの IME に効くか。以前の調査（`docs/ARCHITECTURE.md`「TSF で IME の状態を読めるかの調査」）では、別プロセスのトレイからは TSF の値が空で読めなかった。書き込みが効かない可能性が高い。**効かなければ、その結果を残してほしい**（方式の記述を見直す）。
- Google 日本語入力など、Microsoft IME 以外。環境があれば IM10 を見てほしい。無ければ「未実施」でよい。
- ARM64。ビルド・実機の環境が無ければ「未実施」でよい。
- 英語版マニュアルは未作成（日本語版のみ更新）。

## 試験担当への注意
- `tray.log` の IME まわりの行は `IME(IMM32):`、`IME(AltKanji):`、`IME(TSF):`、`IME:` で始まる。FAIL の根拠に、該当行を添える。
- IME の状態は、試験の途中で手を触れると変わる。各項目の直前に揃える。
- `altkanji` は切り替えのキーなので、IME の状態を読めない環境で誤って送ると、状態が逆になる。IM6 は、送られていないことを `tray.log` と IME の状態で確かめる。
- 試験後、`helper.json` / `tray.json` と IME の状態を元に戻す。

## 結果
`ime-control-results.md` に PASS／FAIL／未実施で書く。FAIL は、再現手順と `tray.log` の抜粋、`--probe-ime` の出力を添える。IM5 と IM10 は、方式×アプリの表にする。試験の手法で新しく分かったことは、dev-notes にも追記する（由来にこのファイル名を書く）。
