<div class="banner"><img src="../img/ja/editor-main-admin.png" alt="かさねのエディタの画面"></div>

<div class="title-row">
<h1 id="kasane">かさね ヘルプ</h1>
<img src="../assets/about.png" alt="かさね">
</div>

kasane（かさね）は、Windows のキーボードの割り当てを変えるツールです。キーの入れ替え、レイヤーの切り替え、長押しとタップで動作を変える Mod-Tap や Layer-Tap、マクロ、親指シフト（NICOLA）などを、キーボードごとに設定できます。

このヘルプは、画面の図を見ながら、インストールからアンインストールまでの全機能を説明します。

## 動作環境

- Windows 11 25H2（x64 または ARM64）
- インストールのための管理者権限
- インターネット接続。ライセンス管理サービスが約24時間ごとに Gumroad へ接続し、サブスクリプションが有効かどうかを確認します。直近の確認結果を使うので、ネットワークに接続できなくても、最大7日間はオフラインのまま使えます。

## はじめての方へ

1. [インストール](01-install.html)して、再起動します。
2. [ライセンスの認証](03-license.html)をします。
3. [エディタの基本画面](05-editor-basics.html)で、キーボードを選び、キーを入れ替えて、キーボードに適用します。
4. 困ったときのために、[緊急解除](07-emergency.html)と[アンインストール](02-uninstall.html)の手順を先に見ておくと安心です。

## 名称とファイル名

| 部品 | 名前 | ファイル、場所 |
|---|---|---|
| 製品名 | かさね（kasane） | インストール先は `C:\Program Files\applet\kasane` |
| エディタ | kasane | `kasane.exe`（`keymap-gui` フォルダ） |
| トレイ（常駐） | kasane-tray | `kasane-tray.exe`（`tray` フォルダ。ログオン時に自動起動）。UI アクセスのない版は `kasane-tray-nua.exe`（手動で起動する） |
| デバイスドライバ | kbdlayer | `kbdlayer.sys`（内部名とサービス名は `kbdlayer`。`kbdlayer` フォルダ） |
| ライセンス管理（License Manager） | kasane-license | `kasane-license.exe`（`license-gui` フォルダ） |
| ライセンスサービス | kasaneLicenseSvc（表示名 kasane License Service） | `kasane-license-svc.exe`（`license-svc` フォルダ）。ライセンスの記録は `%ProgramData%\kbdlayer-license` |
| ドライバの管理ツール | DriverManager | `DriverManager.exe`（インストール先の直下） |
| コマンド版の設定ツール | KeymapTool | `KeymapTool.exe`（インストール先の直下）。使い方は [17. KeymapTool とサンプル](17-keymaptool.html) |
| インストーラー | Install-kasane | `Install-kasane.exe`、`KasaneSetup-ja-JP.msi`、`KasaneSetup-en-US.msi` |
| ヘルプ | | `manual\ja\index.html`（インストール先の直下。エディタの **ヘルプ** と、トレイのメニューの **ヘルプ** から開く） |
| 設定の保存先 | | `%LOCALAPPDATA%\applet\kasane`（環境変数 `KASANE` で変えられる） |

## やりたいことから探す

