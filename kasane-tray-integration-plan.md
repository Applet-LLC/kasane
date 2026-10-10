# kasane-tray との連動機能プラン（拡張出力キー／ヘルパー動作／アプリ別レイヤー）— 記録

> このファイルは、開発中に使ったプランの記録です（`claude/handoff` ブランチ。`main` や作業ブランチには入れません。LayerDriver の `.plans/` はコミットしない決まりのため、ここに置いています）。
> 下の「実施の結果」が最終の状況で、その後ろの「プラン本文」は、開発中に何度も追記したものを、そのまま残しています
> （最後のほうの「参考」の節は、各フェーズの試験結果を受けて足した、古い順の記録です）。**食い違うときは、実装と `docs/`（`ARCHITECTURE.md`・`HELPER.md`・`IMPORT-NODOKA.md`）が正です。**

## 実施の結果（2026-10-10 時点）

作業ブランチ `claude/tender-euler-xxfl7a`（`main` の `15d5d92` から26コミット）。各フェーズの申し送りと試験結果は、このブランチの `phase-N-handoff.md` / `phase-N-results.md`。

| 段階 | 内容 | 結果 | 主なコミット |
|---|---|---|---|
| P0 | ドライバ: CUSTOM のヘルパーイベント（`HLP(id)`）と `WAIT_HELPER_EVENT` | 全 PASS（Verifier 下） | `f57644d` |
| P1 | 出力キーの拡張（F13–F24、ブラウザー/システムキー、`SC_XXXX`） | 全 PASS | `dcbe9ef`、`4861812`（電源キーの確認） |
| P2 | レイヤーの合成器（IME 連動を載せ替え）、アプリ別レイヤー、MapOff | PASS。MapOff の再入と記録の欠落を修正（P2.1・P2.2） | `afe32ab`、`1904ab8`、`63bb4d7` |
| P3 | ヘルパー動作（HLP 受信、F13–F24 フック、実行） | PASS。HLP が捨てられる不具合（時刻の符号）を修正 | `d0e54d6`、`e381e65`、`4b2868c` |
| P4 | 設定ダイアログの2タブとキー割り当ての入口、セッション状態をその場で判定に作り直し | PASS（P4.1） | `6e0c8c5`、`5a75ee5` |
| P5 | エクスポート／インポート（`*.kasane.json`。全体一括・キーボード・レイヤー1枚） | E1〜E19 PASS、P5.1 PASS | `5e2af35`、`a6dcfef` |
| P6 | のどか（`.nodoka`）の取り込み | G1〜G15 PASS。include の探す場所を修正（P6.1 PASS） | `2d415e7`、`da2afe2` |
| P7 | 文書（`docs/HELPER.md`）、UIA の整理（`Run-All.ps1`）、インストーラーの上書きの切り分け | PASS（P7.1・P7.2） | `78bf5bd`、`19ea8a7`、`bdf6b42` |

### 計画からの変更
- **古いドライバ向けの案内は入れない**: 当初は「CUSTOM に対応しないドライバでは F13–F24 のトリガーへ自動で切り替える」案だったが、まだドライバを提供していないので、画面と文書から外した（トレイ内部の `ERROR_INVALID_FUNCTION` の扱いは安全策として残す）。
- **セッションの状態**: イベントで持つ作りをやめ、その場で確かめる（`ImeLinkHost.SessionActive`、150ms だけ覚える）。
- **上書きインストール**: FileVersion を上げる対策は入れていない（ログ付きの試験で再現せず。見立ては「使用中のファイルは再起動まで入れ替わらない」。`RELEASE-CHECKLIST.md` の7）。
- **設定ダイアログの UIA**: `T11`（ヘルパー）、`T12`（インポート／エクスポート）、`T13`（のどか）を追加。

### 保留のままの確認
- 画面の「キーボードに適用」の操作（UIA が昇格したウィンドウを操作できないため、`KeymapTool` で代替）。
- ドライバ未接続（オフライン）、リモートセッション、管理者窓が前面のときの `HLP(Id)`。
- MSI の「上書きで置き換わらない」件（再現せず）。

### 知見（dev-notes）
`input-injection-testing/README.md` の T-34（時刻の符号付き比較）、T-35（ロックの検知は、その場で）、T-36（`long.MinValue` を初期値にしない）、T-37（`.ps1` は BOM つき）、T-38（MSI は使用中ファイルの再起動待ちを疑う）。

---

# プラン本文（開発中の記録。上のとおり、食い違うときは実装と `docs/` が正）

# kasane-tray との連動機能プラン（拡張出力キー／ヘルパー動作／アプリ別レイヤー）

## 連絡（2026-10-08）: P4 の試験は、一つ前のコミット（`6e0c8c5`）で行う

ユーザーの連絡: 試験は、文言を削る前のビルドで実施する。文言の修正（`c132185`）は画面の説明文と文書だけの変更で、試験項目に影響しない。
- 対応: `claude/handoff` の `phase-4-handoff.md` の「更新」の欄を、「試験の対象は `6e0c8c5`（ProductVersion が `…+6e0c8c5…`）。`c132185` は説明文だけの差で、次の版に含まれる」に直して push する。C4 の目視では、キー割り当て画面の説明文に古いドライバの段落が**出ていてよい**（`6e0c8c5` のため）ことを添える。
- 試験の結果が返ったら、`6e0c8c5` の結果として読む。文言の差は、結果に影響しない。

## 追加の指示（2026-10-08）: 「古いドライバ」の案内文を削る

ユーザーの指示: 「古いドライバでは HLP は動きません。F13〜F24 のトリガーは、設定の『ヘルパー動作』タブで作れます」の文言は不要。まだどこにもドライバを提供していないので、古いドライバは存在しない。
- 画面の文言を削る: `KeyAssignDialog.xaml.cs` の「ヘルパー動作」パネルの説明（「HLP は、対応したドライバとトレイが動いているときだけ働きます。古いドライバでは…」の段落）、`HelperActionsTab.cs` の説明（「ドライバが対応している必要があります」「（古いドライバでも使えます）」）。
- 文書から削る: `docs/TUTORIAL-GUI.md`（「HLP に対応したドライバとトレイが要る」→「トレイが要る」、古いドライバの言及）、`docs/ARCHITECTURE.md` の P4 の段落（「古いドライバへの自動切り替えは入れていない（画面で案内する）」）。
- 残すもの: トレイ内部の `ERROR_INVALID_FUNCTION` の扱い（止めてログに残す）とそのコメント。ドライバの IOCTL を知らない場合の安全策であり、利用者向けの文言ではない。KeymapTool の「does not support helper events」も、診断メッセージとして残す。
- 申し送り（`phase-4-handoff.md`）の「計画からの変更」の段落を直す。コミットして push する。

## 現在の状況（2026-10-08、P3.1 の結果を受けて）

**P3.1 の結果**（`phase-3-fix-results.md`）: R1〜R10 すべて PASS（299 件合格）。HLP の不具合は直った。**P3 は完了。**
- 軽微な所見（R7）: Win+L のロックでは、`SessionLock` の通知が 0.2〜3.4 秒遅れる間に、HLP が6回中5回、1回ずつ実行された。→ **P3.2（小さな修正）**: 実行を許すかの判定（`HelperHost.Allow` と待機の `wanted`）で、入力デスクトップに触れるかを、その場で確かめる（150ms だけキャッシュ）。`ImeLinkHost.SessionActive` を「イベントで追う値 かつ 入力デスクトップに触れる」にする。

## P4: 設定ダイアログとキー割り当て画面（設計）

目的: helper.json を手で書かなくてよくする。**管理者権限は要らない**（helper.json は利用者のフォルダ。OK で直接保存し、トレイが数秒で読み直す）。

**P4 の前に、共有コードを整理する**
1. `kasane-tray/HelperActions.cs`（手順の展開・トリガーの表・実行）を `kasane/Model/HelperActions.cs` へ移し、名前空間を `Kasane.Model` にする（エディタが検証・プレビューに使うため）。トレイの csproj にリンクし、`kasane.Tests` のリンクは外す（kasane を参照しているので不要）。`using Kasane.Model;` をトレイの該当ファイルに足す。
2. `HelperConfig` の読み書きを強くする: `TryLoad(path, out config, out error)`（構文エラーを返す。エディタは壊れたファイルを黙って上書きしない）、`Save` は一時ファイル＋置き換え（トレイが読む途中を見ない）。トレイ側は従来どおり `Load`（壊れたら「設定なし」＋**ログ**を足す）。
3. 純粋なロジック `HelperConfigEditor`（`kasane/Model/`）: 範囲（このキーボード／共通）ごとの読み込み・保存、`NextFreeActionId`（1〜65535）、検証（Id の重複、トリガーのキー・修飾キー、手順のコンパイルエラー、正規表現、レイヤー範囲）。テストする。
4. 手順のテキスト表記 `StepText`（パーサーと書き出し。1行1手順）: `key LCTL(KC_C)` / `down KC_A` / `up KC_A` / `text こんにちは` / `delay 50` / `mouse left [click|down|up]` / `move 10 -5` / `moveto 100 200` / `wheel -120` / `hwheel 120`。行番号つきのエラー。往復のテストをする。画面は、これを複数行の入力欄で編集する（表より単純で壊れにくい）。

**設定ダイアログに2タブ**（`SettingsDialog.cs` は、タブを足す数行だけにする。中身は新規ファイル）
- **「ヘルパー動作」**（`HelperActionsTab`）: 範囲の切り替え（このキーボード／全キーボード共通。オフラインのときは共通だけ）。2つの一覧: `Actions`（Id・名前・種類。HLP(id) から呼ぶ）と `Triggers`（キー・修飾キー・種類。キーフック）。追加・編集・複製・削除。編集は `HelperActionEditorWindow`: 種類（Launch / Open / Sequence / AppLayer / PauseToggle）で入力欄が変わる。Launch・Open は参照ボタン。Sequence は手順の入力欄（押したとき／離したとき）と、その場の検証。AppLayer は 0〜15 のチェックと On/Off/Toggle/Hold。Repeat のチェック。Triggers のキー欄は `KeycodeTable` で検証。
- **「アプリ別レイヤー」**（`AppRulesTab`）: 範囲の切り替え。規則の一覧（上へ／下へ、有効のチェック、追加・編集・複製・削除）。編集は `AppRuleEditorWindow`: 名前、Default、照合（Exe・PathRegex・TitleRegex・Class・NodokaClass・NodokaTitle・NodokaOr。正規表現の検証は `AppMatch.InvalidRegexField`）、レイヤー 0〜15 のチェック、MapOff。**「実行中のアプリから選ぶ」**ボタン: 今開いているウィンドウ（exe・クラス・タイトル）の一覧から選んで、Exe と Class を入れる（`EnumWindows`、新規 `OpenWindowList`）。
- どちらも **OK のときに保存**（キャンセルなら保存しない）。保存に失敗したらタブに理由を出す。変更が無ければ書かない。

