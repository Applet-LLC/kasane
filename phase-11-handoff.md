# P11 申し送り: ユーザーモードの C++ ツールの静的リンク（/MT）

- 作業ブランチ: `fix/static-link-cpp-tools`（`docs/third-party-notices` の上）。対象コミット: `5ac2de9`。
- 書いた日: 2026-10-12（開発担当）。**カーネルドライバ（kbdlayer）は変更していない。**
- 試験には、このコミットから作った**署名済みの MSI**（x64・ARM64 の両方の構成を含む）と、**VC++ 再頒布可能パッケージが入っていない環境**が要る。

## 変更点
1. `DriverManager`・`DriverManager_arm64`・`KeymapTool` の vcxproj に、`RuntimeLibrary` を足した（Release は `/MT`、Debug は `/MTd`）。VC++ ランタイムを exe に取り込むので、`vcruntime140.dll`・`msvcp140.dll`・`api-ms-win-crt-*` に依存しない。
2. `Prepare-Staging.ps1`: ステージした 3 つの exe が、上の DLL をインポートしていたら、ビルドを止める（動的リンクの古いビルドが混ざるのを防ぐ）。
3. README（日本語・英語）から、VC++ 再頒布可能パッケージの要件を削った。
- 背景: これまで、MSI は VC++ ランタイムを同梱せず、まっさらな Windows 11 では `DriverManager.exe` が起動できず、`CA_InstallKbdDriver` が失敗する可能性があった。

## 開発側で確認したこと
- Release の x64・ARM64 をビルドし、インポート表を調べた: `DriverManager.exe`＝advapi32・kernel32・newdev・setupapi・version、`KeymapTool.exe`＝kbd106・kernel32、`DriverManager_arm64.exe`＝advapi32・kernel32・newdev・setupapi・version（CRT の DLL は無い）。サイズ: `DriverManager.exe` 約 114KB → 371KB。
- 古い（動的リンクの）ステージ済みの exe は、ガードで止まる。Debug 構成（x64）のビルドが通る。
- **未確認**: これらの exe は管理者権限が必須で、開発側では実行していない。

## 試験項目（署名済みの MSI）

| # | 内容 | 期待値 |
|---|---|---|
| R1 | インストール先（`C:\Program Files\applet\kasane\...`）の `DriverManager.exe`・`DriverManager_arm64.exe`・`KeymapTool.exe` のインポート表（`dumpbin /imports`、または同等のもの） | `vcruntime140`・`msvcp140`・`api-ms-win-crt-*` が**無い** |
| R2 | **x64**: VC++ 再頒布可能パッケージが無い環境（Windows Sandbox など、まっさらな環境）で、MSI をインストール | エラーなく完了。ドライバが入る（再起動後に `sc query kbdlayer`、`KeymapTool --list` が動く）。**インストールの途中で `CA_InstallKbdDriver` が失敗しない**（失敗すると、エラー 1722「再起動してから、もう一度」が出る） |
| R3 | **ARM64**: VC++ 再頒布可能パッケージが無い環境（無ければ、入っている PC で、アンインストールしてから）で、MSI をインストール | R2 と同じ。ARM64 では、x64 の `DriverManager.exe` が、ARM64 の相棒（`DriverManager_arm64.exe`）へ引き渡す動きも含めて、通る |
| R4 | アンインストール（R2・R3 の環境） | 完了する。再起動後にドライバ（`kbdlayer` サービス）が消える |
| R5 | 回帰: `check-pending`（アンインストール直後・再起動前の再インストールで、案内が出て巻き戻る）、`KeymapTool` の主要コマンド（`--list`、`--apply`、`--off`、`--get-layer-state`）、トレイ・GUI の通常動作 | 従来どおり |
| R6 | **ビルドのガード**（試験担当は任意）: 動的リンクの古い `DriverManager.exe` をステージに置いてから `Prepare-Staging.ps1` を実行 | 「stale /MD build」のメッセージで止まる |

## 試験担当への注意
- R2・R3 は、**まっさらな環境でなければ意味がない**（VC++ 再頒布可能パッケージが入っていると、静的リンクでも動的リンクでも通ってしまう）。入っていないことを、環境の「アプリの一覧」で確かめる。
- 試験後、まっさらな環境は破棄してよい（Windows Sandbox は閉じるだけ）。

## 結果
`phase-11-results.md` に、項目ごとに PASS／FAIL／未実施で書く。FAIL は、`msiexec /l*v` のログと、`setupapi.dev.log` の該当部分を付ける。
