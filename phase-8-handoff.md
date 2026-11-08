# P8 申し送り: トレイのレイヤー表示（B-1）

- 作業ブランチ: `feature/tray-layer-display`（main の `42c9eba` から分岐）。対象コミット: `a599d41`。ビルドは作業ブランチの作業ツリーで行う。
- 書いた日: 2026-10-10（開発担当）。プラン: `~/.claude/plans/c-users-applet-documents-github-layerdr-goofy-cat.md` の B-1。
- **ドライバは変更していない**（`WAIT_LAYER_CHANGE` = 0x812 は実装済み）。`kbd-filter-review` と再署名は不要。

## 変更点
1. `kasane/Interop/KbdLayerClient.LayerWatch.cs`（新規）: `WaitLayerChange(uniqId, lastSeq)`。同期で待つ。取り消しは 995、取り外しは 1617、保留の上限（64本）は 1450、古いドライバは 1。
2. `kasane-tray/LayerWatchService.cs`（新規）: 台数ぶんの待機スレッドを持ち、状態が変わるたびに通知。台数の増減は、トレイの 3 秒タイマーで見直す（`Sync`）。ハンドルは待機専用（Arbiter や PauseController とは共有しない）。
3. `kasane-tray/LayerDisplay.cs`（新規）: 状態からアイコンの1文字とツールチップを決める純粋なロジック。
4. `kasane-tray/TrayIcons.cs`: `WithBadge`（右下に濃い地・白文字の1文字）。番号ごとに使い回す（最大16個）。
5. `kasane-tray/TrayContext.cs`: 購読とアイコン更新。一時停止中・ドライバ未接続・リマップ中のキーボードが無いときは、従来のアイコンのまま。
6. `kasane/Model/TrayConfig.cs`・`kasane/Views/SettingsDialog.cs`: `ShowLayerOnIcon`（既定オン）。NICOLA タブの IME 連動の下に CheckBox（AutomationId: `ShowLayerOnIconCheck`）。保存失敗の文言を「トレイの設定を保存できませんでした」に変更。
7. 単体テスト: `LayerDisplayTests`・`LayerWatchServiceTests`（`dotnet test` は全 407 件合格）。

## 表示の規則（期待値の根拠）
- レイヤー番号は 0 起点。有効なレイヤー（EffectiveLayerMask）の**最上位ビット**を出す。0（基底のみ）なら**重ねない**。
- 1〜9 は数字、10〜15 は A〜F。
- ツールチップは `kasane — 動作中（N台）` の後ろに `・レイヤー N`（NICOLA が効いているときは `（NICOLA）`）。127 文字まで。
- レイヤーマップが OFF のキーボードは無視。複数台は、最上位が最も高い台（同じなら UniqId の小さい台）。
- NICOLA のレイヤー（TG(1) など）が立てば「1」が出る。

## 試験項目（ドライバ接続・トレイ起動の実機）

| # | 内容 | 期待値 |
|---|---|---|
| L1 | トレイを起動し、何もレイヤーを切り替えない | アイコンは通常のまま（数字なし）。ツールチップは `kasane — 動作中（N台）` |
| L2 | MO(n) を押している間、TG(n) を押して離した後 | 押している間／トグル中は、右下に n の数字。離す／戻すと数字が消える（遅れは1秒未満） |
| L3 | レイヤー 10 以上（A〜F）に切り替える | 数字の代わりに A〜F。読める大きさか（見づらければ報告） |
| L4 | IME 連動で、IME をかなに切り替える（NICOLA） | 「1」（NICOLA のレイヤー番号）が出て、ツールチップに `（NICOLA）`。英数に戻すと消える |
| L5 | アプリ別レイヤーの規則（前面のアプリを切り替える） | 規則のレイヤーに合わせて数字が変わる |
| L6 | キーボード 2 台で別々のレイヤーにする | 高いほうの番号が出る。高いほうを戻すと、もう一方の番号になる |
| L7 | キーボードを USB で抜く／挿す | 抜くと表示が消える（またはもう一方に切り替わる）。挿し直すと、数秒（最大約 3 秒）で追従する |
| L8 | 一時停止（メニュー／PauseToggle） | 一時停止のアイコン（灰色＋縦棒）が優先され、数字は出ない。再開で戻る |
| L9 | 設定ダイアログ（NICOLA タブ）の「トレイのアイコンに、今のレイヤー番号を重ねる」をオフにして OK | 数秒でアイコンの数字が消え、ツールチップにもレイヤーが出ない。オンに戻すと再び出る。`tray.json` に `ShowLayerOnIcon` が保存される |
| L10 | ロック（Win+L）→ 解除、ユーザー切り替え | ロック中は待たない（ログに失敗が出ない）。解除後、数秒で表示が追従する |
| L11 | トレイを強制終了（タスクマネージャー）し、起動し直す | アプリ管理レイヤーが戻り、ハンドルが残らない（`--watch-layer` で確認）。起動し直すと表示が復活する |
| L12 | 長時間（目安 30 分以上）レイヤーを何度も切り替える | トレイの GDI オブジェクト数（タスクマネージャーの詳細 → GDI オブジェクト）が増え続けない |
| L13 | 古いドライバ（`WAIT_LAYER_CHANGE` 非対応）が手元にあれば | 表示が出ないだけで、トレイは落ちない。`tray.log` に「WAIT_LAYER_CHANGE に対応していません」 |
| L14 | `KeymapTool --watch-layer` の出力と、アイコンの数字を突き合わせる | 一致する |
| L15 | UIA: `kasane.Tests/UiAutomation/Run-All.ps1`（T9 が設定ダイアログ） | 既存の T1〜T13 が合格のまま（`ShowLayerOnIconCheck` を追加した影響がないこと） |

## 試験担当への注意
- 表示の遅れ・ちらつき・アイコンの見づらさ（高 DPI、ライト／ダークのタスクバー）は主観でよいので報告。
- 画像（トレイの拡大の画面写真）があると助かる。特に 100% / 150% / 200% の DPI。
- 保留の上限（64本）は IME 連動・ヘルパーイベントと共有。キーボードが多数ある環境でなければ試験不要。
- 未確認（静的にしか見ていない）: 高 DPI での文字の大きさ、`SingleBitPerPixelGridFit` での見え方。

## 開発担当の自己レビュー（kbd-filter-review の対象外）
- 待機ハンドルから SET はしない（所有者が変わるため）。
- `CancelSynchronousIo` は、待機に入る直前を空振りしないよう、止まるまで繰り返す（`HelperEventService` と同じ流儀）。
- `HICON` は `Clone` の後に必ず `DestroyIcon`（`ToIcon`）。

## 結果
`phase-8-results.md` に、項目ごとに PASS／FAIL／未実施で書く。FAIL はログ（`tray.log`、`--watch-layer`）と、アイコンの画像を付ける。
