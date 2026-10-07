# kasane 申し送りブランチ（claude/handoff）

開発担当（クラウドの Claude Code）と、試験担当（ローカルのエージェント）のあいだで、フェーズごとの申し送りと試験結果をやり取りするためのブランチ。

- **main とは履歴を共有しない orphan ブランチ。** main にも作業ブランチ（`claude/tender-euler-xxfl7a`）にもマージしない。中身はこのフォルダのファイルだけ。
- ローカルでの置き場所の例: `git worktree add .plans/handoff claude/handoff`。`.plans/` は `.gitignore` 済みなので、LayerDriver の作業ツリーに混ざらない。
- 実装は作業ブランチ `claude/tender-euler-xxfl7a` にある。

## 流れ（フェーズごと）

1. 開発担当が、作業ブランチに実装を push し、ここに `phase-N-handoff.md` を書く。
2. ユーザーが、作業ブランチをビルド・インストールし、試験担当に申し送る。
3. 試験担当が、dev-notes の `input-injection-testing/README.md` に沿って試験し、`phase-N-results.md` に結果を書いて push する。
   - 項目ごとに PASS／FAIL／未実施を書く。FAIL は、生のログの抜粋（注入スクリプト、testsink・`--watch-helper` の出力）を付ける。
   - 試験の手法で新しく分かったことは、dev-notes にも追記する（由来にこのファイル名を書く）。
4. 開発担当が結果を読み、直すか次のフェーズへ進む。次のフェーズに進むのは、結果が返ってきてから。

## ファイル

| ファイル | 書く人 | 内容 |
|---|---|---|
| `phase-N-handoff.md` | 開発担当 | 対象コミット、変更点、ビルドとインストールの注意、試験項目（期待値・使う T-番号）、静的にしか確認していない点、チェックリストに照らした自己レビュー |
| `phase-N-results.md` | 試験担当 | 試験結果 |
| `findings.md` | どちらでも | 途中で分かったこと（dev-notes に蒸留する前の下書き） |

## フェーズ（プランの段階）

| 段階 | 内容 |
|---|---|
| P0 | ドライバ: CUSTOM のヘルパーイベント（`HLP(id)`）と `WAIT_HELPER_EVENT`、KeymapTool の `--watch-helper` |
| P1 | 出力キーの拡張（F13–F24 など、`SC_XXXX`） |
| P2 | トレイ: レイヤーの合成器（IME 連動を載せ替え）、アプリ別レイヤー |
| P3 | トレイ: ヘルパー動作（HLP の受信、F13–F24 の LL フック、動作の実行） |
| P4 | 設定ダイアログとキー割り当て画面 |
| P5 | エクスポート／インポート |
| P6 | のどかの設定のインポート |
| P7 | ドキュメント、UIA の試験、インストーラーの確認 |