**キー割り当て画面に「ヘルパー動作」の入口**（`KeyAssignDialog`）
- 新しいカテゴリ「ヘルパー動作」: このキーボードと共通の `Actions` を一覧し、選ぶと `HLP(id)` が入る。「新しい動作…」で `HelperActionEditorWindow` を開き、空いている Id で `Actions` に保存して、そのまま `HLP(id)` を入れる。
- `KeyAssignDialog` に、キーボードの UniqId を渡す（オフラインなら 0 → 共通だけ）。`HLP(id)` が `Actions` に無いときは、検証欄に注意を出す（エラーにはしない）。
- **古いドライバへの自動切り替えは入れない**（GUI からは、ドライバが WAIT_HELPER_EVENT に対応しているか安全に調べられないため）。「古いドライバでは HLP は動きません。F13〜F24 のトリガーは、設定の『ヘルパー動作』タブで作れます」と、画面に説明を出す。**当初の計画から変更**。

**テスト**: `StepTextTests`、`HelperConfigEditorTests`（保存先、Id の割り当て、検証、壊れたファイルの扱い）、`HelperConfig` の往復。UIA: `T9` のタブ数を更新し、新規 `T11-HelperTabs.ps1`（2つのタブの有無、規則の追加→OK→helper.json に書かれる、動作の追加）。
**ドキュメント**: `docs/ARCHITECTURE.md`、`docs/TUTORIAL-GUI.md` に使い方。

**手順**: ① P3.2 の修正 → ② 共有コードの整理（移動・TryLoad・Save）→ ③ 純粋なロジックとテスト → ④ 2タブとエディタ窓 → ⑤ キー割り当ての入口 → ⑥ ドキュメント・UIA → ⑦ `phase-4-handoff.md`。画面は、ここでビルドも表示もできないので、**構文の確認と、コードの読み合わせを念入りに行い、試験担当に画面の目視（スクリーンショット）を頼む**。

## （参考）P3 の結果を受けた時点の記録（2026-10-08）

**P3 の結果**（`phase-3-results.md`）: B1・B3〜B20・B22 PASS（HLP 経由はパッチ版で確認）。B21（古いドライバ）は未実施。**不具合1件と所見数件。**

**不具合 [高]: インストール済みの d0e54d6 では、HLP(id) を押しても何も起きない**
- 原因（試験担当が特定、コードで確認）: `HelperEventService.cs:75` の「古すぎるイベントを捨てる」判定 `(uint)(Environment.TickCount64 - e.TimeMs) > StaleMs`。ドライバの時刻が `TickCount64`（約15.6ms 刻み）より数〜十数 ms 先に進むと、差が負になり、`uint` で 4294967284 付近に巻き戻って捨てられる。押下は速く届いてほぼ毎回当たり、解放は遅れて通る。
- 修正: 符号付きで比べる。ただし試験担当の1行案（`long` のまま）は、**起動から 49.7 日を超えると**、`TimeMs`（`uint` で巻き戻る）との差が巨大になり、すべて捨てる不具合に変わる。そこで、`unchecked((int)((uint)Environment.TickCount64 - timeMs))` で 32bit の差を符号付きにして比べる（負 = 新しい）。純粋な `HelperEventService.IsStale(long tickCount64, uint timeMs)` に切り出し、テストする（負・境界・巻き戻り）。
- 教訓: 単体テストは時刻の比較をまだ見ていない。`IsStale` のテストで、「少し未来」「ちょうど StaleMs」「uint の巻き戻り」を入れる。
- dev-notes: 「ドライバと user mode の時刻を比べるときは、符号付きの差で。unsigned の引き算は、刻みの差で負になり巻き戻る」を追記する（`input-injection-testing` か `kbd-filter-driver`。由来は `phase-3-results.md`）。

**所見への対応**
1. **ロック画面から戻った直後に HLP が動く**（B17）: `ConsoleConnect` で、ロック状態を見ずに「アクティブ」にしていた。→ セッションの接続・ログオンのイベントで「入力デスクトップに触れるか」を確かめる（`OpenInputDesktop(0, false, DESKTOP_SWITCHDESKTOP)` が `ERROR_ACCESS_DENIED` ならロック画面など。他のエラーは「触れる」とみなす）。ロックしていたら非アクティブのままにし、`SessionUnlock` で戻す（既存）。トレイの起動時の判定にも使う。
2. **キューの消費**（B19）: 解放が要らない動作（`ReleaseSteps` の無い `Sequence`、Hold でない `AppLayer`、`Launch`/`Open`/`PauseToggle`）は、解放をキューに積まない（`ActionExecutor.NeedsRelease`）。キューは、押下を 64 件で打ち切り、解放は 256 件まで受ける（押下が入って解放が捨てられ、ボタンが押されたままになるのを避ける）。
3. **ログの重複**（B20）: `AppLayer: Layers が空です` を押下のときだけ出す。
4. 構文エラーの helper.json でログが出ない件は、P4 の設定画面の検証で扱う（今回は見送り）。
5. 取扱説明（P7）に書くこと: 管理者の窓が前面のときはフックにキーが届かない（UIPI）、相対移動はポインター加速を受ける、`WorkDir` 省略時はトレイの作業フォルダ。
6. 「ConsoleDisconnect の 1.3 秒後に HLP が1回動いた」は、切り替え中の遅延として様子を見る。

**進め方**: 上の修正を1コミットで入れ、`phase-3-fix-handoff.md` を書く（再試験: B2・B3 を**インストール版**で、B17 の切り替え後のロック画面、B19 の連打とボタン、B8、B22 の短い回帰）。その後、P4（設定画面）に進む。

## （参考）P2.2 の結果を受けた時点の記録（2026-10-08）

**P2.2 の結果**（`phase-2-fix2-results.md`）: G1〜G4 すべて PASS（246 件合格）。kill のタイミングを変えた 32 回で、記録の欠落は再現せず。**P2 は完了。**

