# VM での撮影(インストーラー、ライセンス、トレイなど)

インストール、アンインストール、ライセンス、トレイ、ドライバが動いた状態のエディタは、ホストでは撮れないので、Windows 11 の VM(HLK クライアント)で撮った(2026-10-22)。ここに、そのときの部品とジョブを残す。再撮影の手順の参考にする。

## 仕組み

1. ホストに、撮影専用の SMB 共有(`kasanevm`)と専用ユーザーを作る。ホストを変更するので、管理者が自分で実行する。
2. VM に、リモート デスクトップ(RDP)でつなぐ。ドライブの共有は使わない(ホストのドライブ全体が見えてしまう)。
3. VM のデスクトップで、共有の `out\agent.ps1` を起動する(Win+R に、`net use` と起動のコマンドを打つ)。`agent.ps1` は、共有の `jobs\*.ps1` を 1 つずつ実行する。
4. インストーラーやエディタは、昇格したウィンドウなので、昇格していないスクリプトからは操作できない。そのため、`jobs` の最初のジョブで、昇格した `agent2.ps1` を起動する(UAC の確認が出る)。以降は、`jobs2\*.ps1` を、昇格した側で実行する。
5. ジョブは、`vmlib.ps1` を読み込み、画面を撮って、共有の `out\img\` に保存する。

`vmlib.ps1` は、`kasane.Tests/UiAutomation/UiaLib.ps1` を、共有の `out\lib\` に置いて使う。

## 注意

- **ライセンスキーは、`<LICENSE_KEY>` に置き換えてある。** 実際に使うときは、撮影用のキーを入れる。サブスクリプションのキーは、画像に写さない(ユーザー名とキーの欄は、`SnapMasked` で塗りつぶす)。
- ライセンス画面には、購入時のメールアドレスが出る。必ず `SnapMasked` で塗りつぶす。
- Windows の設定の画面は、左のプロフィール欄に、アカウント名とメールアドレスが出る。右側の内容だけを切り出す。
- (修正前のトレイの動作。2026-10-22 に修正済み。古い版のトレイで撮り直すときのための記録) RDP のセッションでは、トレイが「操作中のコンソールではない」と判断して、キーボードのリマップを OFF にする。トレイのメニューを撮るときは、設定の「トレイが動いている間だけ、キーのリマップを有効にする」を OFF にしてから、キーボードに適用し直す(`jobs\52-remap-setting-off.ps1`)。
- 以前のテストの設定が残っていると、エディタを開いたときに読み込まれて、適用されてしまう。撮影の前に、`%LOCALAPPDATA%\LayerDriver` と `%LOCALAPPDATA%\applet\kasane` を確かめる(`jobs\45-clean-state.ps1`)。
- 撮影が終わったら、`jobs\70-cleanup.ps1` で、作業用のフォルダを消して、再起動する(ドライバの削除が、再起動後に完了する)。
- ホストの共有は、撮影のあとに、`teardown-share.ps1` で削除する(共有、ユーザー、フォルダ)。

## ジョブの順序

| 番号 | 内容 |
|---|---|
| 10 | 配布物を、VM のローカルにコピーする |
| 13、16〜17 | インストーラーの起動画面と、失敗の画面 |
| 20〜26 | ウィザードの各ページ。設定ファイルの取り込みの警告。インストールの実行 |
| 27 | インストール後の確認(スタートメニュー、サービス、自動起動) |
| 30〜34 | License Manager。エラー、試用版、認証済み(キーは塗りつぶす) |
| 35〜36 | バージョン情報、表示言語の確認、実行中のアプリの一覧 |
| 41、52〜54 | トレイのアイコン、メニュー、レイヤー、一時停止、「準備しています」 |
| 43、48 | キーの入れ替えと適用(クリアしてから) |
| 55 | のどかの設定の取り込み |
| 62 | 閉じるときの確認(管理者) |
| 63、65、66 | アンインストール(OFF、設定の画面、実行) |
| 70 | 後片付け |


## 1.02 の撮り直し（`jobs-1.02\`。日本語と英語）

1.02 で、画面が変わった（版の表記 1.02、エディタのボタンの並び、ろキーの表示「_」、トレイの「設定/エディタ」、インストーラーの「参照...」、使用許諾の見出し）。日本語は変わった分だけ、英語は全画像を撮る。`jobs-1.02\` は、上の表のジョブを、日本語と英語の両方で動くように作り直したもの（言語の切り替えは、`vmlib.ps1` の `T '日本語' 'English'` と、共有の `out\lang.txt`）。上の `jobs\` は、2026-10-22 の記録として残してある。

**まだ VM で動かしていない。** 最初の 1 回は、`results\*.log` を見て、止まったジョブを直しながら進める（画面の文言を、名前で探しているところが多い）。

### 撮る画像

| 言語 | 画像 | 撮るジョブ |
|---|---|---|
| ja、en | `editor-main`、`editor-readonly` | `90-capture-manual`（昇格していない。表示倍率 150%） |
| ja、en | `editor-main-admin` | `91-capture-manual-admin`（昇格。表示倍率 150%） |
| ja、en | `editor-keyboard-list`、`editor-swapped`、`editor-applied` | `48-editor-swap` |
| ja、en | `editor-close-confirm-admin` | `62-close-confirm`（`editor-close-confirm-readonly` は、昇格していないエディタで手動） |
| ja、en | `tray-menu`、`tray-menu-paused`、`tray-layers`、`tray-strip-*` | `53-tray-shots` |
| ja、en | `install-wizard-welcome`、`-license`、`-features`、`-datadir` | `20-wizard-pages` |
| ja、en | `install-wizard-import`、`-import-filled`、`-import-missing`、`-ready` | `22-import-pages` |
| ja、en | `install-wizard-progress`、`-finish` | `26-install` |
| ja、en | `settings-about`、`settings-language-restart` | `35-about-language` |
| ja、en | `uninstall-off-raw`（`uninstall-off` の元） | `63-uninstall-off` |
| en のみ | `install-launcher`、`install-launcher-failed` | `13-launcher` |
| en のみ | `license-input`、`license-error`、`license-trial`、`license-activated` | `30`〜`32` |
| en のみ | `tray-starting`、`apprules-picker`、`nodoka-import`、`nodoka-result`、`uninstall-confirm` | `54`、`36`、`55`、`66` |
| en のみ | `90`・`91` の、残りの画面（`-Only` を付けない） | `90-capture-manual`、`91-capture-manual-admin` |

日本語で、リポジトリの画像に置き換えるのは、1.02 で見た目が変わった次の 15 枚（と、そこから作る派生画像 3 枚）だけ: `editor-main`、`editor-readonly`、`editor-main-admin`、`editor-keyboard-list`、`editor-swapped`、`editor-applied`、`editor-close-confirm-admin`、`editor-close-confirm-readonly`（手動）、`tray-menu`、`tray-menu-paused`、`tray-layers`、`install-wizard-license`、`install-wizard-datadir`、`settings-about`、`uninstall-off-raw`。派生画像は、`editor-main-labeled`、`editor-statusbar`、`uninstall-off`。
同じジョブが、ほかの画像も撮るが、日本語では置き換えない（`install-launcher` など、1.02 で変わっていないため）。`uninstall-apps` と `uninstall-apps-menu` は、Windows の設定の画面なので、英語のヘルプも日本語の画像を使う（`65-apps-shot` は、日本語のときだけ撮る）。

撮ったあとに、ホストで作る派生画像: `pwsh docs/manual/annotate-images.ps1 -Lang <言語>` が、`editor-main-labeled`、`editor-statusbar`、`tray-icons`、`uninstall-off`、`license-diagnostics` を作る。editor-main の座標は、1902x891（表示倍率 150%）の画像に合わせてある。

### 準備

ホストでは、昇格した PowerShell で、`setup-share.ps1` を実行する。`C:\kasanevm` に、撮影専用の共有 `kasanevm`（専用ユーザーだけがアクセスできる。ドライブ全体は公開しない）と、`out\` などのフォルダを作り、下の表のファイルのうち、スクリプト類を置く。専用ユーザーのパスワードは、最後に 1 回だけ表示する（保存しない）。ジョブの中の、ホストのアドレス（`100.64.1.22`）は、実際のアドレスに置き換えて写す（リポジトリのファイルは変えない）。撮影が終わったら、`teardown-share.ps1` で、共有とユーザーを消す（`-RemoveFolder` でフォルダも消す）。

```
.\setup-share.ps1 -DistFolder <1.02 の配布物のフォルダ>
```

**配布物は、1.02 でビルドし直したものを使う。** `setup\dist\kasane-subscription` に、以前のビルド（Install-kasane.exe が 1.0.0.0、使用許諾が 1.01）が残っていることがある。

共有の `out\` には、次が置かれる（`setup-share.ps1` が置かないものは、手で置く）。

| 場所 | 中身 |
|---|---|
| `lib\vmlib.ps1` | このフォルダの `vmlib.ps1` |
| `lib\UiaLib.ps1`、`lib\Capture-Manual.ps1` | `kasane.Tests\UiAutomation\` の同名のファイル |
| `dist\` | 1.02 の配布物（Subscription 版: `Install-kasane.exe`、`KasaneSetup-ja-JP.msi`、`KasaneSetup-en-US.msi`） |
| `keys\trial.txt`、`subscription.txt`(無効なキーは、`30` に偽のキーを書いてある) | 撮影用のキー（1 行に、キーだけ）。スクリプトにも、画像にも、キーは残さない（License Manager の画像は、塗りつぶす） |
| `lang.txt` | `ja` か `en`（無ければ `ja`） |
| `only.txt`（任意） | `90`・`91` で撮る画面の前方一致（カンマ区切り）。無ければ、日本語は変わった分だけ、英語は全部 |

画像は、`out\img\<言語>\` に保存される。

### 手順

1. VM を、何も入っていない状態にする（以前の kasane を、アンインストールして、再起動済み）。表示倍率は、100%。
2. `out\lang.txt` に、`ja` か `en` を書く。
3. 昇格したエージェント（`agent2.ps1`）で、`10`、`13`、`20`、`22`、`26`、`28` を、`out\jobs2\` に 1 つずつ入れて実行する（名前順に動く。止まったら、ログを見て直す）。
4. 表示倍率を 150% にして、昇格していないエージェント（`agent.ps1`）で、`90-capture-manual` を、`out\jobs\` に入れて実行する。
5. `agent2.ps1` で、`91-capture-manual-admin` を実行する。表示倍率を 100% に戻す。
6. `agent2.ps1` で、`30`〜`32`（License Manager）、`35`、`36`、`41`、`45`、`48`、`53`、`54`、`55`、`62`、`63`、`65`、`66` を順に実行する。
7. `70-cleanup` で、後片付けと再起動をする。
8. ホストで、`out\img\<言語>\` から、上の表の画像を、`docs/manual/img/<言語>/` へ写し、`annotate-images.ps1` を実行する。画像を見て、個人情報（アカウント名、メールアドレス、キー、デバイス ID）が写っていないことを確かめる。

英語の画像ができたら、原稿（`src/en/*.md`）の `../img/ja/…` を `../img/en/…` に変える（撮っていない画像は、日本語のまま）。
