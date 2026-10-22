# マニュアルの画像の撮り方

画像は `docs/manual/img/<言語>/<画面ID>.png`。原稿(`src/<言語>/*.md`)の `![…](../img/ja/<画面ID>.png)` が、このファイル名を参照している。

## 自動で撮る画面

```
pwsh kasane.Tests/UiAutomation/Capture-Manual.ps1            # すべて(日本語)
pwsh kasane.Tests/UiAutomation/Capture-Manual.ps1 -Only settings-,impexp-
pwsh kasane.Tests/UiAutomation/Capture-Manual.ps1 -Lang en   # 英語(img/en/)
```

- 前提: kasane を Debug|x64 でビルドし、kasane・トレイを終了してから、昇格していない PowerShell で実行する。
- 設定フォルダ(環境変数 KASANE)は `C:\kasane-data` の一時フォルダになり、終わると削除される。すでに同名のフォルダがあるときは、実行しない。
- ドライバが動いている PC では、キーボードの一覧に、実際のキーボードの名前と ID が写る。公開前に確認すること。
- 撮影される画面ID: editor-main, editor-readonly, editor-kle, assign-main, assign-layer, assign-modtap, macro-editor, combo-editor, tapdance-editor, keyoverride-editor, calib-main, settings-general, settings-features, settings-timing, settings-scope, settings-datadir, settings-i8042, settings-desktop, settings-dark, emergency-tab, emergency-check, ime-tab, nicola-section, helper-tab, apprules-tab, nicola-editor, impexp-export, impexp-import, impexp-clear

## 番号を描き込む画像

`pwsh docs/manual/annotate-images.ps1` が、`editor-main.png` に番号の丸を描いた `editor-main-labeled.png` を作る（05 章の「画面の各部」の図）。座標は、1902x891 の画像に合わせてある。撮り直して大きさが変わったら、`annotate-images.ps1` の座標を直す。

## 管理者モードの画面（自動）

昇格した PowerShell で `Capture-Manual.ps1 -Admin` を実行する（昇格したウィンドウは、昇格していない UIA から操作できないため）。UAC の確認が出る場合は、許可する。撮影される画面ID: editor-main-admin, calib-main-admin, calib-single, calib-all, settings-scope-admin, settings-i8042-admin, settings-datadir-admin, emergency-tab-admin, emergency-check-admin。キーボードへの適用や、緊急解除の反映は行わない。

## VM で撮った画面(2026-10-22)

インストーラー、アンインストール、ライセンス、トレイ、ドライバが動いた状態のエディタ(適用、キーの入れ替え、閉じるときの確認)、実行中のアプリの一覧、のどかの取り込みは、Windows 11 の VM で撮った。手順、注意点、ジョブは、`vm-capture/README.md` にある。

| 画面ID | 内容 |
|---|---|
| install-launcher、install-launcher-failed | インストーラーの起動画面。空の MSI を置いて失敗させた画面 |
| install-wizard-welcome、license、features、datadir、import、import-filled、import-missing、ready、progress、finish | MSI のウィザード |
| license-input、license-error、license-trial、license-activated、license-diagnostics | License Manager(ユーザー名とキーは、塗りつぶし済み) |
| tray-menu、tray-menu-paused、tray-layers、tray-starting、tray-icons | トレイ。`tray-icons` は、`annotate-images.ps1` で `tray-strip-*` から作る |
| editor-keyboard-list、editor-swapped、editor-applied | 管理者のエディタで、キーを入れ替えて適用する |
| editor-close-confirm-readonly、editor-close-confirm-admin | 閉じるときの確認 |
| settings-about、settings-language-restart | インストール版のバージョン情報。表示言語を変えたときの確認 |
| apprules-picker | 実行中のアプリから選ぶ(VM のウィンドウだけが並ぶ) |
| nodoka-import、nodoka-result | のどかの設定の取り込み |
| uninstall-off、uninstall-apps、uninstall-apps-menu、uninstall-confirm | OFF にした状態(`uninstall-off-raw` から切り出す)。設定の「インストールされているアプリ」(右側だけ)。アンインストールの確認 |

## 撮れていない画面

- トレイのアイコンの「ドライバに接続できません」の状態。ドライバを止められなかった。
- トレイのバルーン(通知)。失敗の通知を起こせなかったので、原稿から画像を外した。
- アンインストール中の「使用中のファイル」の画面。撮った画像の領域がずれたので、使わない(内容は、原稿に文章で書いた)。
- インストール時の「再起動してから、もう一度」のエラー(1722)。
