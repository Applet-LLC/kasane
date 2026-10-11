# P9 試験結果: トレイの uiAccess=true 版（B-4）

- 対象: `ed5c8d5`（`feature/tray-uiaccess`）。署名済みの MSI（`Program Files\applet\kasane\tray\` の `kasane-tray.exe` と `kasane-tray-nua.exe`、ProductVersion `1.0.0+ed5c8d5…`）が入った実機。
- 試験日: 2026-10-11（試験担当）
- 結論: **U1〜U7・U12・U13 は PASS（条件つき）。U8 は FAIL（自己展開あり）。U3 は「uiAccess は、シェル（エクスプローラー）から起動したときだけ付く」という重要な条件が見つかった。U9 は条件つきで PASS。U10・U11 は未実施。**

| # | 結果 | 内容（実測） |
|---|---|---|
| U1 | PASS | `tray\` に `kasane-tray.exe`（178,955,992 B）と `kasane-tray-nua.exe`（178,956,016 B）の 2 つだけ。**両方とも署名は Valid**（署名者 `CN=Applet LLC`）。作成時刻 10:28 |
| U2 | PASS | `HKLM\…\Run` の `kasane-tray` = `"C:\Program Files\applet\kasane\tray\kasane-tray.exe"`（nua ではない） |
| U3 | PASS（条件つき） | 動いているトレイのトークンを昇格したランナーから読むと、**`TokenUIAccess=1`**（インストール済みの `kasane-tray.exe`、Program Files から）。**ただし、`Start-Process`（CreateProcess）で同じ exe を起動すると `TokenUIAccess=0`（uiAccess なし）で、何のエラーも出ずに動く。** `explorer.exe "<exe>"`（シェル経由。ログオン時の Run キーと同じ経路）で起動すると `TokenUIAccess=1`。→ **所見 1**。再起動（ログオフ・ログイン）は行っていないので、ログオン時の自動起動で付くことは未確認（プロセスの開始 10:53 は、起動 10:43 の 10 分後で、ユーザーの手で起動されたものと思われる） |
| U4 | PASS | **本物の uiAccess（`TokenUIAccess=1`）のトレイ**で、管理者として動く試験窓（TextBox つき、昇格したランナーから起動）を前面にして、(a) `HLP(1)`（`Sequence`: 文字 `HLP` ＋ キー `KC_X`）→ **`HLPx` が届く**（6 回すべて）、(b)(c) トリガー `KC_F13`（`Sequence`: 文字 `TRG` ＋ キー `KC_Y`）→ **`TRGy` が届く**（6 回すべて）。同じ操作は P3・P4・P7 では届かなかった。普通のウィンドウでも同じ（`HLPx`・`TRGy`）。UAC の確認画面は試していない |
| U5 | PASS | `kasane-tray-nua.exe`（`TokenUIAccess=0`）に切り替えると、管理者の窓の前面では **HLP も トリガーの文字も届かない**（F13 のキー自体は窓に届く: `7c 64 d/u` を窓のキーログで確認）。普通のウィンドウでは届く（従来どおり） |
| U6 | PASS（条件つき） | uiAccess のトレイで L1・L2・L4・L9 は P8 と同じ結果（レイヤー表示の追従 101〜117 ms、NICOLA + IME 連動の表示と解放、設定のチェックでオフ／オン）。L8: 一時停止 → `一時停止中`・`Enabled=0`、再開 → 通常に戻る。**ただし、uiAccess のトレイのメニューは、UIA（試験側は uiAccess なし）からは項目が見えず、マウスの入力も届かない**（UIPI。昇格したランナーからのクリックなら届く）。ユーザーの実際の操作には影響しないはず。なお T10 が nua 版を使うのはこのため（正しい設計） |
| U7 | PASS | uia が動いている間に nua を起動 → 即終了（コード 0）、逆（nua → uia）も即終了。どちらも 1 つだけが残る |
| U8 | **FAIL** | **uiAccess のトレイが、起動時に `%TEMP%\.net\kasane-tray\<ハッシュ>\` へネイティブ DLL を自己展開している**（`D3DCompiler_47_cor3.dll`、`PenImc_cor3.dll`、`PresentationNative_cor3.dll`、`vcruntime140_cor3.dll`、`wpfgfx_cor3.dll` の 5 個、約 8MB）。フォルダはユーザーが書ける場所（ACL: ユーザー本人に FullControl）。トレイの起動時刻（10:53:02）とフォルダの作成時刻が一致。`DOTNET_BUNDLE_EXTRACT_BASE_DIR` を試験用の場所にして確かめると、**uia（`cgsFAw…`）も nua（`Yreo7G…`）も同じ 5 個を展開**する。**緩和の事実: 動いている uiAccess のトレイのモジュール一覧（39 個）に、Temp の DLL は 1 つも無かった**（読み込まれているのは Windows のものと `kasane-tray.exe` 自身だけ。トレイは WPF の画面を出さないため）。それでも、展開されたファイルを差し替えられる状態が残る（今後トレイが WPF の機能を使えば、読み込まれる）。→ **所見 2** |
| U9 | PASS（条件つき） | 開発ビルドの未署名の uiAccess 版（`kasane-tray-uia\bin\x64\Release\…\publish\kasane-tray.exe`、署名 NotSigned、Program Files の外）: **`explorer.exe` 経由 → 起動しない**（プロセスが残らない。エラーの画面は出ず、何も出ない）。**`Start-Process` 経由 → 起動して動く（`TokenUIAccess=0`）**。手で `kasane-tray-nua.exe` を起動すれば動く。→ 所見 1 の裏返し（起動の経路で挙動が変わる） |
| U10 | 未実施 | 旧版の MSI が無く、上書き・アンインストールはドライバ（kbdlayer）の状態に触るため、試験担当では行っていない（ユーザーの手で行う場合は、`msiexec /l*v` のログと、`tray\` の中身、Run キーを知らせてもらえれば確認する） |
| U11 | 未実施 | 同上 |
| U12 | PASS（記述と違う） | `HLP` の `Launch`（`pwsh.exe` を起動）: 起動する。**起動した子のトークンは `TokenUIAccess=0`**（親は `kasane-tray`、uiAccess=1）。**ハンドオフの変更点 6「子にも uiAccess が引き継がれる」とは違い、引き継がれていない**（受け入れて文書化するという説明を、実測に合わせて直したほうがよい）→ 所見 3 |
| U13 | PASS | `Run-All.ps1`（Windows PowerShell 5.1 のみ、インストール済みのトレイを先に終了）: 最初は T10 が `The system cannot find the file specified` で失敗（**開発ビルドの `kasane-tray\bin\Debug\…\kasane-tray-nua.exe` が無かった**＝今回のビルドは Release だけ。Debug の `kasane-tray.exe`（昨日 22:17 のもの）だけが残っていた）。試験担当が `dotnet build kasane-tray -c Debug`（22 秒、エラー 0）で nua を作り、T10 だけ再実行 → **11/11 合格**。他の 12 本は最初の実行で合格（合計 13/13 相当）。作業ツリーは変更なし（`bin` は無視されている） |

## 所見
1. **uiAccess は、シェル（エクスプローラー）から起動したときだけ付く。** `Start-Process` ほか、シェル以外の親から起動すると、署名済みの正しい exe でも **uiAccess なしで起動し、エラーも出ない**（`TokenUIAccess=0`）。ログオン時の Run キー、スタートメニュー、エクスプローラーの二重クリックは、シェル経由なので問題ない見込み。ただし、(1) インストーラーの最後の「トレイを起動する」処理（`CA_…`）が CreateProcess 系だと、インストール直後のトレイは uiAccess なしで動く（次のログオンまで）、(2) `kasane.exe`（エディタ）や `kasane-license.exe` など、トレイ以外のプログラムがトレイを起動し直す場合も同じ。利用者向けの説明に「uiAccess の確認方法」と、起動の経路による違いを入れてほしい。
2. **U8: 自己展開（上記）。** 対策の例: ネイティブ DLL を単一ファイルの外に置く（`IncludeNativeLibrariesForSelfExtract` を使わず、DLL を exe の隣に置く。Program Files の中は管理者しか書けない）、またはトレイの再配布物から WPF のネイティブ DLL を外す（トレイは WPF を使わない）。
3. 子プロセスは uiAccess を引き継がない（実測）。文書の記述を直す。
4. （参考）uiAccess のトレイを UIA やマウスで試験するには、昇格したランナー（管理者）が要る（UIPI）。トレイのメニュー・IME 連動の試験の方法として、`UiaLib`・T10 は nua 版を使う、という今の設計でよい。
5. （参考）IME 連動が管理者の窓で取れるか（ハンドオフの「未確認」）: 今回は試験していない（管理者の窓で IME を操作する方法を用意できなかった）。

## 試験の補足
- 試験の途中、`Start-Process` で起動した「uia」のトレイが実は `TokenUIAccess=0` で、最初の U4（「届かない」）は無効だった。トークンを確かめてから、エクスプローラー経由で起動し直して U4 を取り直した（上の結果）。
- 試験の最中に、`cmd /c start` で起動を試した結果、Windows Terminal のウィンドウ（タイトルが `C:\Program Files\applet\kasane\tray\kasane-tray.exe`）が 1 つ残っている。試験担当の副作用で、手で閉じてよい（Windows Terminal 全体を終了させると、ほかの端末も閉じるので触っていない）。
- 試験後: レイヤーマップは全台 `--clear`、`helper.json`・`tray.json`・`tray.log` は削除、昇格ランナーを終了、`kasane-tray.exe`（uiAccess、エクスプローラー経由）が動いている状態に戻した（`TokenUIAccess=1`）。リポジトリの作業ツリーは変更なし。