**試験担当からの提案（要調査）: 上書きインストールで、ファイルが置き換わらないことがある**
- 状況: インストール済みの exe が、直前の版（`…+25c0406`）のまま（更新日時も古いまま）だった。MSI は正常終了。アンインストール後の入れ直しで解消。
- 試験担当の見立て: ビルドごとの `FileVersion` が `1.0.0.0` で同じなため。ただし、**確定していない**。
- コードで分かったこと: \`setup/Product.wxs\` は \`MajorUpgrade\` に \`AllowSameVersionUpgrades="yes"\` を付けており、旧製品の除去→新規インストールになるはず。したがって原因は、ファイルバージョンの同一だけでは説明しきれない（使用中のファイル、除去の順序なども疑う）。
- 対応: **P3 の流れとは別に、まず原因を切り分ける**。MSI のログ（\`msiexec /l*v\`）で、コンポーネントごとに「置き換え／スキップ」を見る。確定してから直す（ビルドごとに \`FileVersion\` を上げる、など）。ユーザーに、再現したときのログ取得を頼む。P3 の申し送りには、「インストール後に ProductVersion を確かめる」手順を入れる。

**次: P3**（下の手順）。P2.1・P2.2 は完了。

## （参考）P2.1 の結果を受けた時点の記録（2026-10-08）

**P2.1 の結果**（`phase-2-fix-results.md`）: F1〜F5 PASS（244 件合格、所見1は直った）。「⚠Wake」のキートップの折り返しは、ユーザーが許容。

**残った所見（約 1/22 で再現）: MapOff で `Enabled=0` なのに `tray-mapoff.json` が無く、トレイが落ちると戻せない**
- **原因**（コードで特定）: \`TurnMapOff\` が先に \`SetEnabled(false)\` を実行し、記録の保存（\`SaveMapOff\`、\`ApplyDecisions\` の最後）が後になる。OFF を観測した直後に kill すると、この数 ms〜十数 ms の間に当たり、「OFF なのに記録なし」になる。試験の手順（OFF を見たらすぐ kill）は、まさにこの隙間を狙う形で、頻度（約 1/22）とも合う。\`JsonMapOffStore.Save\` は単純な \`File.WriteAllText\`（途中のファイルが見える）。
- **修正（書き込みを先にする）**
  - \`TurnMapOff\`: \`_mapOff.Add(id)\` と保存を**先に**行い、そのあと \`SetEnabled(false)\`。失敗したら記録から外して保存し直す。
  - 戻す側（\`RestoreMapOff\`）は今のまま（ON にしてから記録を消す）。kill が間に入っても「ON なのに記録が残る」だけで、次の起動で ON にするのは無害。
  - \`JsonMapOffStore.Save\` は、一時ファイルに書いてから置き換える（\`File.Replace\` / \`File.Move(overwrite)\`）。途中のファイルを読んで「記録なし」と扱わないため。
- **テスト**: 保存の順序を確かめる偽のストア。\`SetEnabled(false)\` が呼ばれた時点で、ストアにはすでに ID がある（OFF の瞬間に記録が無い時間が無い）。\`SetEnabled\` が例外のときは記録が残らない。
- **知見**: 「外の状態を変える前に、戻すための記録を先に書く（write-ahead）」。dev-notes に追記するか判断する（リマップの OFF のような、永続する副作用全般に当てはまる）。
- **申し送り**: \`phase-2-fix-handoff\` の続きとして、P3 の申し送りに「A10 の再試験（kill のタイミングを変えて 30 回ほど）」を含める。

**進め方**: この修正を入れてから P3 に進む（同じコミット列でよい）。

## （参考）P2 の結果を受けた時点の記録（2026-10-08）

**P2 の結果**（`phase-2-results.md`）
- A1〜A16 は、1 件を除いて PASS。`dotnet test` は 239 件すべて合格。UIA の T1〜T10 も合格。

**所見1 [中]: MapOff の窓へ、離れて 0.1〜0.6 秒以内に戻ると、OFF にならない**
- **原因**（コードで特定）: `AppRuleController.TurnMapOff` が、1 秒ごとにしか読み直さないデバイス一覧のキャッシュ（`_deviceList`）で、「すでに OFF か」を判定している。
  - 直前に戻した（`SetEnabled(true)`）キーボードは、キャッシュの上ではまだ `Enabled=false`。そのため「ユーザーが OFF にした台」とみなして触らない。
  - 決定は MapOff のまま変わらないので、そのまま放置される。1 秒たってキャッシュが更新されると、次の変化から直る。結果の「0.8 秒以上おけば OFF になる」と一致する。
- **修正**
  - `TurnMapOff` の判定の直前に、`ListDevices()` で一覧を取り直す（変化の瞬間にだけ呼ぶので、負担は小さい）。
  - `SetEnabled` が成功したら、キャッシュの `Enabled` も直す（`TurnMapOff` と `RestoreMapOff` の両方）。
- **回帰テスト**（`AppRuleControllerTests`）: 戻して 100ms 後に MapOff の窓へ戻り、`Enabled=false` になること。素早い往復を 6 回しても、最後の状態が窓に合うこと。今のコードでは失敗し、修正後は通ることを確かめる。

**所見2 [軽]: キートップが「⚠Slee / p」に折り返す**
- 原因: `KeyboardCanvas` は、6 文字以下を 12pt で描く。「⚠Sleep」はちょうど 6 文字なので大きく描かれ、収まらない。
- 修正: 短い名前を「⚠Slp」「⚠Pwr」「⚠Wake」にする（`KeycodeCatalog.ShortNames`）。

**修正の後の流れ**
- 2 件の修正を 1 コミットにする。
- `phase-2-fix-handoff.md` を書く。再試験の項目は次のとおり。
  - 所見1 の再現手順（0 / 100 / 250 / 500ms の間隔、素早い往復 6 回）
  - A9・A10・A11 の短い回帰
  - キートップの目視
  - `dotnet test`
- **P3 は、修正の再試験の結果を待たずに並行して進める**（変更箇所が重ならないため）。申し送りは、P3 を別のファイルにする。

## P3: ヘルパー動作（HLP の受信、F13–F24 のフック、動作の実行）の実装手順

**CUSTOM の受信と LL Hook の関係**（ユーザーの質問への回答として明記する）
- ドライバ側（CUSTOM のイベントを積む処理と `WAIT_HELPER_EVENT`）は、P0 で実装と試験が済んでいる。
- トレイで受け取って動作を実行する側（`HelperEventService` と `ActionRunner`）は、まだ無い。P3 で作り、**P3 の中心はこちら**にする。
- **CUSTOM（`HLP(n)` と `Actions`）だけを使う設定では、LL Hook は張らない。**
  - `KeyHookService` は、`Triggers`（F13–F24 などのキーで起動するもの）が 1 件以上あるときだけ張る。0 件なら張らない。設定が変わり、0 件になったら外す。
  - 古いドライバだから、という理由だけで自動で張ることはしない。古いドライバのときは、P4 の割り当て画面が `Triggers` を作る。そのときに初めて張られる。
  - 申し送りの試験項目に、`Triggers` が 0 件のときにフックが無いことの確認を足す（例: 他のツールの LL Hook との順序の影響が無いこと、トレイのスレッド数）。

1. **スキーマを確定する**（`kasane/Model/HelperConfig.cs`。P2 ではスキーマだけ入れてあり、まだ使っていないので変えてよい）
   - `HelperActionBody` の `Type` は、`Launch` / `Open` / `Sequence` / `AppLayer` / `PauseToggle`。
   - `Steps` を型付きの `HelperStep` のリストにする。
     - 項目: `Key`（`LCTL(KC_C)` などのタップ）、`Down`、`Up`、`DelayMs`（最大 10 秒）、`Text`（Unicode）
     - マウス: `Mouse`（`Left`/`Right`/`Middle`/`X1`/`X2` と `Click`/`Down`/`Up`）、`MoveX`/`MoveY` と `Absolute`、`Wheel`/`HWheel`
   - `ReleaseSteps`: 離したときに実行する（マウスボタンを押している間だけ押す、などに使う）。
   - `Repeat`: オートリピートで繰り返すか。
   - `AppLayer` は `Layers` と `LayerOp`（`On`/`Off`/`Toggle`/`Hold`。`Hold` は押している間だけ立てる）。
2. **純粋なロジック**（新規 `kasane-tray/HelperActions.cs`。kasane.Tests にリンクする）
   - `SequenceCompiler.Compile(steps)` は、抽象的な入力の列（`InputOp`：キーの押下・離し（スキャンコードと E0）、Unicode、マウス、待ち）を返す。
     - キーは `KeycodeTable.TryResolve` で解決する。`ModdedKey` は「修飾キーの押下 → キー → 逆順で離す」に展開する（ドライバのマクロと同じ形）。
   - `TriggerMatcher`: `(スキャンコード, E0, 修飾キー, ExtraInfo>>16)` から、キーボードごとの Trigger → 共通の Trigger の順に引く。
     - 修飾キーは、左右を区別せず、Ctrl / Shift / Alt / Win の有無で比べる。
     - キーボードは、`ExtraInfo` の上位 16bit と UniqId の上位 16bit で照合する。
   - `HelperActionResolver`: HLP のイベント `(UniqId, CustomId)` から、キーボードごとの Actions → 共通の Actions の順に引く。
3. **Win32 側**（新規ファイル）
   - **`HelperEventService.cs`**
     - 専用スレッドで、`WAIT_HELPER_EVENT` を Overlapped で投げ続ける。初回の LastSeq は `KBDLAYER_HELPER_SEQ_LATEST`。終了時は `CancelIoEx` で止める。
     - `ERROR_INVALID_FUNCTION`（古いドライバ）が返ったら、止めて記録する。
     - 合成器のハンドルとは別に開く。`KbdLayerClient` に、Overlapped 版の `WaitHelperEvent` を新規の partial ファイルとして足す。
     - 古いイベント（TimeMs が 2 秒より前）と、セッションが非アクティブのときのイベントは、実行しない。
   - **`KeyHookService.cs`**
     - 専用スレッドで `WH_KEYBOARD_LL` を張る。Trigger があるときだけ張る。
     - `LLKHF_INJECTED` の付いたイベントは無視する。
     - 一致した押下と、それに対応する離しを飲み込む（飲み込んだキーを覚えておく）。
     - 実行は ActionRunner のキューに入れるだけにする（LowLevelHooksTimeout を超えないため）。
     - 設定が変わったら張り直す。
   - **`ActionRunner.cs`**: 専用スレッドのキューで動かす。
     - `Launch` / `Open`: ShellExecute。昇格はしない。作業フォルダを指定できる。
     - `Sequence`: `SendInput`。キーは `KEYEVENTF_SCANCODE`（E0 なら `EXTENDEDKEY`）、文字は `KEYEVENTF_UNICODE`。
     - `AppLayer`: 合成器は IME 連動のスレッドでしか触れないので、`AppRuleTick` が持つキューに入れて Poke する（入力源の名前は "action"）。
     - `PauseToggle`: UI スレッド（`TrayContext`）へ Post する。
   - **`TrayContext` / `ImeLinkHost`**: 起動、終了、設定の読み直しを配線する（`AppRuleTick.Reload` と同じ設定の一式を共有する）。
4. **テスト**: `HelperActionsTests` を作る（Compile の展開、Trigger の照合、Actions の引き当て、`Repeat`、`ReleaseSteps`）。
5. **申し送り `phase-3-handoff.md` の試験項目**
   - HLP による `Launch`、`Text`（日本語を含む）、`Sequence` のキーと遅延、マウス
   - F13 の Trigger を飲み込むこと（他のアプリに届かないこと）
   - キーボード 2 台で別々の動作になること
   - 古いドライバでのフォールバック（HLP が使えないとき）
   - 管理者の窓が前面のときは送れないこと（UIPI。仕様として確認する）
   - ロック中は実行しないこと
   - トレイを kill したら、フックが外れること
   - 回帰（P0〜P2）
   - 方法は、dev-notes の T-7、T-9、T-14、T-16、T-23、T-25 に沿う。

## 現在の状況（2026-10-08）

**終わったもの**
- **P0**（`f57644d`）: 全 PASS（H1〜H17、Verifier 下）。dev-notes も更新済み（eb57b27）。
- **P1**（`dcbe9ef`）: 全 PASS（K1〜K9、`dotnet test` 202 件合格、UIA T1〜T10 合格）。`claude/handoff` の `phase-1-results.md`。

**P1 の結果の所見への対応（P2 の最初に入れる）**
1. 電源キー（`KC_PWR` / `KC_SLEP` / `KC_WAKE`）は、今は確認なしで割り当てられる。対応は次のとおり。
   - キー割り当て画面で選んだとき（ボタン・入力欄の両方）に確認を出し、「PC がスリープ／シャットダウンする可能性」を示す。
   - キートップには「⚠」を付ける。
   - 対象は `KeyAssignDialog` の OK 時の判定で、新規に `KeycodeCatalog.IsPowerKey(token)` を設ける。
2. 申し送りのテンプレート（handoff ブランチの `README.md`）に、次の注意を足す。
   - GUI が「ドライバから読み込む」試験では、古い保存先 `%LOCALAPPDATA%\LayerDriver\<UniqId>\project.json` も退避すること。
   - 理由は `ProjectService.MigrateLegacyProject` が古い保存先を読み込むため。
3. dev-notes: P1 で新しい種類の問題は見つかっていない。追記するのは T-2 の由来（試験中に nodoka が再起動した 2 回目の事例）程度で、必須ではない。チェックリストの状況表は変わらない（ドライバの変更なし）。

**次: P2（レイヤーの合成器と、アプリ別レイヤー）**の実装手順
1. **`kasane-tray/AppLayerArbiter.cs`（新規、純粋なロジック）**
   - ドライバの操作は `ILayerStateDriver` 経由で行う。メソッドは `IsOn(uniq, layer)`、`Set(uniq, layer, on)`、`Release()`。
   - 入力源ごとの希望 `(UniqId, Layer)` の集合を持ち、立てたビットの記録（`_applied`）も持つ。
   - `SetSource(name, set)` / `ClearSource(name)` の後に `Reconcile()` で差分だけ ON/OFF する。全入力源が空で、立てたビットも無くなったら `Release()` する。
   - `Verify()` で実際の状態と比べ、再接続で消えたビットを立て直す。
2. **IME 連動のアダプター（`ImeLayerDriver.cs` を書き換える）**
   - `IImeLayerDriver` の実装を、Arbiter の「ime」入力源への橋渡しにする。
   - `FindTargets` は、今どおり短く開いて閉じる。
   - `IsAppLayerOn` は実際の状態を返す。`SetAppLayer` と `Release` は、入力源の希望を変える。
   - **`ImeLinkController` 本体と、`ImeLinkControllerTests` は変えない。**
   - 実ドライバの実装（ハンドルを持ち続ける部分）は、`KbdLayerAppLayerDriver : ILayerStateDriver` として残す。
3. **`kasane/Model/HelperConfig.cs`（新規、kasane と tray でソースリンク）**
   - P2 で扱うのは `AppLayerRules`。`Match` は `Exe`、`PathRegex`、`TitleRegex`、`Class`、`NodokaClass`、`NodokaTitle`。
   - 動作は `Layers[]`、または `MapOff`。`Default` ルールも書ける。
   - キーボードごとの `<UniqId:X8>\helper.json` と、`TrayConfig.Helper`（共通）を読む。
   - P3 で使う `Actions` と `Triggers` も、スキーマだけ先に入れる。
4. **`kasane-tray/ForegroundInfo.cs`（新規）**: 前面のウィンドウから、exe のフルパス、トップレベルのクラスとタイトル、のどか互換のクラス・タイトル連結文字列（`GetGUIThreadInfo` でフォーカスを取り、親をたどる）を取る。
5. **`kasane-tray/AppRuleController.cs`（新規、純粋なロジック）**
   - 前面の情報、ルール、デバイスの一覧から、Arbiter の「rule」入力源の希望を決める。`MapOff` の対象も決める。
   - 一時停止中と、セッションが非アクティブのときは空にする。
   - `MapOff` は `PauseController` と同じく「自分が OFF にした台」を覚えて戻す。一時停止中は触らない。
6. **スレッドとフック**
   - `ImeLinkService` のループで、IME と rule の両方を Tick する（クラス名は変えず、Tick の対象を足す）。
   - `ImeLinkHost` の WinEvent に `EVENT_OBJECT_NAMECHANGE`（前面のものだけ）を足す。`Poke` はデバウンス 150ms。
   - `TrayContext.RefreshState` の `ReloadConfig` で、helper.json と TrayConfig を読み直す（更新時刻を見て、変わったものだけ）。
7. **テスト（xUnit。kasane.Tests にソースリンク）**
   - `AppLayerArbiterTests`
     - IME と rule が同じビットを立てたとき、片方を解除しても残ること。
     - 別のビットを立てたとき、それぞれ独立に動くこと。
     - 全部空になったときだけ `Release()` すること。
     - `Verify` で消えたビットを立て直すこと。
   - `AppRuleControllerTests`: ルールの順序、Exe と正規表現の照合、のどか互換の文字列、`Default`、一時停止、セッション、`MapOff` を戻すこと。
   - 既存の `ImeLinkControllerTests` は、変えずに通ること。
8. **設定 UI は P4**。P2 の試験は、helper.json を手で書いて行う。
9. **申し送り `phase-2-handoff.md` の試験項目**
   - `--watch-layer` で `AppLayerMask` が前面のアプリに追従すること。
   - IME 連動と同時に使い、片方が外れてももう片方が残ること。
   - トレイを kill すると 0 に戻ること。
   - ロックとユーザーの切り替え。
   - 昇格したアプリが前面のときも、パスで照合できること。
   - タイトルの正規表現、`MapOff` と一時停止の関係、`Default`、キーボード 2 台で別々のルール。
   - 進め方は dev-notes の T-25〜T-27（IME の窓、昇格したランナー、ハンドルの確認）に沿う。

## Context

LayerDriver（kbdlayer.sys ＋ GUI `kasane` ＋ ヘルパー `kasane-tray`）に、次の 3 つを加える。

1. 出力キーに F13–F24 を加え、物理キーが無くてもスキャンコードで表せるキーはすべて出力に選べるようにする。
2. ヘルパーが LL Hook で F13–F24 などのトリガーを受け、マクロ（遅延・文字列付き）・マウス操作・アプリ起動などを実行する。
3. フォアグラウンドのアプリを検知して、指定したレイヤーに切り替える。
4. **設定のエクスポート／インポートを広げる**。範囲は「全体一括」「キーボード単位」「レイヤー単位」の 3 つ。
5. **のどか（nodoka4）の設定ファイル（`.nodoka`）をインポートする**。

ドライバはコードフリーズに向かっているので、**ドライバへの機能追加は `CUSTOM`(=11) の「ヘルパーイベント」1 件だけ**にする（第0節）。決定事項は次のとおり。
- **ヘルパー動作の主なトリガーは `CUSTOM`** にする。ドライバはキーを出さず、押下と離しをヘルパーへ通知する。
  - トリガーを数の制限なく作れ、修飾キーが漏れず、ヘルパーが止まっていても何も出ない。昇格したウィンドウの上でも受け取れる。
- **F13–F24 を LL Hook で受けるトリガーも、あわせて用意する。** 用途は次の 2 つ。
  - ユーザーが F13–F24 そのものに動作を割り当てる場合（他のツールが出す F13 などを含む）
  - CUSTOM に対応していない古いドライバでのフォールバック
- キーボードの区別は次の 2 つで行う。どちらでも**設定をキーボードごとに持てる**。
  - CUSTOM：イベントに含まれる UniqId
  - LL Hook：`KBDLLHOOKSTRUCT.dwExtraInfo >> 16`（DeviceId の上位16bit。docs/DEVICE-ID.md §7.1）

## 調査で分かった前提

- **出力キーの表現**: 生の Set-1 スキャンコード（Enc16：上位バイトが `00`/`E0`/`E1`、下位バイトが make）で、`KBDLAYER_ACTION.Param2` に入る。
  - ドライバ側には許可リストが無い（`ParseLayerMapBlob`、`LayerEngine.c:165-260`）。
  - したがって **F13–F24 もメディアキーもブラウザーキーも、ドライバとブロブ形式を変えずに出せる**。
  - 制約になっているのは名前表の側。`driver/KeymapTool/KeycodeTable.cpp` と `kasane/Keycodes/KeycodeTable.cs` に F1–F12 までしかない。
- **ドライバのマクロ**: 押す／離すの列だけ（`KBDLAYER_MACRO_STEP`）。遅延・文字列・マウスは扱えないので、ヘルパー側で補う。
- **アプリ管理レイヤーの土台はドライバに実装済み**（`docs/ARCHITECTURE.md` の「レイヤー制御の双方向通知」）。
  - IOCTL は `GET_LAYER_STATE` 0x811、`WAIT_LAYER_CHANGE` 0x812、`SET_LAYER_STATE` 0x813。3 つとも READ アクセスなので、**昇格していない tray から呼べる**。
  - アプリが立てるレイヤーは `AppLayerMask` で、キーボード側のレイヤーと OR で合成される。
  - 所有者は SET を呼んだハンドルで、そのハンドルが閉じると 0 に戻る。
  - C# のラッパーは main（15d5d92、feature/ime-link をマージ済み）に `KbdLayerClient.GetLayerState` / `SetLayerState` として入っている。定数は `LayerOpOn`、`LayerOpOff`、`LayerOpToggle`、`LayerOpSetMask`、`LayerFlagNoNicolaKeys`。`WAIT_LAYER_CHANGE` のラッパーはまだ無い。
- **kasane-tray の現状**（main 15d5d92）
  - WinForms、net8.0、単一ファイル。HKLM Run で各ユーザーのログオン時に起動する。
  - 一時停止（`PauseController`）とアプリ起動（`AppLauncher`）がある。
  - **IME 連動が入った**。IME がかなモードのときだけ、NICOLA のレイヤーをアプリ管理レイヤーとして立てる。構成は次のとおり。
    - `ImeLinkHost`：UI スレッド側。`EVENT_SYSTEM_FOREGROUND` / `EVENT_OBJECT_FOCUS` の WinEvent フックと `SystemEvents.SessionSwitch` を持ち、アクティブなコンソールセッションのときだけ連動する。
    - `ImeLinkService`：専用スレッドで 100ms / 1s ごとに回る。`Poke()` ですぐに読み直す。
    - `ImeLinkController`：レイヤーをビット単位で ON/OFF し、`_driven` で後始末する。xUnit テストあり。
    - `KbdLayerImeLayerDriver`：ハンドルを開いたまま持つ。`Release()` で閉じると、AppLayerMask が全部 0 に戻る。
  - 設定は `kasane/Model/TrayConfig.cs`（`%LOCALAPPDATA%\applet\kasane\tray.json`、利用者ごと）にある。エディタの設定ダイアログ（NICOLA タブ）が書き、tray が 3 秒ごとのタイマーで `ReloadConfig()` する。
  - キーボードのフックはまだ無い。
  - `KbdLayerClient.cs` / `NativeMethods.cs` / `KeycodeTable.cs` / `TrayConfig.cs` は、kasane から**ソースをリンク**して使っている。
  - **注意**: アプリ管理レイヤーの所有者は、キーボードごとに 1 ハンドルだけ。新しく作るアプリ別レイヤーが別のハンドルで SET_LAYER_STATE を呼ぶと所有を奪い、そのハンドルを閉じると IME 連動のビットも消える。したがって **IME 連動とドライバのハンドルを共有する設計が必須**になる（第3節）。

## 設計

### 0. ドライバ：`CUSTOM` アクションをヘルパーイベントにする（ドライバの変更はこれだけ）

**現状**
- `KBDLAYER_ACTION_CUSTOM`(=11) は、型だけが予約されている。
- `ParseLayerMapBlob` は受け付け（`TYPE_MAX` 以内）、`ResolveLayerAction` の `default:` で「何もしない」として捨てる（`LayerEngine.c:784`）。
- したがって、**古いドライバに CUSTOM 入りのブロブを送っても、キーは何も出ない**（安全）。

**仕様**（`driver/common/kbdlayer_ioctl.h` に追加する）
- **アクション**: `Type = KBDLAYER_ACTION_CUSTOM`、`Param1 = 0`（予約。非0 は `ParseLayerMapBlob` で拒否して、将来のフラグに残す）、`Param2 = ヘルパー動作の ID（0–65535）`。
- **押下時**
  - キーは出力しない。
  - 既存の `HeldKeys` に記録し、離したときも同じアクションとして解決する（レイヤーが変わっても対応が崩れないように）。
  - イベントをキューに積む。オートリピートの押下もフラグ付きで通知する。
- **イベント**

```c
typedef struct _KBDLAYER_HELPER_EVENT {
    ULONG  Seq;        // 単調増加（ドライバ全体）
    ULONG  UniqId;     // DeviceId
    USHORT CustomId;   // Param2
    UCHAR  IsBreak;    // 0=押下, 1=離し
    UCHAR  Flags;      // bit0: オートリピート
    ULONG  Modifiers;  // そのキーボードの物理修飾キーの状態（KBDLAYER_MOD_*）
    ULONG  TimeMs;     // KeQueryInterruptTime 由来
} KBDLAYER_HELPER_EVENT;
```

- **キュー**: ドライバ全体で 1 本のリングバッファにする（例：64 件）。あふれたら古いものから捨て、捨てた件数を返す。
- **新しい IOCTL** `IOCTL_KBDLAYER_WAIT_HELPER_EVENT`
  - 番号は `CTL_CODE(FILE_DEVICE_KBDLAYER, 0x81E, METHOD_BUFFERED, FILE_READ_ACCESS)`。
  - 入力は `{ ULONG LastSeq }`。出力は `{ ULONG Seq; ULONG Count; ULONG Dropped; KBDLAYER_HELPER_EVENT Events[] }`。
  - `LastSeq` より新しいイベントがあれば、すぐに返す。無ければ保留する。
  - 保留の仕組みは `LayerNotify.c` と同じにする。具体的には次のとおり。
    - IoCsq を使う
    - 先にキューへ入れてから Seq を見て、取りこぼしを防ぐ
    - 同時に保留できる数に上限を設ける。Everyone が呼べるので、`KBDLAYER_MAX_PENDING_LAYER_WAITS` と同じ考え方にする
    - キャンセルとハンドルのクローズに対応する
  - 新しい `HelperNotify.c` に置くか、`LayerNotify.c` を一般化する。保留を完了させるのは、イベントを積んだ後（`OutputLock` を外した後）にする。
  - **READ アクセス**にする（昇格していない tray から使うため）。
    - 中身は「ユーザー自身が CUSTOM を置いたキーが押された」ことと修飾キーの状態だけで、任意の打鍵は漏れない。
    - 複数のセッションの tray がいる場合は、全員に配る（Seq 方式）。受け取った側が「アクティブなコンソールのときだけ実行する」判定をする（`ImeLinkHost` の判定を使い回す）。
- **使える場所**
  - 今回はレイヤーマップ（`ResolveLayerAction`）と、コンボの出力に限る。
  - タップダンスとキーオーバーライドの出力は、検証の分岐（`LayerEngine.c:4894-4930`）を見たうえで、手間が小さければ含める。含めない場合は、SET 時に今どおり拒否するか捨てる。
- **対応の判定**: tray と GUI は `WAIT_HELPER_EVENT` が `ERROR_INVALID_FUNCTION` を返したら「非対応のドライバ」と判断し、F13–F24 のトリガーに切り替える。
- **ツール**
  - KeymapTool：トークン `HLP(n)` を解釈し、`--watch-helper` を足す（実機での確認用）。
  - サンプルとして `samples/test-keymap-helper.json` を足す。
- **ドキュメント**: `docs/ARCHITECTURE.md` で「CUSTOM（予約）」としている箇所（:539 付近）を、この仕様に書き換える。
- **再署名**: 必要になる（`setup/EvCodeSign.ps1`）。`docs/RELEASE-CHECKLIST.md` にも項目を足す。

### 1. 出力キーの拡張（kasane ＋ KeymapTool。ドライバは無改修）

- **名前表**: `KeycodeTable.cpp` と `KeycodeTable.cs` に、**同じ順序で**次を追加する（`tools/Check-KeycodeTables.ps1` が順序を検査する。`SC1`/`SCE0`/`SC(..)` の書式を守る）。
  - F13–F23 = `SC1(0x64)`–`SC1(0x6E)`、F24 = `SC1(0x76)`
  - ブラウザー: `WBAK` E06A、`WFWD` E069、`WREF` E067、`WSTP` E068、`WSCH` E065、`WFAV` E066、`WHOM` E032
  - `MAIL` E06C、`MSEL` E06D、`MYCM` E06B
  - 電源系: `SLEP` E05F、`PWR` E05E、`WAKE` E063
  - Intl/Lang: `INT6`–`INT9`、`LANG1`/`LANG2` など。値は実機で確認してから入れる（Windows の VK への対応が配列で違うため）。
- **任意のスキャンコードを書けるようにする**: `TryResolve` が `SC_XXXX` 形式（例 `SC_E06A`）を受け付けるようにする。
  - 逆引きで名前が無いときは `KC_UNKNOWN_XXXX` ではなく `SC_XXXX` を返す。
  - これで、ドライバから読み戻した設定が往復できない問題（`KeycodeTable.cs:387`、`ProjectService.cs:215,480`）も直る。
  - C++ の KeymapTool も同じ規則にする。
- **GUI** `kasane/Keycodes/KeycodeCatalog.cs`
  - 「ファンクション」を `Range(1,24)` にする。
  - 「メディア」カテゴリを広げるか、「ブラウザー／システム」カテゴリを追加する。
  - `KeyAssignDialog` の `TxtToken` は `SC_XXXX` も検証する。`ShortNames` にキー上の表示名を足す。
- **テスト**
  - `kasane.Tests/KeycodeTableTests.cs` に追加分と `SC_` の往復を足す。
  - `T7-OsmKeyAssignRoundTrip.ps1:31-34` はカテゴリ数をハードコードしているので、カテゴリを足したら更新する。

### 2. ヘルパー動作（トリガー → 動作）

**設定の保存先**: キーボードごとに `%LOCALAPPDATA%\applet\kasane\<UniqId:X8>\helper.json` に置く。
- project.json と同じフォルダにする。project.json とは分けるので、Apply（管理者）と無関係に保存して即反映できる。
- 「全キーボード共通」の設定は、既存の `TrayConfig`（tray.json）に `Helper` プロパティとして足す。
  - 読み込みは既存の 3 秒ごとの `ReloadConfig` に相乗りさせ、そこでキーボードごとの helper.json もあわせて読み直す（更新時刻を見て、変わったものだけ）。
  - `TrayConfig` に新しいプロパティを足しても、既存の tray.json はそのまま読める（System.Text.Json は、無いプロパティを既定値にする）。
- helper.json の部分のスキーマ（`Version` を付ける）は次のとおり。

```jsonc
{ "Version": 1,
  "Actions": [ { "Id": 1, "Name": "メモ帳", "Action": { "Type": "Launch", "Path": "notepad.exe" } } ],   // HLP(1) から呼ぶ（CUSTOM）
  "Triggers": [ { "Key": "KC_F13", "Mods": "", "Action": { "Type": "Launch", "Path": "...", "Args": "", "WorkDir": "" } },
                { "Key": "KC_F14", "Mods": "LSFT", "Action": { "Type": "Sequence", "Steps": [
                    { "Key": "LCTL(KC_C)" }, { "DelayMs": 50 }, { "Text": "こんにちは" },
                    { "Mouse": "LeftClick" }, { "MouseMove": [10,0], "Relative": true }, { "Wheel": -120 } ] } } ],
  "AppLayerRules": [ ... 第3節 ... ] }
