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

## 手動で撮る画面

VM または Windows Sandbox で撮る(手元の環境を汚さないため)。ウィンドウだけを撮る(Alt+PrintScreen、または Win+Shift+S)。ライセンスキーなどは、伏せるかダミーにする。

| 画面ID | 撮り方 |
|---|---|
| install-launcher | `Install-kasane.exe` を起動した直後 |
| install-launcher-failed | `.msi` を別のフォルダに移して起動する(または、失敗させた「インストール失敗」ダイアログ) |
| install-wizard-welcome / datadir / import / finish | MSI のウィザードの各画面。取り込みファイル無しの警告も、別途撮る |
| uninstall-off | エディタで OFF(素通しに戻す)を押した状態(昇格して撮る) |
| uninstall-apps | Windows の設定の「アプリと機能」で「かさね Subscription」を表示 |
| license-input / activated / diagnostics / trial / error | License Manager の各状態。キーは伏せる。エラーは、無効なキーやオフラインで起こす |
| tray-icons | 通知領域のアイコン 3 状態(動作中、一時停止中、ドライバに接続できない)を並べる |
| tray-menu / tray-layers | トレイの右クリックのメニューと、レイヤーのサブメニュー |
| tray-starting / tray-balloon | 「準備しています」の表示、バルーンの通知 |
| editor-applied / swapped / close-confirm / keyboard-list | 昇格したエディタで、キーを入れ替えて適用、閉じるときの確認、キーボードの一覧を開いた状態 |
| assign-power / assign-softkeyboard | 電源キーの確認、画面のキーボード |
| helper-assign / sequence / trigger | ヘルパー動作の割り当て、操作の手順、トリガーの編集画面 |
| apprules-editor / picker | 前面のアプリの規則、実行中のアプリから選ぶ |
| settings-about | インストール版の kasane で、設定のバージョン情報タブ。Debug ビルドで撮ると、ユーザー名入りのパスが写るので、自動撮影には含めない |
| settings-language-restart | 表示言語を変えて保存したときの確認 |
| nodoka-import / result | のどかの設定を取り込む画面と、変換の結果 |
