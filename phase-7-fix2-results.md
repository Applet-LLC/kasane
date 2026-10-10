# P7.2 追試結果: T9 のパスのタブ文字

- 対象: `bdf6b42`（T9 の 56 行目の `\t` を直した 1 件。スクリプトだけの変更で、ビルドの変更なし）。インストール済みは `19ea8a7` のまま。
- 試験日: 2026-10-10（試験担当）
- 結論: **PASS。** Windows PowerShell 5.1 だけ（`pwsh` を PATH から外した）で `Run-All.ps1` が **「すべて合格（13 件）」**（`exit=0`）。T9 の `Illegal characters in path` は出なくなった。

| # | 結果 | 内容（実測） |
|---|---|---|
| L1 | PASS | `T9-SettingsDialogTabs.ps1` の 56 行目が `"applet\kasane\tray.json"`（タブ文字なし）、BOM あり |
| L2 | PASS | 5.1 のみで `Run-All.ps1`: 1 回目（修正直後）は T9 が合格、T1・T8 が不合格（下記）。2 回目は **13/13 合格** |

## 1 回目の T1・T8 の不合格（再現せず）
- 1 回目の全体実行で、T1 と T8 が `EXCEPTION: element with AutomationId='IdList' not found under  within 5000ms`（5 秒以内に見つからない）で不合格。同じ T1・T8 を単独で 5.1 で実行すると合格、全体をもう一度流しても合格（13/13）。したがって**間欠的な失敗**（画面が出るのが遅れて待ちの 5 秒を超えた、直前の実行の `kasane.exe` の後始末と重なった、など）で、今回の修正とは関係ないと見ている。再び出るようなら、`UiaLib.ps1` の待ち時間を延ばすとよい。
- 補足: `Run-All.ps1 -Only T1-,T8-` を `-File` で渡すと、コンマ区切りが 1 つの文字列になり、0 件になる（`-Only T1-` のように 1 つずつ渡す）。`powershell -File` の仕様で、不具合ではない。

## メモ
- 試験の後、`kasane.exe` を止め、トレイを起動し直した。リポジトリ（`claude/tender-euler-xxfl7a`、`bdf6b42`）は変更なし。
