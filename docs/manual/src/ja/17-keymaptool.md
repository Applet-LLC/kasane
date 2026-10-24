# 17. KeymapTool とサンプル

KeymapTool（`KeymapTool.exe`）は、コマンドラインでキーボードを設定するツールです。キャリブレーション、キーマップの書き込み、ON と OFF の切り替えを、コンソールから行えます。画面で操作する場合は、[5. エディタの基本画面](05-editor-basics.html)から読んでください。

インストール先には、KeymapTool で使う手順書とサンプルのキーマップが入っています。ここでは、その案内をします。リンクをクリックすると、Windows の既定のアプリでファイルが開きます。

## 手順書

| ファイル | 内容 |
|---|---|
| [TUTORIAL-CLI.md](../../docs/TUTORIAL-CLI.md) | インストールから、キャリブレーション、キーの入れ替えの書き込み、動作の確認までを、コマンドで行う手順です。JIS キーボードと US キーボードの両方を扱います。テンキー単体、親指シフト（NICOLA）、IME 連動の使い方も載っています |

手順書の保存先は、インストール先の `docs` フォルダです。

## サンプルのキーマップ

インストール先の `samples` フォルダに入っています。[samples/README.md](../../samples/README.md) に、各ファイルの使い方と確認項目があります。

はじめに使うのは、次の 4 つです。[TUTORIAL-CLI.md](../../docs/TUTORIAL-CLI.md) の手順で使います。

| ファイル | 内容 |
|---|---|
| [tutorial-jis109-eisu-lctrl.json](../../samples/tutorial-jis109-eisu-lctrl.json) | JIS キーボードで、英数キーと左 Ctrl を入れ替えます |
| [tutorial-us104-caps-lctrl.json](../../samples/tutorial-us104-caps-lctrl.json) | US キーボードで、CapsLock と左 Ctrl を入れ替えます |
| [tutorial-swap-2key.json](../../samples/tutorial-swap-2key.json) | 2 つのキーだけを入れ替えます。JIS と US の両方で使えます |
| [tutorial-10key-numbers.json](../../samples/tutorial-10key-numbers.json) | テンキー単体のキーボードで、NumLock に関係なく常に数字を出します |

機能ごとの動作確認に使うサンプルです。

| ファイル | 内容 |
|---|---|
| [nicola.json](../../samples/nicola.json) | 親指シフト（NICOLA）の配列です。NICOLA 配列の規格書の図 1 を、そのまま書き起こしてあります |
| [nicola-enter-space.json](../../samples/nicola-enter-space.json) | `nicola.json` と同じ配列で、親指キーを単独で押したときに、無変換を Enter、変換を Space にします |
| [test-keymap-nicola-toggle.json](../../samples/test-keymap-nicola-toggle.json) | 親指シフトの ON と OFF を切り替えるキーだけを置いた、1 キーのレイヤーです |
| [test-keymap-modtap.json](../../samples/test-keymap-modtap.json) | 長押しとタップで動作を変える Mod-Tap と Layer-Tap の例です |
| [test-keymap-macro.json](../../samples/test-keymap-macro.json) | マクロの例です |
| [test-keymap-combo.json](../../samples/test-keymap-combo.json) | 2 つのキーの同時押しで別のキーを出す、コンボの例です |
| [test-keymap-tapdance.json](../../samples/test-keymap-tapdance.json) | タップの回数で動作を変える、タップダンスの例です |
| [test-keymap-helper.json](../../samples/test-keymap-helper.json) | プログラムの起動や文字列の入力などを呼び出す、ヘルパー動作のキーの例です |
| [test-keymap-simple.json](../../samples/test-keymap-simple.json) | 10 個のキーだけの、最小の例です |
| [test-keymap-us104.json](../../samples/test-keymap-us104.json) | US 104 キーボードの全キーを並べたキーマップです |
| [test-keymap-jis.json](../../samples/test-keymap-jis.json) | JIS 109 キーボードの全キーを並べたキーマップです |
| [test-keymap-10key.json](../../samples/test-keymap-10key.json) | テンキー単体（17 キー）のキーマップです |

画面でヘルパー動作を設定したい場合は、[11. ヘルパー動作とアプリ別レイヤー](11-helper-apprules.html)を見てください。
