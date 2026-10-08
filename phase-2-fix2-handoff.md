# P2.2 申し送り: MapOff の記録を先に書く（A10 の異常 1 回への対応）

- 作業ブランチ: `claude/tender-euler-xxfl7a`、対象コミット: `63bb4d7`。**こちらでは未ビルド**。
- 書いた日: 2026-10-08（開発担当）

## 見立てと修正
- 原因: `TurnMapOff` が `SetEnabled(false)` を先に行い、`tray-mapoff.json` の保存を後にしていた。OFF を観測した直後の kill は、その隙間に当たりうる（試験の手順がちょうどそれ。約 1/22 と合う）。
- 修正: **記録を先に書いてから OFF にする**。ドライバの呼び出しが失敗したら記録を戻す。ファイルは一時ファイルに書いてから置き換える（書きかけを見ない）。戻す側は今のまま（ON にしてから記録を消す。間で落ちても「ON なのに記録が残る」だけで無害）。

## 試験項目
| # | 内容 | 期待値 |
|---|---|---|
| G1 | `dotnet test kasane.Tests` | 全部合格（新規 2 件: `MapOff_RecordIsWrittenBeforeTheKeyboardIsTurnedOff`、`MapOff_FailedTurnOff_LeavesNoRecord`） |
| G2 | A10 を繰り返す: MapOff の窓が前面 → `Enabled=0` を**観測した瞬間**に kill（待ち 0ms を含め、0〜300ms を変えて合計 30 回ほど） | 毎回、kill の後 `Enabled=0` でファイルあり → 起動し直して `Enabled=1`、ファイルなし |
| G3 | 回帰: A9・A11、F2・F3（素早い往復） | 既存どおり |
| G4 | 試験の後、`tray-mapoff.json` と `tray-mapoff.json.tmp` が残っていない | 残らない |