```

**動作の種類**
- `Sequence`：キー（VK/スキャンコード）、`Text`（`KEYEVENTF_UNICODE`）、遅延、マウスのボタン・移動・ホイールを並べる。
- `Launch`：パスと引数。昇格はしない。
- `Open`：URL やファイルを開く（ShellExecute）。
- `AppLayer`：アプリ管理レイヤーを ON / OFF / トグルする。
- `PauseToggle`：既存の `PauseController` を呼ぶ。
- 出力は `SendInput` で行う。kbdclass を通らないのでドライバのリマップを受けず、ループしない。

**CUSTOM の受け取り**（新規 `kasane-tray/HelperEventService.cs`）
- 専用スレッドで `WAIT_HELPER_EVENT` を投げ続ける。Overlapped で投げ、終了時は `CancelIoEx` で止める。
  - このハンドルは、レイヤー用のハンドル（Arbiter）とは別にする。WAIT は所有権に関係しない。
- 受けたイベントは `(UniqId, CustomId)` から helper.json の `Actions` を引き、`ActionRunner` のキューに入れる。
  - 押下で実行する。離しは「押している間」系の動作（マウスボタンを押し続けるなど）で使う。
  - オートリピートは、動作ごとに「繰り返す／しない」を設定で選べるようにする。
- `KbdLayerClient` に `WaitHelperEvent(lastSeq, CancellationToken)` を足す。既存の `Ioctl()` は同期なので、Overlapped 版を別に作る。

**キーフック**（F13–F24 などのキーをトリガーにする場合。新規 `kasane-tray/KeyHookService.cs`）
- 専用スレッドで `WH_KEYBOARD_LL` を張り、メッセージループを回す。
- `LLKHF_INJECTED` の付いたイベントは無視する（自分の `SendInput` を拾わないため）。
- `dwExtraInfo >> 16` からキーボードを特定し、まずそのキーボードの helper.json、次に共通の helper.json を引く。
- 押下時に `(Key, 現在の修飾状態)` が一致したら、そのキーの押下と離しを**飲み込み**、動作は別スレッドのキューで実行する。
  - フック内で時間を使うと、LowLevelHooksTimeout を超えてフックを外されるため。
- フックが外されたときに備えて、定期的に張り直す。
- 既存の `kasane/Interop/LowLevelKeyboardHook.cs` は飲み込みのロジックの参考にとどめる。tray 用は「一致したものだけを飲み込む」別実装にする。

**修飾キー付きのトリガー**（LL Hook のときだけ）
- GUI で出力を `LSFT(KC_F13)` などにすると、ドライバの `MODDED_KEY` で修飾キーごと出力される。
- 修飾キー自体の押下と離しはアプリにも届く。そのため **Shift/Ctrl の組み合わせだけを推奨**し、Alt/Win は単独で離すとメニューやスタートが開くので UI で警告する。

**キー割り当て画面からの導線**
- `KeyAssignDialog` に「ヘルパー動作」パネルを追加する。
- 動作を選ぶか新しく作ると、**CUSTOM 対応のドライバなら `HLP(n)`** を出力トークンにし、`Actions` に登録する。
- 非対応のドライバなら、空いている F13–F24 と Shift/Ctrl の組み合わせを割り当て、`Triggers` に登録する。
- トークン `HLP(n)` は `KeycodeTable.TryResolve`、`BlobBuilder`、`ProjectService` の逆引きで扱う（Type 11、Param2 = n）。

**設定の反映**: tray の既存の 3 秒ごとの `ReloadConfig` で読み直す（上記）。追加の IPC は要らない。

### 3. アプリ別レイヤー

**ルール**（helper.json の `AppLayerRules`。上から順に評価し、最初に一致したものを使う）

```jsonc
{ "Match": { "Exe": "code.exe", "PathRegex": null, "TitleRegex": null, "Class": null }, "Layers": [3], "NicolaKeys": false }
```

- 一致しないときは何も立てない（mask = 0）。
- 規則の書き方に「既定（どれにも一致しないとき）」も用意する。

**検知**: `ImeLinkHost` にある WinEvent フック（`EVENT_SYSTEM_FOREGROUND` / `EVENT_OBJECT_FOCUS`）とセッションの判定を使い回す。新しいフックは作らない。
- 新規 `kasane-tray/ForegroundInfo.cs`：前面のウィンドウから次を取る。
  - exe のパス（`GetWindowThreadProcessId` → `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` → `QueryFullProcessImageName`。昇格プロセスにも使える）
  - トップレベルのクラスとタイトル
  - のどか互換のクラス文字列とタイトル文字列（第5節）
- タイトルでの判定用に `EVENT_OBJECT_NAMECHANGE` を加える。前面のウィンドウのものに限る。
- 短いデバウンスを入れる（Alt+Tab 中のちらつきを抑える）。

**適用**: IME 連動とハンドルを共有する、レイヤーの合成器を作る。
- 新規 `kasane-tray/AppLayerArbiter.cs`
  - ドライバのハンドルは 1 本だけにし、`KbdLayerImeLayerDriver` が持っていたものを引き継ぐ。
  - 入力源（IME 連動、アプリ別ルール、ヘルパー動作の `AppLayer`）ごとに「立てたい (UniqId, Layer)」の集合を持つ。
  - ドライバのビットは、いずれかの入力源が立てたいかどうかの OR にし、`LayerOpOn` / `LayerOpOff` でビット単位に合わせる。
  - **全部の入力源が空になったときだけ**ハンドルを閉じる（ドライバの更新を妨げないという既存の方針を保つ）。
- **IME 連動はほぼそのまま残す**
  - `IImeLayerDriver` を実装するアダプター（IME 入力源）を Arbiter の上に作り、`ImeLinkHost` でそれを渡す。`ImeLinkController` 本体と、その xUnit テストは変えない。
  - アダプターの各操作の意味は次のとおり。
    - `IsAppLayerOn`：実際のドライバの状態を返す（再接続で消えたときの見直しは、今と同じに働く）。
    - `SetAppLayer`：その入力源の希望を変え、合成し直す。
    - `Release`：その入力源の希望を全部消し、合成し直す。
- **スレッド**
  - `ImeLinkService` の専用スレッドを、アプリ別ルールにも使う（ハンドルを 1 スレッドだけで使う既存の方針を保つ）。
  - 名前を `AppLayerService` に変え、IME と前面アプリの両方を Tick する（ファイル名を変える分は差分が増えるので、クラスは残して Tick の対象を足すだけでもよい）。
  - `Poke()` は前面が変わったときに両方を起こす。
- **新規 `AppRuleController`**（純粋なロジック。偽のドライバでテストする）
  - 前面の情報とルールから、キーボードごとの希望レイヤーを決め、Arbiter のルール入力源に渡す。
  - 一時停止中と、セッションが非アクティブのときは空にする。
- `WAIT_LAYER_CHANGE` のラッパーは、今回は作らない（B-1 のレイヤー表示で必要になる）。

**関連する機能（B-3 を取り込む）**: ルールの動作に「このアプリではリマップを OFF」を足す。
- `SET_LAYERMAP_ENABLED` は READ アクセスで使える。
- `PauseController` と同じく「自分が OFF にしたキーボード」を覚えておき、前面が変わったら戻す。
- 一時停止とは状態を分ける（一時停止中はこのルールを効かせない）。

**設定 UI**: `kasane/Views/SettingsDialog.cs` にタブを 2 つ追加する。
- 「ヘルパー動作」：トリガーの一覧と動作エディタ。マクロの列エディタは `MacroEditorWindow` の UI パターンを流用する。
- 「アプリ別レイヤー」：ルールの一覧。実行中のプロセスを選ぶボタンと、レイヤー番号（0–15）のチェックを置く。
- どちらも「このキーボード／全キーボード共通」を切り替えられるようにし、**OK で helper.json に直接保存**する（管理者は不要）。
- `T9-SettingsDialogTabs.ps1` のタブ数も更新する。

### 4. エクスポート／インポート（全体一括・キーボード単位・レイヤー単位）

**現状**
- エクスポートは `BtnExport`「エクスポート（CLI形式）」だけで、レイヤーだけを一方向に書き出す（`ProjectService.Export`、`ProjectService.cs:516-558`）。
- ほかには nicola.json の読み書き（`NicolaService`）があるだけ。
- project.json はレイヤーを **KLE レイアウトのスロット番号順**に持つので、レイアウトが違うキーボードへはそのまま持っていけない。

**交換形式 `*.kasane.json`**（新規 `kasane/Model/Portable/PortableFormat.cs`）

```jsonc
{ "Format": "kasane-export", "Version": 1, "Scope": "All|Keyboard|Layer", "Exported": "2026-10-07T..", "App": "kasane x.y",
  "Keyboards": [ {
      "UniqId": "0xCDEC0000", "HardwareId": "HID\\VID_..", "LayoutName": "JP109",
      "Layers": [ { "Index": 0, "Keys": { "SC_001E": "KC_B", "SC_E048": "MO(1)" } } ],   // スキャンコードをキーにする（レイアウトに依存しない）
      "Macros": [...], "Combos": [ { "Id": 0, "Key1": "SC_0024", "Key2": "SC_0025", "Output": "KC_ESC" } ],
      "TapDances": [...], "KeyOverrides": [...], "Timing": {...}, "EnabledFeatures": 7, "Nicola": {...},
      "Helper": { /* そのキーボードの helper.json */ },
      "MeasuredScanCodes": null } ],                          // 既定では書き出さない。同じ機器へのバックアップのときだけ選んで含める
  "Tray": { /* tray.json（TrayConfig）：ImeLink と共通の Helper */ } }
