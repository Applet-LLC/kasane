# P2.1 申し送り: P2 の所見 2 件の修正

- 作業ブランチ: `claude/tender-euler-xxfl7a`、対象コミット: `1904ab8` と `25c0406`
- 書いた日: 2026-10-08（開発担当）。**こちらでは未ビルド**（.NET SDK なし）。

## 修正

1. **所見1 [中] MapOff の窓へ素早く戻ると OFF にならない**
   - 原因: `AppRuleController.TurnMapOff` が、1 秒ごとにしか更新しない控えの一覧で「すでに OFF か」を判定していた。直前に自分が戻した台が古い「OFF」のままで、「ユーザーが止めた」と取り違えた。
   - 修正: OFF にする直前に一覧を取り直す。自分で ON/OFF したら控えも直す。
2. **所見2 [軽]** キートップの名前を「⚠Slp」「⚠Pwr」に短くした（「⚠Wake」はそのまま）。

## 試験項目

| # | 内容 | 期待値 |
|---|---|---|
| F1 | `dotnet test kasane.Tests` | すべて合格。新規: `MapOff_ReturningQuicklyToTheWindow_TurnsOffAgain`（0/100/250/500ms）と `MapOff_RapidBackAndForth_…`。**修正前のコードでは前者が失敗する想定**（可能なら `git stash` 相当で確認） |
| F2 | 実機: MapOff の窓を 1 秒以上前面にしてから離れ、0 / 100 / 250 / 500ms 後に戻る | いずれも `--list` で `Enabled=0` |
| F3 | 実機: 素早い往復 6 回 | 最後の状態が前面の窓に合う。`tray-mapoff.json` が残らない（他の窓で終えたとき） |
| F4 | A9・A10・A11 の短い回帰 | 既存どおり |
| F5 | キー割り当て画面とキーボード表示で、Sleep/Power/Wake のキートップを目視 | 折り返さない |
