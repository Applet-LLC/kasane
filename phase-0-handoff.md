# P0 申し送り: ヘルパーイベント（CUSTOM / `HLP(id)`）

- 作業ブランチ: `claude/tender-euler-xxfl7a`
- 対象コミット: `f57644d`（main `15d5d92` の上）
- 書いた日: 2026-10-08（開発担当）

## 何が変わったか

レイヤーマップに `HLP(id)`（`KBDLAYER_ACTION_CUSTOM`=11、id は 0〜65535）を置いたキーは、**キーを何も出さず**、押下・オートリピート・解放を「ヘルパーイベント」として、新しい IOCTL `IOCTL_KBDLAYER_WAIT_HELPER_EVENT`（0x81E、READ アクセス、昇格は不要）で待っているアプリへ知らせる。P2 以降でトレイがこれを受け、アプリの起動・文字列の入力・マウス操作などを行う。ドライバに足す機能は、フリーズ前のこれ1つだけ。

- ドライバ
  - `driver/kbdlayer/HelperNotify.c`（新規）: ドライバ全体で 1 本のイベントのリング（64 件）と、保留 IRP のキャンセル安全キュー（最大 16）。`LayerNotify.c` と同じ形。
  - `LayerEngine.c`
    - `LayerEngineShouldDropEntry` に `CUSTOM` のケースを追加した。キーを捨ててイベントを積む。OSL は KEY と同じく消費する。
    - `ParseLayerMapBlob`: `CUSTOM` の Param1 が 0 でなければ拒否する。
    - コンボのメンバーが `CUSTOM` のとき、コンボが成立しなかった場合の復元は「何も出さない」。
    - `LayerNotifyIfChanged` の先頭で、積んだイベントを待っている IRP へ渡す（ロックの外）。
  - `ControlDevice.c`: `WAIT_HELPER_EVENT` のハンドラ。ハンドルのクローズで保留 IRP を終わらせる。
  - `common/kbdlayer_ioctl.h`: `KBDLAYER_HELPER_EVENT`（20 バイト）、入出力の構造体、IOCTL。
  - `kbdlayer.vcxproj` / `kbdlayer_arm64.vcxproj` に `HelperNotify.c` を追加した。
- KeymapTool
  - キーマップのトークン `HLP(id)`（10 進、または `0x` 付きの 16 進）。
  - `--watch-helper`: 全キーボードのヘルパーイベントを 1 行ずつ表示する。行ごとに `fflush` するので、リダイレクトしても強制終了で行は失われない（T-23）。
  - `samples/test-keymap-helper.json` と、`samples/README.md` の確認項目。
- ドキュメント: `docs/ARCHITECTURE.md`（「ヘルパーイベント（CUSTOM）」の節）、`docs/RELEASE-CHECKLIST.md`。
- GUI（kasane）とトレイは**変えていない**（P1〜P4）。GUI で HLP を含むマップを読み込むと正しく表示されない可能性がある。P0 の試験は KeymapTool だけで行う。

## ビルドとインストール

- `kasane.sln` で `kbdlayer`（x64。ARM64 は任意）と `KeymapTool` をビルドする。**新しいソースファイル `HelperNotify.c` が増えた**ので、プロジェクトの読み込み直しが必要なことがある。
- ドライバの版番号（`kbdlayer.inf` の `DriverVer`、`kbdlayer.rc`）は**上げていない**。区別が要る場合は、ローカルで上げてからビルドしてほしい。
- 署名とインストールは、いつもの手順で行う。インストールした `.sys` のハッシュが、ビルドの成果物と一致することを確認する（実行前チェック 4）。
- **ここではビルドしていない**（クラウドに WDK が無いため）。`HelperNotify.c` だけは、型の定義を真似たスタブで gcc の構文チェックを通した。構造体のサイズ（イベント 20、出力の見出し 16、入力 8 バイト）も確認した。コンパイルの警告・エラーが出たら、そのまま結果に書いてほしい。

## 試験項目

