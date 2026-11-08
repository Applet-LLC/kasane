# Windows 11 の「管理者保護」（Administrator protection）と kasane

2026-10-27 時点。Windows 11（ビルド 10.0.26300）の実機で、管理者保護を有効にして確かめた結果をまとめる。確かめていない点は、その旨を書く。

## 1. 管理者保護とは

Windows 11 の UAC の昇格方式を変えるオプション機能（既定は無効）。「UAC: 管理者承認モードの種類」で「管理者保護を使用した管理者承認モード」を選ぶと有効になる。

- 昇格すると、システムが管理する別の管理者アカウント（SMAA）のトークンで動く。ユーザー自身の管理者トークンは使われない。アカウント名は `ADMIN_<ユーザー名>`（この機では `T480S\ADMIN_applet`）。
- 昇格のたびに認証が要る。静かな昇格（自動承認）は無い。
- 昇格したプロセスは別のユーザーとして動くので、`HKCU`、`%LOCALAPPDATA%`、`%TEMP%` が、元のユーザー（`applet`）のものと別になる。
- 有効かどうかは、`HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System` の `TypeOfAdminApprovalMode` が 2 かどうかで分かる（実機で確認）。コードは `AdminProtection.IsEnabled()`（`kasane/Model/AdminProtection.cs`）。
  - 検討メモの段階では、`Lsa\TypeOfAdminApproval` と書いていた。そのキーは存在しない。

## 2. 実機で分かったこと

### 2.1 uiAccess のトレイは、昇格しない ADMIN_ユーザー名 で動く

`kasane-tray.exe` は `uiAccess="true"` で、`asInvoker` で起動する。管理者保護では、これを Run キー（Explorer）から起動すると UAC の確認が出て、`ADMIN_<ユーザー名>` のトークンで動く。このトークンは昇格していない。`Administrators` グループは deny-only で、管理者としての権限は使えない。

トレイ起動時の診断（`tray.log`）に出た値:

| 起動の経路 | 実行ユーザー | Administrators | uiAccess |
|---|---|---|---|
| Run キー | `ADMIN_applet` | 無効（deny-only） | あり |
| タスクスケジューラ（最上位の特権） | `ADMIN_applet` | 有効 | あり |

uiAccess は、どちらの経路でも付く。昇格した PowerShell から起動しても付いた。

### 2.2 保存先（`KASANE`）に書けない

環境変数 `KASANE`（マシン全体）が `C:\Users\applet\kasane` を指していても、そのフォルダの ACL にある許可は、SYSTEM、Administrators、`applet` だけだった。`ADMIN_applet` 自身は含まれない。Administrators は deny-only なので効かず、トレイとエディタは、保存先に書けない（`UnauthorizedAccessException`）。昇格したエディタは、Administrators が有効なので書ける。

最初に報告された「エディタを閉じられない」は、これが原因だった。閉じるときの自動保存が毎回失敗し、エラーを出して閉じるのを取り消していた。

### 2.3 uiAccess で、昇格したアプリの情報を取れる

`kasane-tray.exe --probe-elevated`（診断コマンド）を、トレイと同じ条件（Administrators が deny-only、uiAccess あり）で実行した。前面が、昇格した PowerShell（整合性レベル High、ユーザー `ADMIN_applet`）のとき、次を取れた。

- exe のパス、クラス、タイトル、トークンの昇格と整合性レベル、ユーザー
- 前面のアプリの規則の経路（`ForegroundInfo.Read()`）
- IME の状態（ON と OFF の切り替えも追えた）
- `WM_NULL` の送信
- キーフックへのキー（キーを打った区間で 2 から 32 キー）

管理者保護でも、前面のアプリの規則、IME 連動、キーフックを使う機能は動く。

### 2.4 トレイのアイコンが表に出ない

タスクバーの設定は、エクスプローラーを動かしている `applet` の `HKCU\Control Panel\NotifyIconSettings` に作られる。トレイは `ADMIN_applet` で動くので、自分の `HKCU` を見ても項目が無く、アイコンを「表に表示」にできなかった。

## 3. kasane の対策

### 3.1 診断

トレイは起動時に、実行ユーザー（SID）、Administrators の有効と無効、uiAccess、管理者保護の有無、`KASANE`、保存先、保存先への書き込み試験の結果を、`tray.log` に 1 行で書く。保存先に書けないときは、`%TEMP%\kasane-tray-fallback.log`（実行ユーザーの `%TEMP%`）に書く。

### 3.2 保存先の権限

`kasane.exe --ensure-data-acl [フォルダ]`（昇格して実行）は、実行しているユーザー自身（管理者保護なら `ADMIN_<ユーザー名>`）に、保存先の変更権限を付ける。継承あり。すでに付いていれば何もしない。

- トレイは、起動時に保存先へ書けないと分かったら、バルーンで知らせる。書けないときだけ、メニューに「保存先の権限を直す…」を出す。押すと昇格して上のコマンドを 1 回実行する（認証が 1 回出る）。
- 付ける相手は、昇格するときと同じ 1 アカウントだけ。他のユーザーには広げない。アカウント名は推測せず、昇格したプロセス自身の SID を使う。
- 管理者保護を使わない従来の UAC では、元のユーザー自身の権限が付いているので、何も起きない。

