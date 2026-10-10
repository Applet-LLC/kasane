# P7.1 試験結果: UIA スクリプトの BOM、編集画面の Id の説明、HELPER.md の修正

- 対象: `19ea8a7`（P7.1。インストール済みの `kasane.exe` / `kasane-tray.exe` の ProductVersion が `1.0.0+19ea8a7…`）。申し送りファイルは無く、開発担当の説明（BOM の追加、Id の説明、文書の修正）から試験項目を選んだ。
- 試験日: 2026-10-10（試験担当）
- 結論: **BOM は効いた（構文エラー 0）。ただし、Windows PowerShell 5.1 では T9 が 1 件失敗する（スクリプトの不具合、下記 K2）。** それ以外の 12 件は 5.1 で合格。

| # | 結果 | 内容（実測） |
|---|---|---|
| K1 | PASS | 全 15 本の `.ps1`（`Run-All`・`T1`〜`T13`・`UiaLib`）が UTF-8 BOM つき。Windows PowerShell 5.1 での構文チェックはすべて 0 件（前回は日本語で崩れた） |
| K2 | **FAIL（軽微）** | 5.1 だけで（`pwsh` を PATH から外して）`Run-All.ps1` を実行: **12/13 合格、T9 が `EXCEPTION: Illegal characters in path.`** で失敗（157 秒）。原因: `T9-SettingsDialogTabs.ps1` の 56 行目 `"applet\kasane	ray.json"` に、`\t` が**本物のタブ文字（コード 9）**になっている（`"applet\kasane\tray.json"` の書き間違い。PowerShell のダブルクォートでは `\t` はタブにならないので、書くときにタブへ変わったもの）。.NET Framework（5.1）はパスのタブを拒否して例外、.NET 8（pwsh）は通すので、`pwsh` では気付かれなかった。そのため、この行は `tray.json` を読めておらず、`ImeLink` の期待値が既定（`$true`）のまま。修正: `"applet\kasane\tray.json"` に直す |
| K3 | PASS | `Run-All` は `pwsh` があれば優先する（`pwsh` あり: 前回 13/13、194 秒）。最後に「注意: kasane-tray.exe は動いていません（T10 が終了させます）」と出る（実際に T10 のあとトレイは止まっていた。手で起動し直した） |
| K4 | PASS | 編集画面の説明が「…重ならない数（**0〜65535**）にします」（インストール済みの `kasane.exe` の中の文字列で確認。「1〜65535」は残っていない） |
| K5 | PASS | HELPER.md の修正は、実測と矛盾しない: 「適用は管理者権限が要る」（P4 C10）、「トリガーとアプリ別レイヤーは適用が要らない」（P3）、Id「0〜65535、新しいものは 1 から」（入力検証 0〜65535 と同じ）、「管理者窓が前面ではトリガーは動かない」（P3 B18）。HLP の管理者窓の記述は外れた（未確認のため。妥当） |
| K6 | 参考 | RELEASE-CHECKLIST の「7. 上書きで置き換わらないとき」は、今回の J4/J5 のログ（`FileRemove` → `No existing file`、修復は `Overwrite; equal version`、`ScheduleReboot`・`RESTART MANAGER`）と一致する |

## 保留のままの項目（開発担当の一覧のうち）
- 画面の「キーボードに適用」: UIA が昇格したウィンドウを操作できないため、`KeymapTool` で代替（変更なし）。
- ドライバ未接続、リモートセッション、管理者窓が前面のときの HLP(Id): 今回も未確認。
- MSI の「上書きで置き換わらない」: 再現せず。使用中のファイルの再起動待ちという見立ては、J4 のログ（`ScheduleReboot`）と矛盾しない。

## メモ
- 試験の後、`kasane.exe` を止め、トレイを起動し直した。リポジトリ（`claude/tender-euler-xxfl7a`、`19ea8a7`）は変更なし。