準備（T-13）:
- 注入スロットのキーボードで、4 キーを注入でキャリブレーションする。例: A(0x1E)、S(0x1F)、D(0x20)、G(0x22)。
  - `--calibrate-simple <ID> 4 helper-profile.json --suppress` の後、`--apply-simple <ID> test-keymap-helper.json helper-profile.json`、`--on <ID>`。
- キーマップ: キー1=`HLP(1)`、キー2=`HLP(0x102)`=258、キー3=`MO(1)`、キー4=`KC_X`。レイヤー1は、キー1=`HLP(3)`、キー4=`HLP(4)`。
- `KeymapTool --watch-helper` を、非昇格・Hidden・stdout リダイレクトで起動しておく（T-8、T-23）。
- 区切りは F13（T-9）。HLP キーは F13 を使わないので、そのまま使える。

| # | 内容 | 期待値 | 参考 |
|---|---|---|---|
| H1 | キー1をタップ | `--watch-helper` に `HLP(1) down` と `HLP(1) up`。testsink には何も来ない（区切りの F13 だけ） | T-9 |
| H2 | キー2をタップ | `HLP(258)` の down / up | |
| H3 | キー1を 30ms 間隔で 10 回リピートしてから離す | 最初の `down` に続いて `down (repeat)` が 9 行、`up` が 1 行。testsink は何も来ない | T-15 |
| H4 | キー3（MO(1)）↓ キー1↓ キー1↑ キー3↑ | `HLP(3)` の down / up | |
| H5 | 離す順序を入れ替える: キー3↓ キー4↓ キー3↑ キー4↑ | `HLP(4) down` と `HLP(4) up`（解放は押下時の記録どおり）。testsink に `x` が出ないこと | T-14 |
| H6 | 左 Shift↓ キー1をタップ 左 Shift↑ | `Mods=0x02`。testsink には Shift の down / up だけ | T-11 |
| H7 | キー1↓ →（押したまま）`--off <ID>` → キー1↑ | 離したときに `HLP(1) up` が出ること。testsink には何も来ない | T-14（`Invoke-OibWithCommand`） |
| H8 | `--watch-helper` を 2 つ同時に動かして H1 | 両方に同じ行が出る | |
| H9 | H8 の片方を kill してから H1 | 残った方には出続ける。kill からプロセスが消えるまでの時間を記録する | T-16 |
| H10 | `--watch-helper` を起動する**前**にキー1をタップし、起動後にキー2をタップ | キー1の行は出ず、キー2の行だけが出る | |
| H11 | `--watch-helper` を `NtSuspendProcess` で止め、キー1を 40 回タップ（80 イベント）、再開する | 保留中の IRP は最初の 1 件で完了するので、再開後は「1 行 → `(15 event(s) were dropped ...)` → 64 行」になるはず（合計 80 = 1 + 15 + 64）。BSOD なし | T-17 |
| H12 | `--watch-helper` を 17 個起動する | 16 個は待ち続け、17 個目は `IOCTL_KBDLAYER_WAIT_HELPER_EVENT failed. Error: 1450` で終了する（STATUS_INSUFFICIENT_RESOURCES）。16 個を止めた後、新しく起動したものは動く | F-3 |
| H13 | アクセス: 非昇格で `--watch-helper` が動く。0x81E をアクセス値 0 で送ると 1（INVALID_FUNCTION）になる | | F-1 |
| H14 | コンボ: `--set-combo <ID> 0 KC_A KC_S KC_ESC` のように、HLP キー（キー1）をメンバーにする。キー1を単独でタップ | コンボが成立しなかった復元で何も出ない（testsink に `a` が出ない）。`HLP(1)` のイベントも出ない（仕様）。終わったらコンボをクリアする | H-5（使い回し） |
| H15 | `--set-combo` の出力や `--set-tapdance` の引数に `HLP(1)` を書く | KeymapTool がエラーで拒否する | |
| H16 | Driver Verifier（/standard）を有効にして、H1〜H12 を通す。`--watch-helper` を待たせたまま、注入先のキーボードを `pnputil /restart-device`（1 台ずつ。H-5 の注意）を 10 回 | BSOD なし。`--watch-helper` は待ち続け（デバイスの取り外しでは終わらない）、再接続後のキー1も表示される | B-5、H-3 |
| H17 | 回帰: 既存の OIB の試験のうち、レイヤー（MO/TG/OSL）、コンボ、タップダンス、MT/LT の短いもの | 既存どおり | |

