# 英語版の翻訳メモ（README・ヘルプ）

英語版の README とヘルプは、最初に日本語の文章から訳した。そのため、**画面に実際に出る英語の文言と食い違う**箇所があった。2026-11-17 に点検して直した。その記録と、次に翻訳するときの注意をまとめる。

## 翻訳するときの原則

1. **画面の文言は、日本語から訳さない。実際の英語表示をそのまま写す。** 画面の英語は、コードの `Loc.T("日本語", "English")` にある。
2. 一覧は `pwsh tools/extract-ui-strings.ps1 -Out <file>` で作れる（`-Filter 語` で絞り込める）。インストーラーの文言は `setup/Product.en-us.wxl`、ライセンス管理は `kasane-license/Properties/Strings.resx`。
3. メニュー・ボタン・タブ・ウィンドウの題名は、**大文字小文字・複数形・末尾の「...」まで**英語表示と同じにする（例: `Layers`、`Settings...`）。
4. README のようにヘルプより短い文書でも、英語版に**日本語の語を残して「(英訳)」を添えない**。英語表示のモードで使う人には読めない。固有名（のどか、窓使いの憂鬱、天狼）の由来表記だけは例外。
5. 日本語の README・ヘルプを直したら、英語版も同じ箇所を直す。**「まだ英語版が無い」のような状態の記述は、状態が変わったときに消し忘れやすい**（今回も残っていた）。
6. Windows 自体の画面の名前は、Windows 11 の英語表示に合わせる（「アプリと機能」→ `Installed apps`。`Apps & features` は Windows 10 の名前）。
7. 英語版ヘルプを `docs/manual/src/en/` で直したら `pwsh docs/manual/build-manual.ps1 -Lang en` で `docs/manual/en/` を作り直す。

## 点検の方法（再現用）

- `extract-ui-strings.ps1` で英語の文言を集め、README とヘルプの原稿から**太字・引用符の語**を取り出して突き合わせた。部分一致だけだと見逃すので、**完全一致**でも調べ、外れたものを目で確認した。
- 動的な文言（`{id}` などを含むボタン）や、Windows・WiX 標準の文言（`I accept the terms...`、`Run as administrator`）は、外れても正しい。

## 直した箇所（2026-11-17）

### README（`setup/dist-readme/` と `setup/dist-readme-pro/` の `README.en-US.txt`）

| 場所 | 直す前 | 直した後 | 理由 |
|---|---|---|---|
| トレイの右クリックのメニュー | `設定 (Settings)`、`一時停止 (Pause)`、`レイヤー (Layer)`、`ライセンス (License)`、`バージョン情報 (About)`、`終了 (Exit)` | `"Settings/Editor"`、`"Pause"`、`"Layers"`、`"License"`、`"Help"`、`"About"`、`"Exit"` | 日本語のままだった。実際は `Settings/Editor`（1.02 で改名）と `Layers`（複数形）で、`Help` もある（Pro 版は `License` なし） |
| NICOLA の ON/OFF の場所 | `設定 (Settings) > IME 連動 (IME link)` | `"Settings..." > "IME link" tab` | 日本語が残っていた。設定はボタン `Settings...` から開くタブ |
| ヘルパー動作の作成 | `"ヘルパー動作" (Helper actions)` in key-assignment screen | `"Helper actions"` in `"Assign key"` window | 同上。画面の題名は `Assign key` |
| F13-F24 などのトリガー、アプリ別レイヤー | `設定 > ヘルパー動作`、`設定 > アプリ別レイヤー` | `"Settings..." > "Helper actions" tab`、`... > "Per-app layers" tab` | 同上 |
| インポート／エクスポート | `"インポート／エクスポート..." (Import / Export...)` | `"Import / Export..."` | 同上 |
| ヘルプの項 | `"ヘルプ" (Help)` ボタン、英語版は無いと記述 | `"Help"` ボタン、トレイの `"Help"`、英語版があり表示言語で開くと記述 | 日本語が残っていた。1.02 で英語版を追加したのに「無い」と書いたままだった |
| アンインストール | `Apps & features` | `Installed apps` | Windows 11 の名前 |
| トレイの左クリック | `opens the kasane settings dialog` | `opens the kasane editor` | 実際はエディタが開く（ヘルプ 04 章と同じ） |

日本語版 README も、同じ内容のずれを直した（右クリックのメニューを `設定/エディタ`・`ヘルプ` に、左クリックを「エディタを開きます」に、ヘルプの英語版が無いという記述を削除）。

### ヘルプ英語版（`docs/manual/src/en/`）

| 章 | 直す前 | 直した後 | 実際の英語表示（出所） |
|---|---|---|---|
| 05 | `Restore this layer's assignment`（2か所） | `Reset assignment on this layer` | キーの右クリックのメニュー（`MainWindow.xaml.cs`。末尾に `(戻し先)` が付く） |
| 14 | `Open the output location in Explorer` | `Open output folder` | `ImportExportWindow.cs` |
| 14 | `Import with these contents` | `Import this` | `NodokaImportWindow.cs` |
| 08, 12 | `"Key assignment" window`（5か所） | `"Assign key" window` | ウィンドウの題名（`KeyAssignDialog`） |

### 点検して、問題が無かったもの

- トレイのメニュー・ツールチップ（04 章）、設定ダイアログのタブ名（13 章）、ライセンス管理の表示（03 章）、エディタ下部のボタン、キー割り当ての分類（08 章）、インストーラーの画面名（01 章）。
- ヘルプの図は、`img/en/` に同名の画像があれば英語の画面、無ければ日本語の画面が使われる（`build-manual.ps1`）。英語の画像が無い図は、日本語の画面のまま表示される。

## 次にやること・気づいたこと

- 日本語のヘルプ・README の文言を変えたら、まず英語の画面（`Loc.T` の英語）を確認する。画面の文言を変えたときは、ヘルプの英語原稿も同じ語で直す。
- 英語版ヘルプの「Key assignment」のような、ウィンドウを指す小文字の一般語（"the key assignment window"）は、画面の題名と違っても誤りではないので直していない。引用符付きで題名として書くときだけ `Assign key` にした。

## 追記（2026-11-17）: Emacs 風の設定例の英語版

- 英語版 samples/emacs/EMACS-KEYBINDINGS.en-US.md を追加した（日本語版 EMACS-KEYBINDINGS.md から訳し、画面の文言は英語表示に合わせた: Emacs Example...、Choose File...、Replace、Turn remapping OFF など）。HTML 版は uild-manual.ps1 -Lang en が EMACS-KEYBINDINGS.en-US.html として作る（ja のときは従来どおり日本語版）。
- ヘルプ英語版 14 章のリンクを EMACS-KEYBINDINGS.en-US.html に変え、「日本語です」という断りを消した。11 章の参照も .en-US.md にした。インストーラー用の setup/Prepare-Staging.ps1 のファイル一覧にも足した。
- 日本語版の EMACS-KEYBINDINGS.md を直したら、英語版も同じ箇所を直すこと。
- 16 章の既知の制限は「画面の図は、日本語版のものが一部残っています。」（英語版: Some of the screenshots are still from the Japanese version.）に変えた。