```

- **レイヤーとコンボをスキャンコードで持つ**
  - 書き出し：各スロットを `ProjectService.EffectiveScanCode` でスキャンコードにする。KC_TRNS のキーは省く。
  - 読み込み：`ImportFromDriver` にある「スキャンコード → スロット」の逆引き（`ProjectService.cs:415-427`）を関数に切り出して使い回す。
  - これで別のレイアウトにも読み込める。読み込み先のレイアウトに無いキーは、レポートに出す。
- 書き込みは `NicolaService.Save` と同じく、UTF-8（BOM なし）と `UnsafeRelaxedJsonEscaping` で行い、日本語をそのまま読めるようにする。

**範囲ごとの動作**

| 範囲 | エクスポート | インポート |
|---|---|---|
| 全体一括 | `%LOCALAPPDATA%\applet\kasane\*\project.json` と helper.json をすべてまとめ、tray.json（IME 連動の ON/OFF と共通のヘルパー設定）も含める | ファイル内の各キーボードを、接続中のデバイスに対応付ける（UniqId → HardwareId の順で自動で割り当て、対応表で直せる）。置き換え前に今の設定を自動でバックアップする |
| キーボード単位 | 今のデバイスの project.json と helper.json | どの項目を読み込むか（レイヤー、マクロ、コンボ、タップダンス、キーオーバーライド、タイミング、NICOLA、ヘルパー）をチェックで選び、「置き換え」か「追加」かを選ぶ |
| レイヤー単位 | 選んだレイヤー 1 枚と、そのレイヤーが参照するマクロとタップダンス | 読み込み先のレイヤー番号（既存のレイヤーへの上書き／新しいレイヤーとして追加）を選ぶ |

- **追加するときの ID の付け直し**: マクロとタップダンスの ID が重なったら空いている番号に付け直し、トークン `M(n)`／`TD(n)` も書き換える。
  - 上限（マクロ 128、タップダンス 16、レイヤー 16）を超える分は取り込まず、レポートに出す。
- **読み込みはプロジェクトに入れるだけ**（`MainWindow.SetProject(..., dirty:true)`）。
  - ドライバへの反映は、今と同じく「適用」（管理者）で行う。
  - helper 部分だけは確認後に helper.json へ直接書く。
- **UI**: 新規の `kasane/Views/ImportExportWindow.cs` を作る。範囲の選択、項目のチェック、対応表、プレビュー兼レポートをまとめた画面にする。
  - MainWindow には「インポート／エクスポート...」ボタンを 1 つ足す（`BtnExport` の隣）。
  - レイヤー一覧（`LstLayers`）の右クリックメニューに「このレイヤーをエクスポート／インポート」を足す。
  - 既存の CLI 形式エクスポートは残す。

### 5. のどか（`.nodoka`）インポート

nodoka4 を調べて分かったこと（パスは nodoka4 リポジトリのもの）。
- **文法の典拠**: `doc/syntax.txt` は古く、`combo`、`taphold`、`tapdance`、`K0-`〜`K7-` などが載っていない。正しいのは `doc/CUSTOMIZE-ja.html` と、パーサーの `nodoka/parser.cpp` / `nodoka/setting.cpp`。
- **キーとスキャンコードの対応**: `def key` で設定ファイルの中に定義される（`dot.nodoka/109.nodoka`、`104.nodoka`）。ハードコードせず、**include された定義を読む**。
- **キーボードの区別**: `def option UnitID = Kn ExtraInfo 0xHHHH0000` の値は、**kasane の DeviceId と同じハッシュ**（FNV-1a 16bit。`d2/common/hash.c` と kasane のコードは同一）。そのまま UniqId に対応付けられる。
- **有効な設定ファイル**: `HKCU\Software\appletkan\nodoka` の `.nodokaIndex` → `.nodoka<N>` = `"表示名;パス;-DSYM;..."` で決まる。
- **include の探し方**: 設定ファイルのフォルダ → `%NODOKA%` → `%HOME%` → `%USERPROFILE%` → `%HOMEDRIVE%%HOMEPATH%` → nodoka.exe のフォルダ。
- **文字コード**: BOM（UTF-16LE/BE、UTF-8）→ 厳密な UTF-8 → CP932 の順に判定する。

**構成**（新規 `kasane/Import/Nodoka/`。WPF に依存しない純粋なロジックにして、単体テストできるようにする）

1. `NodokaSource`
   - 文字コードを判定してファイルを読む。
   - include を上の順で探し、循環を検出する。
   - 行末の `\` による継続行をつなぐ。
2. `NodokaTokenizer`
   - `parser.cpp` を再現する。前置トークンの最長一致（`E0- S- C- ... M0- MAX- L0- LA- K0- IC- ...`）、`"..."`/`'...'` とエスケープ、`/regex/`、`#` コメント、数値（0x と 8 進）、`(,,)` の空引数を扱う。