### 3.3 自動起動

管理者保護が有効だと、Run キー経由の起動で UAC の確認が出る。そこで、インストール時に、管理者保護の有無に合わせて自動起動の方法を切り替える（`kasane.exe --setup-autostart`、`Autostart.cs`）。

| 管理者保護 | 自動起動 |
|---|---|
| 無効 | Run キー（MSI が登録）。タスクは作らない |
| 有効 | ログオン中のユーザーごとのタスク `kasane-tray-<ユーザー名>`。Run キーは `StartupApproved\Run` の値を 03 にして無効にする |

- タスクは、ログオン時に、最上位の特権（`HighestAvailable`）で `startproc.exe`（PowerShell のウィンドウを出さずにアプリを起動するランチャー。トレイと同じフォルダーに入れる）経由で、`-FilePath "<トレイのexe>"` を渡して起動する。電池駆動でも開始、停止しない。UAC の確認は出ない。
- Run キーの値は消さない。MSI のコンポーネントの値なので、消すと修復で戻る。
- タスクを 1 つも作れなかったとき（ログオン中のユーザーが見つからないとき）は、Run キーを有効のままにする。
- アンインストールで、タスクと `StartupApproved` の値を消す。
- 管理者保護の設定を、インストールの後で変えたときは、修復（再インストール）で切り替える。エディタには、切り替えを入れていない。

### 3.4 アイコンを表に出す設定

エクスプローラーを動かしているユーザーの SID を、同じセッションの `explorer.exe` のトークンから得る（`AdminProtection.GetShellUserSid()`）。トレイのユーザーと違えば、`HKEY_USERS\<その SID>` の下を見る。昇格していないトレイは、そこを開けないので、`tray.log` に記録して諦める。タスク経由のトレイは昇格しているので、開ける。

### 3.5 エディタの保存の失敗

閉じる、キーボードの切り替え、管理者として再起動、言語の変更による再起動で、保存に失敗したときは、理由、保存先、実行ユーザーを示し、保存せずに続けるか（既定は「いいえ」）を確かめる。保存の失敗で、エディタを閉じられなくなることはない。

### 3.6 インストーラーの `KASANE_DIR`

エクスプローラーで取ったパスを貼ると、前後に二重引用符が付く。前後の空白と二重引用符を外してから、環境変数 `KASANE` に入れる。

## 4. 制限事項

- タスクは、インストール時にログオンしているユーザー分だけ作る。後から別のユーザーが使うときは、修復（再インストール）で作り直す。複数のユーザーで 1 つのタスクにする方法（グループ指定）は、動くかを確かめていないので採っていない。
- 管理者保護の設定を、インストールの後で変えたときは、修復（再インストール）をするまで、自動起動の方法は切り替わらない。
- ユーザーごとに保存先を変えることは、まだできない。保存先は、マシン全体の環境変数 `KASANE` で決まる（空なら、各ユーザーの `%LOCALAPPDATA%\applet\kasane`）。管理者保護の環境では、この既定は使えない（トレイが `ADMIN_<ユーザー名>` の `%LOCALAPPDATA%` を見るため）。インストーラーで保存先を指定する。
- 設定ダイアログの「i8042prt」のタブと「デスクトップ」のタブの設定は、ふだんのエクスポートには入らず、キーリマップ設定のインポートでは戻らない。管理者保護とは関係なく、再インストールのときに起こる。全体一括のエクスポートで「復旧用の設定を含める」を選ぶと、これらも入る（`docs/SETTINGS-FILES.md` の 5.1 節）。

## 5. 確認の手順

- 診断: トレイを起動した後の `tray.log`（`KASANE` のフォルダ）の最初の行。
- 保存先の権限: `icacls <保存先>` に `ADMIN_<ユーザー名>:(OI)(CI)(M)` があること。
- 自動起動: `schtasks /query /tn "kasane-tray-<ユーザー名>"` でタスクが見つかり、`reg query "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run" /v kasane-tray` の値が `03...` であること。
- 昇格したアプリの情報: `kasane-tray.exe --probe-elevated [回数 [間隔秒]]` を、Win+R（エクスプローラー経由）で実行する。3 秒の間に、昇格したアプリを前面にして、キーを打つ。結果は `%TEMP%\kasane-elevated-probe.txt`。

## 6. 関連するファイル

| ファイル | 内容 |
|---|---|
| `kasane/Model/AdminProtection.cs` | 管理者保護の検出、診断、書き込み試験、エクスプローラーのユーザーの SID |
| `kasane/Model/Autostart.cs` | タスクの定義、`StartupApproved` の切り替え |
| `kasane/Cli/HeadlessCommands.cs` | `--ensure-data-acl`、`--setup-autostart`、`--remove-autostart` |
| `kasane-tray/TrayContext.cs` | 起動時の通知、メニュー「保存先の権限を直す…」 |
| `kasane-tray/TrayIconPromoter.cs` | アイコンを表に出す設定 |
| `kasane-tray/ElevatedAppProbe.cs` | `--probe-elevated` |
| `setup/Product.wxs` | `CA_SetupAutostart`、`CA_RemoveAutostart`、`CA_NormalizeKasaneDir` |
