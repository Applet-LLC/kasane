# P3.1 申し送り: HLP が捨てられる不具合ほか

- 作業ブランチ: `claude/tender-euler-xxfl7a`、対象コミット: `e381e65`
- 書いた日: 2026-10-08（開発担当）。**こちらでは未ビルド**（構文だけパーサーで確認）。
- `phase-3-results.md` のとおり、`d0e54d6` では HLP(id) の押下が全部捨てられていた。原因の特定と1行修正案をありがとう。

## 修正

1. **[高] HLP の押下が「古すぎる」と判定されて捨てられる**
   - 提案の `long` のままの引き算は、起動から 49.7 日を超えると（`TimeMs` は 32bit で巻き戻る）逆にすべて古いと判定するので、**32bit の差を符号付きで**見る形にした: `HelperEventTiming.IsStale(tick, timeMs)` = `(int)((uint)tick - timeMs) > 5000`。単体テストで、実機の値（`4700796` と `4700808`）、境界（5000／5001）、未来、32bit の巻き戻りを確かめる。
2. **ロック画面から戻った直後に HLP が動く**（B17 の所見1）: `ConsoleConnect` / `RemoteConnect` / `SessionLogon` で、入力デスクトップに触れるか（`OpenInputDesktop`。ロック画面ではアクセス拒否）も見る。ロック画面の間は非アクティブのまま、`SessionUnlock` で戻す。
3. **キュー**（B19 の所見2）: 解放が要る動作（`ReleaseSteps` のある Sequence、Hold の AppLayer）だけ、解放をキューに積む。キューは押下 64 件で打ち切り、解放は 256 件まで受ける。
4. `AppLayer: Layers が空です` を押下のときだけ出す（所見4の1つ目）。

見送り: 構文エラーの helper.json でログが出ない件は、P4 の設定画面の検証で扱う。`ConsoleDisconnect` の 1.3 秒後の HLP は、切り替え中の遅延として様子を見る。

## ビルドとインストール
- `kasane.sln` をビルドし、インストールする。**インストール後に ProductVersion が `…+e381e65…` になっていることを確かめる**（T-32）。ドライバは変更なし。

## 試験項目（今回は短い再試験）

| # | 内容 | 期待値 |
|---|---|---|
| R1 | `dotnet test kasane.Tests` | すべて合格（新規: `IsStale_*`、`NeedsRelease_*`、`AppLayer_WithoutLayers_LogsOnPressOnly`） |
| R2 | **インストール版で** B2: HLP(1) → `Launch` | メモ帳が1回だけ起動。解放では何も起きない。`--watch-helper` にも出る |
| R3 | **インストール版で** B3・B5・B6・B7 を HLP 経由で（`Launch` の引数・`Text`・マウス・`ReleaseSteps` の押している間だけ押す） | パッチ版と同じ結果 |
| R4 | B8: HLP の `Repeat` の有無（キーを押し続ける） | `false` は1回、`true` は繰り返す |
| R5 | B9: HLP の `AppLayer` の On / Off / Toggle / Hold | 期待どおり。Hold は押している間だけ |
| R6 | B10: HLP の `PauseToggle`、B12: 2 台で HLP(1) が台ごとに別の動作 | 既存どおり |
| R7 | B17: **ユーザーの切り替えで戻った直後**（ロック画面が出ている間）に HLP のキーを押す | `SessionUnlock` より前は実行されない。パスワード入力後（解除後）は動く。Win+L のロック・解除は従来どおり |
| R8 | B19: 100 回連打（`ReleaseSteps` を持つ動作と持たない動作の両方）／ `Hold` を連打 | クラッシュしない。ボタンが押されたままにならない。`ReleaseSteps` の無い動作では、キューが溢れにくい（`tray.log` の「捨てました」が B19 の 135 行より大幅に少ない） |
| R9 | 回帰: P2（A3・A9 の短いもの）、一時停止（T10）、`Triggers`（B11・B14） | 既存どおり |
| R10 | 古い HLP の取りこぼし確認: HLP のキーを押して**すぐ**別の窓へ切り替える／1 秒に何度も押す | 押した分が実行される（`IsStale` で捨てられない）。`tray.log` に異常なし |

## 試験担当への注意
- HLP の試験は、`Actions` を helper.json に書いて 3〜4 秒待ってから押す。
- R7 は、ロック画面が出ている間の実行を見るので、時刻つきのログ（`tray.log` ではなく、起動した動作が書くファイルなど）で、`SessionUnlock` の前後を比べる。