3. `NodokaParser`
   - **扱う文**: `if/elseif/else/endif/and/or`（`define` と registry/UI の `-D` によるシンボル）、`include`、`def key/alias/subst/option/mod/sync`、`keymap/keymap2/window`（親、既定動作）、`key`（複数の左辺、`key MODS =` による既定修飾の変更）、`mod`（`= += -=`、`! !! !!! !!!!`）、`keyseq`、`combo`、`taphold`、`tapdance`、`event`。
   - **修飾キーの既定値を正しく再現する**: 左辺では `S C A W M0-9 IC` が `~`、ほかは `*`。右辺では `S C A W` が `~`。
   - **大小文字**: 関数名は区別する。キー名、キーマップ名、シンボルは区別しない。
   - **同じキーへの割り当て**: 後に定義したものを優先する（`keymap.cpp` の `addAssignment` と同じ）。
4. `NodokaConverter`：のどかのモデルを「スキャンコードをキーにした交換モデル」（第4節と同じ `PortableKeyboard`）と HelperConfig に変換する。読み込む部分（第4節）は共通にする。

**変換規則**（主なもの）

| のどか | kasane |
|---|---|
| `key *X = *Y`（Global） | レイヤー0：X → Y |
| `key X = Y`（修飾キーの既定は `~`） | レイヤー0：X → Y として近似する（Shift+X などが元のキーのまま残る違いをレポートに出す） |
| 左辺に押下中の修飾キー（`key C-A = Home`） | KeyOverride（Mods = C、A → Home） |
| 右辺に修飾キー（`S-_8`） | `LSFT(KC_8)`（MODDED_KEY） |
| 右辺が複数の動作 | マクロ（押下と離しの意味は §engine `generateKeySeqEvents` に従う。64 ステップまで） |
| `mod modN = !X` | X に `MO(Mn 用のレイヤー)`。`Mn-` 付きの割り当ては、そのレイヤーへ置く |
| `mod modN = !!X` | `LT(Mn 用のレイヤー, X)` |
| `mod shift += !!Space`（SandS など） | `MT(LSFT, KC_SPC)` |
| `mod control += 英数` と `key *英数 = *LControl` | レイヤー0：英数 → LCtrl |
| `&Toggle(Ln)` / `Ln-` 付きの割り当て | `TG(Ln 用のレイヤー)` と、そのレイヤーへの配置 |
| `&Prefix(km)` / `keymap2 km` | `OSL(km 用のレイヤー)`。km の割り当てはそのレイヤーへ置く |
| `&EditNextModifier(M-)` | `OSM(LALT)` |
| `&Default` / `&KeymapParent` / `&OtherWindowClass` | 実行時の処理は無く、透過（KC_TRNS）として平坦化する |
| `&Ignore` / `&Undefined` | `KC_NO` |
| `&VK(F13)` など、スキャンコードがある VK | そのキー |
| `combo A B = ...`（2 キー） | Combo。3 キー以上は対象外としてレポートに出す |
| `tapdance X tap1= tap2= tap3=` | TapDance |
| `taphold X tap= hold=` | hold が修飾キーなら MT、レイヤーなら LT。それ以外は TapDance の SingleTap/SingleHold |
| `window W /regex/ : Global` | W 用のレイヤーを作り、helper の `AppLayerRules` にのどか互換の正規表現ルールを追加する（下記） |
| 親を持たない `window` | helper のルールで「マップ OFF」（不足機能 B-3 を本編に取り込む） |
| `&ShellExecute`、`&SendText`、`&Wait`、`&VK(LButton)`、`&MouseMove/Wheel`、`&SetForegroundWindow`、`&WindowMinimize/Maximize/Close` | helper 動作として `HLP(n)` にする（第0節・第2節）。数の上限は無い。非対応のドライバでは F13–F24 を割り当て、足りない分はレポートに出す |
| 修飾キーとキーの列に `&` 関数が混ざったもの（例 `S-End C-X &Sync ...`） | 列全体を helper の `Sequence` 動作として `HLP(n)` にする（遅延や文字列も扱える） |
| `K1-`〜`K7-` と `UnitID ExtraInfo` | そのハッシュに一致するデバイスのプロジェクトに振り分ける（全体一括インポートの対応表で確認する） |
| `def subst` | KeyOverride、または基本の置き換え |
| `def option TapHoldThreshold`、`TapDanceTimeout`、`ComboWindow` | TapTerm、TapDanceTerm、ComboTerm |
| `IL-`（IME ON）付きの割り当て | 推奨：IME ON 用のレイヤーにまとめ、IME 連動でそのレイヤーを立てる。そのために TrayConfig にキーボードごとの「IME ON のときのレイヤー」を足し、IME 入力源を NICOLA 以外のレイヤーにも広げる。採用しない場合は、変換しないものとしてレポートに出す |
| そのほかの IME と状態の条件（`IC- KL- IH- ...`、`NL- CL- SL-`、`MAX- MIN-`、`T*-`、`R-`）、`&Repeat`、`&Clipboard*`、`&EmacsEditKillLine*`、`&LoadSetting`、`&HelpMessage`、`&PostMessage`、`&MouseHook`、`&Window*` の大半、`&SetIme*` | 変換しない。**どれも ファイル名:行 付きでレポートに出す**。`$ToggleIME` のように中身が普通のキーなら、変換できる |

