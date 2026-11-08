# P9 申し送り: トレイの uiAccess=true 版（B-4）

- 作業ブランチ: `feature/tray-uiaccess`（main の `b840e4c` から分岐）。対象コミット: `ed5c8d5`。
- 書いた日: 2026-10-11（開発担当）。**ドライバは変更していない**（`kbd-filter-review` と再署名は不要）。
- **試験にはインストーラー（署名済みの MSI）が必要。** uiAccess=true の exe は、(1) 信頼されたルートまで連なる証明書で署名され、(2) `Program Files` のような管理者しか書けない場所にあるときだけ起動する。開発ビルドの `kasane-tray-uia` の出力は起動できない（正常）。
  ビルドは `setup/` の MSI（EV 署名のトークンが要る。`docs/RELEASE-CHECKLIST.md` の 3）。MSI を作れない環境では、全項目「未実施」でよい。

## 変更点
1. **新プロジェクト `kasane-tray-uia`**: `kasane-tray` のソースをリンクして使う。違いは `app.manifest`（`uiAccess="true"`）だけ。出力名は **`kasane-tray.exe`**。
2. **既存の `kasane-tray`（uiAccess の無い版）の出力名を `kasane-tray-nua.exe` に変更。** 開発ビルドと UIA の T10 は、こちらを使う。
3. **MSI**: `Program Files\applet\kasane\tray\` に両方（`kasane-tray.exe` ＝ uiAccess あり、`kasane-tray-nua.exe` ＝ なし）を置く。**Run キー（ログイン時の自動起動）は `kasane-tray.exe` だけ**を指す。nua 版は置くだけ。
4. 単一起動の mutex（`Local\kasane-tray`）は両方で共通。アップグレード・アンインストールの前の `taskkill` は、両方の名前を止める。
5. 切り替えは**手動**: uiAccess 版が起動しないとき、利用者が `kasane-tray-nua.exe` を起動する（自動のフォールバックは無い）。
6. 子プロセス: トレイが起動する子（HLP の `Launch`）にも uiAccess が引き継がれる（受け入れて文書化）。

## 試験項目（署名済みの MSI を入れた実機）

| # | 内容 | 期待値 |
|---|---|---|
| U1 | MSI を入れる（新規）。`Program Files\applet\kasane\tray\` の中身 | `kasane-tray.exe` と `kasane-tray-nua.exe` の 2 つ。**両方が署名済み**（ファイルのプロパティ、または `signtool verify /pa`） |
| U2 | `reg query HKLM\Software\Microsoft\Windows\CurrentVersion\Run /v kasane-tray` | `tray\kasane-tray.exe` を指す（`-nua` ではない） |
| U3 | 再起動（またはログオフ・ログイン）後 | トレイのアイコンが出る。プロセス名は `kasane-tray`。**uiAccess が効いていること**（Process Explorer の Properties → Security、または `Get-Process` で別手段。取れなければ U4 で代用） |
| U4 | **管理者として動くウィンドウ**（管理者のメモ帳・管理者の PowerShell）を前面にして、(a) `HLP(id)` に割り当てた動作、(b) F13 などのトリガー、(c) `Sequence` の文字・キー入力 | **届く**（P3・P4・P7 の J2 では届かなかった項目）。一方、UAC の確認画面（セキュアデスクトップ）では**届かなくてよい** |
| U5 | 同じ U4 を、`kasane-tray-nua.exe` に切り替えて行う（`kasane-tray.exe` を終了 → nua を起動） | 管理者ウィンドウの前面では**届かない**（従来どおり。比較用） |
| U6 | トレイ（uia）の通常動作: 左クリックでエディタ、メニュー（設定・一時停止・ライセンス・終了）、一時停止、IME 連動、前面アプリの規則、レイヤー表示（P8） | 従来と同じ（P8・P8.1 の L2・L4・L8・L9 の抜き打ちでよい） |
| U7 | 単一起動: `kasane-tray.exe` が動いている間に、`kasane-tray-nua.exe` を起動する | 何も起きずに終わる（2 つ目は mutex で終了）。逆（nua → uia）も同じ |
| U8 | **自己展開**: トレイが動いている間、`%TEMP%\.net\kasane-tray*`（および `kasane-tray-nua*`）のフォルダ | **作られていない**（作られている場合は、利用者が書ける場所の DLL が uiAccess のプロセスに入るので報告。`ARCHITECTURE.md` の安全面の項） |
| U9 | **起動の失敗の確認（任意）**: 開発ビルドの `kasane-tray-uia\bin\...\kasane-tray.exe`（未署名・Program Files の外）を起動する | 起動に失敗する（エラーの画面か、何も出ない）。**このとき `kasane-tray-nua.exe` を手で起動すれば動く**こと。メッセージの文面を記録 |
| U10 | 上書きインストール（P2.2 の持ち越しの手順）: 旧版から入れ直す | 起動中のトレイ（どちらの名前でも）が自動で終了し、新しい版が入る。ログインで `kasane-tray.exe` が起動する。`tray\` に古い `kasane-tray.exe`（uiAccess なしの旧版）が残らない |
| U11 | アンインストール | `tray\` の 2 つとも消え、Run キーも消える |
| U12 | `HLP` の `Launch` で、アプリ（例: メモ帳）を起動する | 起動する。起動したアプリが uiAccess を持つことの副作用（管理者ウィンドウへの入力）は、確認できれば報告（必須ではない） |
| U13 | UIA: `kasane.Tests/UiAutomation/Run-All.ps1`（開発ビルドの `kasane-tray-nua.exe` を使う T10 を含む） | すべて合格（13 件）。**インストール済みの `kasane-tray.exe` が動いていると、T10 の起動が mutex で終わる**ので、先に終了させる |

## 試験担当への注意
- U3 の uiAccess の確認は、Process Explorer が使えるなら「UIAccess」の列（View → Select Columns → Process Security）が確実。使えなければ U4 の結果で判断する。
- U4 で届かない場合の切り分け: (1) 署名が有効か（U1）、(2) `tray\kasane-tray.exe` が動いているか（nua ではないか）、(3) 前面のウィンドウが本当に管理者か、(4) UAC の確認画面ではないか。
- **未確認（開発側では静的にしか見ていない）**: uiAccess が実際に効くこと、IME 連動の状態取得が管理者ウィンドウで取れるようになるか（`Win32ImeSource` は UIPI で取れないと直前の状態を保つ。uiAccess で取れるかは未確認なので、結果を報告してほしい）。
- 試験後、`tray\kasane-tray.exe` が動いている状態に戻す。

## 結果
`phase-9-results.md` に、項目ごとに PASS／FAIL／未実施で書く。FAIL はログ（`tray.log`、`msiexec /l*v` のログ）を付ける。