## 静的にしか確認していない点（実機で見てほしいところ）

- コンパイルできるか（とくに `HelperNotify.c` と、ControlDevice.c の新しいハンドラ）。
- `--watch-helper` を `Ctrl+C` で止めたとき、保留 IRP がキャンセルされてプロセスがすぐ終わるか（IoCsq のキャンセル経路）。
- オートリピートの make が `(repeat)` になるか（押下記録のあるキーの make をリピートとして扱う既存の判定に乗っている）。
- 押下と解放の `Mods` は、それぞれのときの `RealModsDown`（普通の出力として押されている修飾キー）。
- `TimeMs` は起動からの時間（`KeQueryInterruptTime`）。

## 試験担当への注意

- HLP キーは何も出さないので、testsink では「何も来ない」ことが期待値になる。恒等マッピングの誤 PASS（H-5）は起きにくいが、念のため H5 のように「HLP でなければ `x` が出る」ケースを入れてある。
- `--watch-helper` は全キーボードのイベントを表示する。注入先以外のキーボードを触らない（T-20）。
- `KeymapTool --set-layer` のような、Enter を待つ設計ではない。止めるときは kill でよい（ハンドルが閉じて、保留 IRP は CANCELLED で完了する）。

## チェックリストに照らした自己レビュー

dev-notes の `kbd-filter-driver/review-checklist.md` と `key-remap-engine/README.md` の項目ごとに確認した結果。

| 項目 | 結果 |
|---|---|
| A-1 PAGE＋スピンロック | `HelperNotify.c` に PAGE 指定の関数は無い |
| A-2 DISPATCH で触るデータ | リングは static（非ページ）。プールの確保は無い |
| A-3 ClassService の IRQL | 新しい送出は無い（キーを捨てるだけ） |
| A-4 待たない・DPC を連鎖させない | 待機なし。ループの上限はリング 64 件、キュー 16 件 |
| B-1 ロック外の生存保証 | IOCTL はデバイスを引かない。イベントは UniqId の値だけを持つ |
| B-5 保留 IRP | IoCsq で持ち、キャンセル・CLEANUP で完了する。特定のデバイスに結び付かないので、remove lock は取らず、デバイス削除でも完了させない（`RemovePending` は関係しない）。制御デバイスはハンドルが残っているとアンロードされない |
| D-3 捨てたときの件数 | 既存の `CompactInputDataArray` の経路で捨てるので、元の件数の報告はそのまま |
| D-5 送出順序 | 新しい送出は無い。イベントの Seq は、積んだ順（`EngineLock` の内側）に振る |
| D-6 押下中の設定変更 | HLP キーは OS に何も押していないので、取り残しは起きない。OFF の後の解放も、記録どおりにイベントになる |
| D-14 バッチの分割 | エントリごとの処理なので、分割は要らない |
| F-1 アクセス | READ。返すのは「ユーザー自身が HLP を置いたキーの押下」と修飾キーの状態だけで、任意の打鍵は読めない |
| F-3 入力の検証 | 入力長、`Flags`=0、出力が見出し＋1件以上。METHOD_BUFFERED の入力は先に読む |
| K-1 / K-2（key-remap-engine） | 解放とリピートは押下時の記録（HeldKeys）に従う |
| K-3 | HLP キーは break を出さない（make も出していない） |

dev-notes のチェックリストの「ドライバ別の状況」表は、P0 の実機の結果が返ってから更新する（新しい種類の問題は見つかっていない）。
