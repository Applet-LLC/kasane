# P7 試験結果: ドキュメント（HELPER.md）とテストスクリプト（Run-All.ps1）

- 対象: `cdbbf86`（ドキュメントと `kasane.Tests/UiAutomation/Run-All.ps1` だけの更新。コード変更なし。ビルド・再インストールなしで、インストール済みの P6.1 版のまま確認）
- 試験日: 2026-10-10（試験担当）
- 結論: **J1 FAIL、J2 一部の食い違いを報告、J3 整合、J4・J5 は実施（下記）。** 「上書きインストールでファイルが入れ替わらない」は、今回のログでは再現しなかった。

| # | 結果 | 内容（実測） |
|---|---|---|
| J1 | **FAIL** | 非管理者の Windows PowerShell 5.1 で `Run-All.ps1` を実行すると、子の `T*.ps1` が構文エラー・日本語文字列の不一致で落ち、「すべて合格」にならない。原因は、BOM なしの UTF-8 の `.ps1` を 5.1 が CP932 として読むこと（日本語の文字列リテラルが壊れる）。**子を `pwsh` で動かすコピーでは 13 件すべて PASS**（194 秒）。 |
| J2 | 一部不一致 | 下記 |
| J3 | PASS | HELPER.md の制約は P3/P4 の実測と矛盾しない（下記） |
| J4 | PASS（所見あり） | 別ビルドの MSI（製品コードが違う）を、既存インストールの上に実行 → 「新規インストール扱い」に見えたが、ログでは **MajorUpgrade が働いた**（`FindRelatedProducts` が旧 `{CC138EF8-…}` を検出、`RemoveExistingProducts` で旧製品を削除、新 `{6DD453DF-…}` を登録）。旧 kasane.exe は `FileRemove` で消えてから `kasane.exe: To be installed; No existing file` で入れ直された＝入れ替わっている。`Won't Overwrite` は無い。所見は下記 |
| J5 | PASS（所見あり） | 同じ MSI を再実行 → 保守モード（修復）。トレイの終了を求められ、終了せずに進めても、全ファイルが `Overwrite; Existing file is of an equal version`＝上書きされた（kasane.exe・kasane-tray.exe・kbdlayer.sys ほか）。再起動は求められなかったが、ログに `RESTART MANAGER: The user chose to go on with the installation, although a reboot will be required.` がある |

## J1 の詳細と修正案
- 症状: 5.1 で実行すると `FAIL`／`EXCEPTION` が並び、「失敗: …」で終わる。
- 修正案（どれか）: (a) `UiAutomation` 内のすべての `.ps1` に UTF-8 BOM を付ける。(b) `Run-All.ps1` が子を `pwsh` で動かす（PowerShell 7 が前提）。
- もう 1 点: T10 がトレイを止めたまま終わる。Run-All の最後でトレイを起こし直すか、その旨を文書に書くとよい。

## J2: HELPER.md と実際の食い違い
1. **「どちらも管理者権限は要りません」（8 行目）**: 「すぐ試す」の手順に「適用」があるが、画面の「キーボードに適用（ON）」は管理者権限が要る（P4 C10）。設定画面だけでなくキーを割り当てて動かすには管理者が要る、と書き分けたほうがよい。
2. **Id の範囲**: 文書とヘルプ文（`HelperActionEditorWindow.cs:89`）は「1〜65535」だが、検証は 0〜65535 を通す（`:285`、`HelperSettingsModel.cs:132` のメッセージも「0〜65535」）。新規の Id は 1 から振る（`:73`）。どちらかに揃えてほしい。
3. 確認できたもの（実機）: 電源キー（`KC_PWR`・`KC_SLEP`・`KC_WAKE`）は、割り当て画面に「注意: 電源キーです。押すとPCがスリープ・シャットダウンすることがあります」と出て、OK で はい／いいえ の確認が出る。「いいえ」で割り当てられず画面が残る（3 つとも PASS）。
4. ソースで確認（実機は未実施）: 手順は最大 2000 件、待ちは合計 60 秒（`HelperActions.cs:46,48`）。リンク先（IMPORT-NODOKA.md、TUTORIAL-GUI.md、ARCHITECTURE.md）は存在し、TUTORIAL-GUI.md と ARCHITECTURE.md からも HELPER.md へリンクあり。
5. 未実施: 管理者窓が前面のときの HLP（P3 B18 で「トリガー・AppLayer は動かない」を確認済み。HLP 経由は引き続き未確認）。

## J3: 制約の整合
- 管理者窓が前面: トリガーは届かない（P3 B18）。HELPER.md の UIPI の記述と矛盾しない。ただし文書は「`HLP(Id)` のキー受け取りは影響を受けない」とするが、実測は HLP 経由が未確認。
- ロック検知が最大約 2 秒遅れる（P4.1 D4）、キュー 64、5 秒より古いものは捨てる（P3）: 文書の数字と一致。

## J4・J5 の詳細（ログ: `%TEMP%\kasane-j4.log`、`kasane-j5.log`）
- FileVersion は全ファイル 1.0.0.0 のまま。J5（修復）で `equal version` でも `Overwrite` になる（修復は再インストール扱いのため）。J4（アップグレード）は旧製品を先に削除するので、バージョンが同じでも入れ替わる。**今回の2つの操作では、仮説（同じバージョンのファイルが上書きされない）は再現しなかった。** 再現するのは、製品コードを変えず（同じ ProductCode）、通常の `msiexec /i`（修復ではない）で別ビルドを入れるような場合のはずだが、今回のビルドでは製品コードがビルドごとに変わる。
- J4 で `ScheduleReboot` が動き（`MsiRebootActionScheduled=1`、`Propagated Reboot to the client/parent install`）、再起動が求められる状態になった。旧製品の削除中に使用中のファイルがあった可能性（`FilesInUse` の表の読み込みはあるが、`Files in use` のダイアログの行はない）。
- `CA_StopTray` は J4 で `returned actual error code 128 but will be translated to success`（トレイが居ないときの終了コードと思われる。無害）。
- J5 の kasane.exe の GUI を起動したままの試験は、ユーザーが「トレイの終了を求められた」とだけ報告（kasane.exe の GUI を起動していたかは未確認）。ログに kasane.exe の使用中の行（`Files in use`）は出ていない。

## メモ
- 試験の後、保存先は元の状態に戻した。トレイは起動済み。リポジトリの作業ツリーは変更なし（`.cursorindexingignore` はユーザーのもの）。