**レイヤーの割り当て**
- レイヤー0 は Global にする。
- 1〜15 は、使われている `M0-9`、`L0-F`、Prefix キーマップ、window キーマップに、出てくる順で割り当てる。
- 16 を超えたら、UI で取り込むものを選ばせる。
- 複数の `Mn` を同時に押す組み合わせは扱わず、レポートに出す。

**helper の拡張**（第3節の `AppLayerRules` に追加）
- `Match.NodokaClass`／`Match.NodokaTitle` を追加する。
- `ForegroundWatcher` は、のどかと同じ `"<exeのフルパス>:<トップレベルのクラス>:...:<フォーカスのクラス>"` という文字列を作る（`GetGUIThreadInfo` でフォーカスのあるウィンドウを取り、親をたどる）。それに対して、大小文字を区別しない部分一致の正規表現で判定する。
- `&&` と `||` の結合も再現する。

**UI**: ImportExportWindow の「のどか設定を取り込む」から開く。
1. registry からのどかの設定一覧を読み、どれを取り込むか選ぶ。ファイルを直接指定することもできる。
2. シンボルを確認して編集する。
3. 変換結果を「変換できたもの／近似したもの／変換できないもの」に分けて、ファイル名と行番号付きで見せる。
4. 読み込み先のデバイスを選ぶ。
5. 第4節と同じ読み込み処理に渡す。

**テスト**
- nodoka4 のサンプル（`dot.nodoka/109.nodoka`、`default.nodoka`、`emacsedit.nodoka`、`contrib/other.nodoka` の SandS 例など）を `kasane.Tests/Fixtures/nodoka/` に置く。いずれも同じ組織のリポジトリだが、`dot.nodoka/LICENSE.txt` の条件は確認する。
- 次を xUnit で試す。
  - トークナイザー：継続行、引用符、前置トークン
  - `if` と `define` の評価
  - `def key` の読み込み
  - 変換規則ごとの期待値
  - `window` → レイヤーとルール
  - `K1-` → デバイスへの振り分け
  - レポートの内容

### 6. コードの共有とテスト

- 新規の `kasane/Model/HelperConfig.cs`（スキーマ、読み書き、検証）を、kasane と tray の両方からソースリンクする（既存の csproj:28-30 と同じ方式）。
- 純粋なロジックはインターフェースで切り出し、`kasane.Tests` からリンクして xUnit で試す（`PauseControllerTests` と同じ偽物パターン）。対象は次の 3 つ。
  - `TriggerMatcher`（キー、修飾、DeviceId → 動作）
  - `AppRuleMatcher`
  - `AppLayerArbiter`（偽の `ILayerStateDriver`）。次の 2 点を必ず試す。
    - IME 連動とアプリ別ルールが同じキーボードの同じレイヤー、または別のレイヤーを立てたとき、片方の解除がもう片方を消さないこと。
    - すべての入力源が空になったときだけハンドルを閉じること。
  - 既存の `ImeLinkControllerTests` は、変更せずに通すこと。

## 進め方（開発担当＝クラウド、ビルド・インストール＝ユーザー、試験＝ローカルの試験担当）

**フェーズごとの流れ**
1. **開発担当（クラウド）が実装する**
   - 作業ブランチ `claude/tender-euler-xxfl7a` にコミットして push する。
   - 申し送りブランチに `phase-N-handoff.md` を書いて push する（下記）。
2. **ユーザーがビルドしてインストールする**: 作業ブランチを pull してビルド・インストールし、試験担当に申し送る。
3. **試験担当（ローカル）が試験する**
   - dev-notes の `input-injection-testing/README.md` に沿って試験する（OIB 注入、testsink、実行前チェック、T-1〜T-28）。
   - 結果を `phase-N-results.md` に追記して push する。
4. **開発担当が結果を読む**: fetch して結果を読み、不具合を直すか次のフェーズへ進む。
   - **次のフェーズに進むのは、そのフェーズの結果が返ってきてから**にする。

**申し送りブランチ** `claude/handoff`（LayerDriver の orphan ブランチ）
- main とは履歴を共有せず、**main にも作業ブランチにもマージしない**。中身は申し送りと結果のファイルだけにする。
- ローカルでは、例えば `git worktree add .plans/handoff claude/handoff` とすれば、`.plans/` の配下（.gitignore 済み）に置ける。試験担当は、今までと同じようにファイルへ追記できる。
- **ファイル**
  - `README.md`：運用のルール
  - `phase-N-handoff.md`：開発担当が書く
  - `phase-N-results.md`：試験担当が書く
  - `findings.md`：新しく分かったことの下書き
- **`phase-N-handoff.md` に書く内容**
  - 対象コミット
  - 変更点
  - ビルドとインストールの手順と、注意点（ドライバの再署名の有無など）
  - 試験項目：期待値、注入スクリプトの案、使う T-番号
  - 静的にしか確認していない点
  - 試験担当への注意（UIPI、IME の窓、昇格のランナーなど）

**知見の扱い**（dev-notes の README の運用に合わせる）
- **ドライバとリマップエンジンに手を入れる前に、必ず全部読む**
  - `kbd-filter-driver/review-checklist.md`
  - `key-remap-engine/README.md`
  - `input-injection-testing/README.md`
- 指摘や設計の判断には、項目の ID（例：`B-5`、`T-14`）を付ける。
- 開発や試験で新しく分かったことは、dev-notes の該当する文書に追記し、由来（日付と一次資料）を書く。
  - 一次資料が `.plans` にしか無い場合は、申し送りブランチのファイル名を書く。
  - dev-notes では、追記を commit して main に push する（ローカルの CLAUDE.md の指示どおり）。
- 公開リポジトリ（nodoka4 など）には、dev-notes の内容やパスを書かない（dev-notes の README のとおり）。
- **LayerDriver のローカルの `CLAUDE.md`**（受け取り済み）に従う。要点は次のとおり。
  - 名前の使い分け
    - 製品名とユーザーモードは kasane（`kasane/`、`kasane-license*/`、`kasane-launcher/`、`kasane.sln`）。
    - ドライバの内部名は kbdlayer のまま（`driver/kbdlayer`、サービス名 kbdlayer）。新しいコードと文書もこの使い分けに従う。
  - **driver/kbdlayer を修正・レビューする前に**、`kbd-filter-driver/review-checklist.md` を読む。
    - `/kbd-filter-review` スキルはローカルにしか無いので、クラウドではチェックリストを最初から最後まで読み、項目ごとに自分で照らし合わせる。
  - 新しい種類の問題を見つけたとき、またはチェックリストの「ドライバ別の状況」表で kbdlayer の状態が変わったとき（P0 で CUSTOM を実装した後の該当項目など）は、チェックリストを更新して dev-notes に commit / push する。

## ブランチ

- **起点**: 最新の `origin/main`（15d5d92。IME 連動と NICOLA の GUI 例をマージ済み）から、作業ブランチ `claude/tender-euler-xxfl7a` を切る。
  - 作業はすべてこのブランチで行い、push する。main には直接 push しない。
  - 作業中に main が進んだら、push の前に `git fetch origin main` して取り込む（このブランチは自分で作るので、rebase してよい）。
