# マークモードと疑似 Shift（D-23）の確認結果（2026-11-04）

対象: `main` の `c260253`（疑似 Shift の修正。トレイは `1.1.0+c260253`、Pro 版のドライバ）。申し送りは `emacs-mark-fakeshift-handoff.md`。観察は rawsink（Raw Input）、設定例は `apply-emacs.ps1` で適用。

## 結論

1. **修正は効いた。** 疑似 Shift（`E0 2A`）を前置きした矢印キー・Home を押しても、マークは終わらず、続く C-f も Shift 付き（F1・F3）。マークなしでは、変わらない（F2）。
2. PrintScreen（`E0 37`）でマークは終わる（F4。ただし判定の書き方に不備があり、動作は期待どおり）。
3. 回帰（マークモード A 群・B 群、Emacs 設定例 R 群）は、すべて合格。

## 試験 F（疑似 Shift）

疑似 Shift は、OIB の `0x2A down e0` ... `0x2A up e0` で注入した。rawsink のログでは、疑似 Shift は `sc=0x2a ext=1`、本物の Shift は `ext=0`。

| No | 操作 | OS に届いた並び | 判定 |
|---|---|---|---|
| F1 | マーク入、疑似 Shift つきの ←、続けて C-f | `2ad 2ad 4bd 4bu 2au 2au`（疑似 Shift、足した Shift、←）のあと、`2ad 4dd 4du 2au`（Shift 付きの →） | **合格**（マークは終わらない） |
| F2 | マークなし、疑似 Shift つきの ← | `2ad 4bd 4bu 2au`（疑似 Shift と ← だけ。Shift は足されない） | 合格 |
| F3 | マーク入、疑似 Shift つきの Home、同じく ←、C-g | `2ad 2ad 47d …`、`2ad 2ad 4bd …`（2 回とも Shift 付き）、Esc | 合格 |
| F4 | マーク入、PrintScreen（`E0 2A`、`E0 37`）、続けて C-f | PrintScreen のあと、C-f が Shift なしの → | 動作は期待どおり（マークが終わる）。**判定は不合格と出た**: Windows が PrintScreen の押下（make）を、観察役のログに出さなかったため（`37d` が無い）。判定の書き方の問題 |

**F4 の副作用**: Windows 11 では、PrintScreen で画面の切り取り（SnippingTool）が起動する。試験のあと、`SnippingTool` が残っていたので、終了させた（保存されたスクリーンショットはなし）。今後の試験では、PrintScreen を注入しない（疑似 Shift だけで足りる）。

## 回帰

| 群 | 結果 |
|---|---|
| A 群（A1〜A12。マークモードの動作） | 10 件すべて合格。取り残しなし |
| B 群（A9 OFF/ON、B1 前面のウィンドウの変更、B3 変更なし） | すべて合格 |
| R 群（R1〜R10。C-x、C-k、キー置換、レイヤーのマクロ） | すべて合格。取り残しなし |

## 後始末

- 昇格ランナーは止め、一時ファイルを消した。`tray.json` は変えていない。
- 内蔵キーボード: レジストリは、ユーザーの設定に戻した。**ドライバの実行中の状態は、試験用の Emacs の設定のまま**。戻すには、kasane の「キーボードに適用(ON)」を押すか、PC を再起動する。
