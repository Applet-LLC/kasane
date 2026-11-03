# 申し送り: マークモードと疑似 Shift（D-23）の確認

対象: `main`（`feature/emacs-mark` を取り込み済み。疑似 Shift の修正を含む）。ドライバ（kbdlayer.sys）を入れ替える（Pro 版・サブスク版とも、リンクまで確認済み）。

## 何を確かめたいか

マークモードを終わらせる規則（移動キーと修飾キー以外のキーの make）が、疑似 Shift（`E0 2A`・`E0 36`。PS/2 キーボードが、ナビゲーションキーの前に付ける）を、キーとして数えないこと。数えると、疑似 Shift を前置きするキーボードで、矢印キーを押すたびに、先にマークが終わる。

## 試験（OIB 注入。観察は rawsink、`m-mark.ps1` の安全な順序で）

準備: `samples/emacs/apply-emacs.ps1` で設定例を書き込む。

| No | 操作 | 期待 |
|---|---|---|
| F1 | マーク入（Ctrl+Space）、疑似 Shift つきの ←（`0x2A down e0`、`0x4B down e0`、`0x4B up e0`、`0x2A up e0`）、続けて C-f | 疑似 Shift は、そのまま通る（rawsink で `sc=0x2a ext=1`）。← に Shift が付く（`ext=0` の `2a` と `4b`）。**マークは終わらず**、続く C-f も Shift 付き |
| F2 | マークなしで、疑似 Shift つきの ← | 変わらない（Shift は付かない） |
| F3 | マーク入、疑似 Shift つきの Home、疑似 Shift つきの ←、C-g | Shift 付きの Home、← が出て、C-g で終わる |
| F4 | マーク入、PrintScreen（`E0 2A`、`E0 37`） | `E0 37` は、移動キーでも修飾キーでもないので、マークが終わる |
| F5 | 回帰: `emacs-mark-handoff.md` の A1〜A12、B1、B3、R1〜R10 | 変わらない |

疑似 Shift の判定は、rawsink のログの `ext` 欄で行う（トークン `2ad` は、本物の Shift と区別できない）。

結果は `emacs-mark-fakeshift-results.md` に書く。