- **PR**: ユーザーから指示があるまで作らない。
- **コミットの単位**: 段階ごとにまとめる（P1〜P7）。必要なところだけを取り込んだり、段階ごとにレビューしたりできるようにする。
- **既存ファイルへの変更の方針**（レビューしやすくするため）
  - 新機能は、なるべく新しいファイルに置く。`SettingsDialog`、`KeyAssignDialog`、`TrayContext` への変更は、登録の数行に寄せる。
  - キーコード表は、既存の行を並べ替えずに末尾へ追加する。
  - 既存のコードは整形しない。改行コードと BOM は既存ファイルに合わせる。
- **このクラウド環境の制約**
  - Windows も .NET の Windows ターゲットも MSBuild も無いので、ここではビルドもテストもできない。
  - そのため、コンパイルが通るかと実機での確認は、ユーザーの環境で行ってもらう前提になる。こちらでは静的に注意深く確認し、`Check-KeycodeTables.ps1` 相当の整合（名前、値、順序）をスクリプトで確かめる。
  - 確認できていない点は、コミットメッセージと最終報告に明記する。
- **このクラウド環境の制約**
  - Windows も .NET の Windows ターゲットも MSBuild も無いので、ここではビルドもテストもできない。
  - そのため、コンパイルが通るかと実機での確認は、ユーザーの環境で行ってもらう前提になる。こちらでは静的に注意深く確認し、`Check-KeycodeTables.ps1` 相当の整合（名前、値、順序）をスクリプトで確かめる。
  - 確認できていない点は、コミットメッセージと最終報告に明記する。

## 主な変更ファイル

- **ドライバ（P0）**
  - `driver/common/kbdlayer_ioctl.h`
  - `driver/kbdlayer/LayerEngine.c`（CUSTOM の分岐と HeldKeys）
  - 新規 `driver/kbdlayer/HelperNotify.c`、または `LayerNotify.c` の一般化
  - `ControlDevice.c`（IOCTL の振り分けとクリーンアップ）、`DeviceExtension.h`、`kbdlayer.vcxproj` / `kbdlayer_arm64.vcxproj`
  - `driver/KeymapTool/{KeymapJson,BlobBuilder,IoctlClient,Main}.cpp`
  - `docs/ARCHITECTURE.md`、`docs/RELEASE-CHECKLIST.md`
- **キーコード**
  - `kasane/Keycodes/KeycodeTable.cs`、`driver/KeymapTool/KeycodeTable.cpp`（表と、`SC_` / `HLP(n)` の構文）
  - `kasane/Keycodes/KeycodeCatalog.cs`、`kasane/Views/KeyAssignDialog.xaml(.cs)`
- **ドライバとの通信**: `kasane/Interop/NativeMethods.cs`（SendInput、LL Hook、プロセス情報）。Layer State の IOCTL は main にあるものを使う
- **共有モデル**
  - 新規：`kasane/Model/HelperConfig.cs`
  - 変更：`kasane/Model/TrayConfig.cs` に `Helper` プロパティを足す
- **設定画面**: `kasane/Views/SettingsDialog.cs`（タブ 2 つ。中身は新規ファイルに置く）
- **tray**
  - 新規：`KeyHookService.cs`、`ActionRunner.cs`、`ForegroundInfo.cs`、`AppLayerArbiter.cs`、`AppRuleController.cs`、`HelperConfigCache.cs`
  - 変更
    - `ImeLayerDriver.cs`：Arbiter の上のアダプターにする
    - `ImeLinkHost.cs` / `ImeLinkService.cs`：前面アプリの入力源を足す
    - `TrayContext.cs`：起動と終了、`ReloadConfig` の対象を広げる
    - `kasane-tray.csproj`
- **エクスポート／インポート**
  - 新規：`kasane/Model/Portable/*.cs`（形式、書き出し、読み込みと ID の付け直し）、`kasane/Views/ImportExportWindow.cs`
  - 変更：`ProjectService.cs` から「スキャンコード → スロット」の逆引きを切り出す。`MainWindow.xaml(.cs)` にボタン 1 つと、レイヤーの右クリックメニューを足す
- **のどかインポート**: 新規 `kasane/Import/Nodoka/*.cs`、`kasane.Tests/Nodoka*Tests.cs`、`kasane.Tests/Fixtures/nodoka/*`
- **テストとドキュメント**
  - `kasane.Tests/*`、`UiAutomation/T7`、`T9`
  - `docs/ARCHITECTURE.md`（tray の節）、`docs/TUTORIAL-GUI.md`

## 段階

| 段階 | 内容 |
|---|---|
| P0 | ドライバ：CUSTOM のヘルパーイベントと `WAIT_HELPER_EVENT`。KeymapTool の `HLP(n)` と `--watch-helper`。フリーズの前に最初に入れる。着手する前に、dev-notes のチェックリストを全部読み、各項目（B-5 の IRP 保留、D-6 など）に照らした自己レビューの結果を申し送りに付ける |
| P1 | 出力キーの拡張（表、`SC_`、カタログ、テスト）。単独でリリースできる |
| P2 | `AppLayerArbiter` を入れて IME 連動をその上へ移す（既存の ImeLinkController のテストが通ること）。続けて前面アプリのルール（`AppRuleController`、`ForegroundInfo`）と helper.json / TrayConfig の読み込みを入れる |
| P3 | `HelperEventService`（CUSTOM）、`KeyHookService`（F13–F24）、`ActionRunner`（Launch/Open/Sequence/Text/Mouse/AppLayer） |
| P4 | 設定ダイアログの 2 タブと KeyAssignDialog の「ヘルパー動作」導線 |
| P5 | エクスポート／インポート（交換形式、全体／キーボード／レイヤー、ImportExportWindow） |
| P6 | のどかインポート（Source/Tokenizer/Parser/Converter、helper の NodokaClass 照合、UI、テスト） |
| P7 | ドキュメント（`docs/HELPER.md`、`docs/IMPORT-NODOKA.md`）、UIA テスト、インストーラーの確認（tray の構成は変わらない） |

## 検証

- **ビルドと単体テスト**
  - `kasane.sln` をビルドし、`dotnet test kasane.Tests` を通す。
  - `tools/Check-KeycodeTables.ps1` が成功することを確認する。
- **実機**
  0. **ドライバ（P0）**
     - `HLP(5)` を置いたキーを押すと、`KeymapTool --watch-helper` に押下と離しのイベントが出て、アプリには何も届かないことを確かめる。
     - オートリピート、ハンドルのクローズ中、キーボードの抜き差し、保留数の上限での動作を確かめる。
     - 古いドライバでは、そのキーが何も出さないこと（今の挙動）を確かめる。
     - Driver Verifier を有効にして動かし、問題が出ないことを確かめる。
  1. GUI で F13–F24、ブラウザーキー、`SC_E06A` を割り当てて Apply する。キーボードビューアや `KeymapTool --get` で出力と往復を確かめる。
  2. tray を動かした状態で F13 にメモ帳の起動を割り当てる。押すとメモ帳が起動し、他のアプリには F13 が届かないことを確かめる。tray を止めると F13 が素通りすることも確かめる。
  3. キーボードを 2 台つなぎ、同じ F13 に別の動作を割り当てる（helper.json をキーボードごとに置く）。それぞれ正しく分かれることを確かめる。
  4. `code.exe` → レイヤー3 の規則を作り、フォーカスを切り替えて `KeymapTool --watch-layer` で `AppLayerMask` の変化を確かめる。tray を kill すると mask が 0 に戻ることも確かめる。
  5. 昇格したウィンドウ、ロック画面、ユーザーの高速切り替えでの動作を確かめる（第3節のセッション処理）。
     - **IME 連動と同時に使う**: NICOLA のキーボードで IME をかなにしたまま、ルールのあるアプリへ切り替える。両方のレイヤーが立ち、片方が外れてももう片方が残ることを `--watch-layer` で確かめる。
  6. **エクスポート／インポート**
     - 全体一括で書き出し、設定を消してから読み込むと、元に戻ることを確かめる。
     - JP109 のキーボードで書き出したレイヤーを US104 のキーボードへ読み込み、同じスキャンコードのキーに割り当てられることと、無いキーがレポートに出ることを確かめる。
     - マクロ ID が重なる状態で「追加」を選び、番号が付け直されることを確かめる。
  7. **のどか**
     - 実際に使っている `.nodoka` を registry から選んで取り込み、レポートを確認する。
     - SandS、英数→Ctrl、Emacs 風キーマップ（window）が期待どおり動くことを確かめる。
     - 適用後に、のどかと同じ打鍵になることを確かめる。
- **UIA**: T7、T9、T10 を回し直す。

---

## 不足機能の洗い出し（プラン化するかの判断材料）

### A. ドライバ（フリーズ前に判断が要るもの）

1. ~~`CUSTOM`(=11) を「ヘルパーイベント」にする~~ → **採用**。本編の第0節（P0）に入れた。
   - 以下の 2〜5 はいずれもヘルパーで代わりがきくか、優先度が低いので、フリーズでは見送る想定。
2. **マクロの遅延と押しっぱなし**: `MACRO_STEP.Reserved` を遅延（×10ms など）に使う。ヘルパー（`HLP(n)` の Sequence）で代わりがきくので、優先度は中。
3. **3 キー以上のコンボ**（ARCHITECTURE.md:549 で未実装と明記）。優先度は中〜低。
4. **アプリ管理レイヤーの所有者が 1 つだけ**: tray の Arbiter で集約すればドライバの変更は要らない（IME 連動とアプリ別ルールは、本プランで集約する）。ただしサードパーティのアプリと共存させるなら、所有者のスロットを複数にする案がある。優先度は低。
5. **Caps Word、Leader キー、Auto Shift などの QMK 系機能**: 要望があれば。優先度は低。

### B. ヘルパーと GUI（ドライバは無改修でできる）

1. **レイヤー表示**：`WAIT_LAYER_CHANGE` を使い、トレイアイコンの数字や OSD で今のレイヤーと NICOLA の状態を出す。小さく作れて体感の効果が大きい。
2. ~~IME 連動~~ → main に実装済み（feature/ime-link）。本プランでは Arbiter の上に載せ替える。
3. ~~アプリ別にリマップを無効にする~~ → 本編の第3節に取り込んだ。
4. **昇格したウィンドウへの対応**：昇格していない tray の LL Hook と `SendInput` は、管理者ウィンドウの前面では効かない（UIPI）。
   - 署名済みで Program Files に置いているので、manifest に `uiAccess=true` を付ければ解消できる。
   - CUSTOM（第0節）でトリガー側の問題は消えるが、`SendInput` 側は uiAccess が必要なまま。
5. **一時停止のホットキーと、トレイメニューからのレイヤー手動切り替え**。
6. **インストール直後に tray を起動する**：今は次回ログオンまで起動しない。MSI の終了時に起動する CA を入れる。
7. ~~設定のエクスポートとインポート~~ → 本編の第4節に格上げした。
8. **`KC_UNKNOWN_XXXX` が往復できない問題**：P1 の `SC_` 構文で解消する。