| やりたいこと | 見る章 |
|---|---|
| インストールする。設定の保存先を決める | [1. インストール](01-install.html) |
| 以前の設定を、インストールのときに取り込む | [1. インストール](01-install.html)、[14. インポート／エクスポート](14-import-export.html) |
| アンインストールする | [2. アンインストール](02-uninstall.html) |
| ライセンスを認証する。試用版を使う | [3. ライセンスの認証](03-license.html) |
| 一時的にキーの入れ替えを止める | [4. トレイ](04-tray.html)（一時停止）、[5. エディタの基本画面](05-editor-basics.html)（OFF） |
| キーを入れ替える | [5. エディタの基本画面](05-editor-basics.html)、[12. 設定の例](12-recipes.html) |
| 別のキーボードを使う（配列を追加する） | [5. エディタの基本画面](05-editor-basics.html)（KLE から追加） |
| 設定したのに、違うキーが入れ替わる | [6. キャリブレーション](06-calibration.html) |
| 設定を間違えて、キーボードから入力できなくなった | [7. 緊急解除](07-emergency.html) |
| 短く押したときと、長押ししたときで、動作を変える | [8. キーの割り当て](08-key-assign.html)、[12. 設定の例](12-recipes.html) |
| キーを押している間だけ、別のキー配置にする | [8. キーの割り当て](08-key-assign.html)（レイヤー）、[12. 設定の例](12-recipes.html) |
| 決まったキーの並びを、1 つのキーで出す | [9. マクロ・コンボ・タップダンス・キー置換](09-advanced.html)（マクロ） |
| 2 つのキーの同時押しで、別のキーを出す | [9. マクロ・コンボ・タップダンス・キー置換](09-advanced.html)（コンボ） |
| Ctrl+◯◯ のような組み合わせを、別の動作にする | [9. マクロ・コンボ・タップダンス・キー置換](09-advanced.html)（キー置換）、[12. 設定の例](12-recipes.html) |
| 日本語入力（IME）に合わせて、キー配置を切り替える。親指シフトを使う | [10. IME連動とNICOLA](10-ime-nicola.html) |
| キーで、プログラムを起動する。文字列を入力する | [11. ヘルパー動作とアプリ別レイヤー](11-helper-apprules.html) |
| アプリごとに、キー配置を変える。ゲームの間だけ止める | [11. ヘルパー動作とアプリ別レイヤー](11-helper-apprules.html) |
| 画面の色や表示言語を変える | [13. 設定ダイアログ](13-settings.html) |
| 設定をバックアップする。別のパソコンへ移す | [14. インポート／エクスポート](14-import-export.html)、[15. バックアップと移行](15-backup-migrate.html) |
| コマンドで設定する。サンプルのキーマップを使う | [17. KeymapTool とサンプル](17-keymaptool.html) |
| のどか（nodoka）の設定を使う | [14. インポート／エクスポート](14-import-export.html) |
| うまく動かない。ログを見たい | [16. トラブル対応、既知の制限など](16-reference.html) |
| 使用許諾や、同梱しているソフトウェアのライセンスを確かめる | [19. 使用許諾](19-terms.html)、[20. サードパーティノーティス](20-third-party.html) |

## 章の案内

| 章 | 内容 |
|---|---|
| [1. インストール](01-install.html) | インストーラーの画面、設定フォルダの指定、設定ファイルの取り込み |
| [2. アンインストール](02-uninstall.html) | 手順、削除されるもの、残るもの |
| [3. ライセンスの認証](03-license.html) | License Manager、試用版 |
| [4. トレイ](04-tray.html) | 常駐するアイコン、一時停止、レイヤーの手動切り替え |
| [5. エディタの基本画面](05-editor-basics.html) | 画面の各部、キーの入れ替え、適用と OFF |
| [6. キャリブレーション](06-calibration.html) | キーの実測、入力確認 |
| [7. 緊急解除](07-emergency.html) | 誤った設定で入力できなくなったときの解除 |
| [8. キーの割り当て](08-key-assign.html) | 割り当ての画面、レイヤー、Mod-Tap、Layer-Tap |
| [9. マクロ・コンボ・タップダンス・キー置換](09-advanced.html) | 各エディタの使い方 |
| [10. IME連動とNICOLA](10-ime-nicola.html) | IME に合わせたレイヤーの切り替え、親指シフト |
| [11. ヘルパー動作とアプリ別レイヤー](11-helper-apprules.html) | プログラムの起動、文字列の入力、前面のアプリに合わせた切り替え |
| [12. 設定の例](12-recipes.html) | よくある設定を、手順つきで紹介する |
| [13. 設定ダイアログ](13-settings.html) | 全般、機能、タイミングなど各タブ |
| [14. インポート／エクスポート](14-import-export.html) | 設定の書き出し、読み込み、のどかの設定の取り込み |
| [15. バックアップと移行](15-backup-migrate.html) | 設定の置き場所、別のパソコンへの移行 |
| [16. トラブル対応、既知の制限など](16-reference.html) | トラブル対応、既知の制限、コマンドライン、環境変数、ログ |
| [17. KeymapTool とサンプル](17-keymaptool.html) | コマンドで設定する手順、サンプルのキーマップ、キーボードIDの算出方法 |
| [18. README](18-readme.html) | インストール先にある README |
| [19. 使用許諾](19-terms.html) | インストール先にある使用許諾契約書 |
| [20. サードパーティノーティス](20-third-party.html) | 同梱しているソフトウェアのライセンス |
