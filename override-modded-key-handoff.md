# 申し送り: 実機の試験（修飾付きキーへのキー置換、ACPI PS/2 対応のライセンス検証の切り離し）

この申し送りは 2 つの変更の試験をまとめている。**どちらもドライバ（kbdlayer.sys）を変更している**ので、試験は、両方を含むビルドで一度に行う。
- 第 1 部（M-1〜M-14）: 修飾付きキーへのキー置換。
- 第 2 部（L-1〜L-14）: ライセンス検証を `AddDevice` から切り離した変更（ACPI PS/2 の内蔵キーボード対応）。末尾の「第 2 部」の節を見る。

## 第 1 部: 修飾付きキーへのキー置換

- 対象: `main` の `8bd2dfe`（機能の本体は `9b8ca28`。`feature/override-modded-key` をマージ済み）。**ドライバ（kbdlayer.sys）を変更した**。kasane（キー図の R 印）も少し変えた。KeymapTool は変更なし。
- 第 2 部の対象は、`main` の `14a8864`（`feature/license-decouple` をマージ済み）。このコミットは、第 1 部の `8bd2dfe` を含む。試験は、`14a8864` 以降から作ったドライバで、両方をまとめて行ってよい。
- 書いた日: 2026-10-22（開発担当）。元: `docs/KNOWN-ISSUES.md` の 6。仕様: `docs/ARCHITECTURE.md` の「Key Override」。
- 試験には、このコミットから作ったドライバを入れた環境が要る。Driver Verifier を `kbdlayer.sys` に有効にする（`/standard`）。注入には OIB（dev-notes の `input-injection-testing/README.md` の T-番号に従う）。
- ユーザーが、基本の動作（Caps Lock = `LALT(KC_ZKHK)`、Shift+Caps Lock で Caps Lock）を、日本語配列のキーボードで確認済み（2026-10-22）。この申し送りは、残りのリスクを確かめるもの。

## 何を直したか
キー置換は、割り当てが通常キー（`KEY`）のキーにしか効かなかった。割り当てが修飾付きキー（`MODDED_KEY`。`LALT(KC_ZKHK)`、`LCTL(KC_C)` など）のキーにも、効くようにした（`LayerEngine.c` の `ResolveModdedKey`）。
- 照合は、修飾付きキーの**出力キー**（Param2）で行う。条件の修飾キーの判定は、物理で押されているものと、他の記録が足しているものだけ（この修飾付きキー自身が足す修飾キーは含めない）。
- 当たったときは、修飾付きキー自身の修飾キーを足さず、置き換え先だけを押す。押下の記録は、既存の `INTERNAL_KEY_OVERRIDE`。解放・リピート・離していた修飾キーの戻しは、既存の経路。
- `LAYER_MOD`（LM）、`ONESHOT_MOD`（OSM）は、対象外（変わらない）。
- 置換の機能が OFF、または表が空なら、何も変わらない。

## 設定の例（試験で使う）
- キーボード: 注入先は OIB の slot 0。物理スキャンコードは、Caps Lock = `0x3A`、半角/全角 = `0x29`、左 Shift = `0x2A`、`A` = `0x1E`、`2` = `0x03`、BS = `0x0E`。
- 設定 S1（本題）: Caps Lock（`0x3A`）に `LALT(KC_ZKHK)`、キー置換 0 = `KeymapTool --set-key-override <ID> 0 MOD_LSFT KC_ZKHK "LSFT(KC_CAPSLOCK)"`（JIS 配列の Windows。注入では、出力のスキャンコードで見るので、`KC_CAPSLOCK` でも期待値は同じ形）。
- 設定 S2: S1 に加えて、置き換え先がマクロ: キー置換 0 = `MOD_LSFT KC_ZKHK "MACRO(0)"`、マクロ 0 = `A` `B` を打つ。
- 設定 S3: Caps Lock に `LCTL(KC_C)`、キー置換 0 = `MOD_LSFT KC_C KC_D`。
- 設定 S4: 通常キーのキー置換の回帰用（既存の B 系と同じ）: キー置換 0 = `MOD_LSFT KC_2 KC_LBRC`、キー置換 1 = `MOD_LSFT KC_BSPACE KC_DELETE`。

## 試験項目

| # | 設定 | 操作 | 期待値 |
|---|---|---|---|
| M-1 | S1 | Caps だけを押して離す | 出力: 左 Alt の make → `0x29` の make → `0x29` の break → 左 Alt の break（Alt+半角/全角）。Shift は出ない |
| M-2 | S1 | 左 Shift ↓ → Caps ↓ → Caps ↑ → Shift ↑ | Shift が一時的に離れ（break）、置き換え先の `0x3A`（+ 置き換え先が `LSFT(...)` なら Shift の make）が出る。Alt は出ない。Caps ↑ のあと、Shift が戻る（まだ押されている場合）。最後に Shift が押しっぱなしにならない |
| M-3 | S1 | Shift ↓ → Caps ↓ → **Shift ↑ を先に** → Caps ↑ | どの修飾キー・Caps も、押しっぱなしにならない。Shift の break が二重に出ない |
| M-4 | S1 | Shift ↓ → Caps ↓ → （押したまま）`A` ↓ ↑ → Caps ↑ → Shift ↑ | `A` の直前に、離していた Shift が戻る（置き換えは続く）。最後に何も押しっぱなしにならない（既存の B-4 と同じ形） |
| M-5 | S2 | Shift ↓ → Caps ↓ ↑ → Shift ↑ | マクロ（`a`、`b`）が 1 回出る。マクロの中のキーに Alt、Shift、Ctrl が掛からない（D-18）。Caps ↑ は飲み込まれる |
| M-6 | S1。ただし機能のキー置換を OFF（`--set-features` で外す） | Shift ↓ → Caps ↓ ↑ → Shift ↑ | 置換なし。出力: Shift ↓、左 Alt ↓、`0x29`、左 Alt ↑、Shift ↑（Shift+Alt+半角/全角）。従来どおり |
| M-7 | S1 | Shift ↓ → Caps ↓（押したまま）。この間に `--off`（または `--clear`）を実行 → Caps ↑ → Shift ↑ | 取り残しがない。そのあとのキー入力が普通に出る（D-6） |
| M-8 | S3 | Shift ↓ → Caps ↓ ↑ → Shift ↑ | `D`（`0x20`）だけが出る。Ctrl は出ない。Shift なしの Caps ↓ ↑ は Ctrl+C（Ctrl の make → `0x2E` → Ctrl の break） |
| M-9 | LM・OSM を含むマップ（Caps = `LM(1,MOD_LSFT)`、別のキー = `OSM(MOD_LCTL)`）と、キー置換 1 件 | LM・OSM を、今までどおり操作 | 変わらない（対象外の型） |
| M-10 | S4（通常キーのキー置換の回帰） | 既存の A-1〜A-6、B-1〜B-5（`key-override-macro-results.md`、`key-override.md`） | すべて、従来と同じ結果 |
| M-11 | S1 | Shift ↓ → Caps を押し続ける（オートリピートを 20 回以上。注入では、make を連続して送る） → Caps ↑ → Shift ↑ | リピートは、置き換え先のキーの make だけ。Alt が足されない。最後に何も押しっぱなしにならない |
| M-12 | S1 | **回帰（偶然の一致）**: 本物の半角/全角キー（`0x29` の別のキー、または Caps を使わず `0x29` を注入）を、Shift と押す | 仕様どおり、置き換え先に置き換わる（出力キーで照合するため）。仕様として確認する |
| M-13 | S1 + 既存のマクロ・コンボ・タップダンス・MT/LT のマップ | 通常のタイピング、キーリピート、緊急解除（LCtrl・RCtrl・LShift・RShift を 3 秒）、`--watch-helper` と HLP | 従来どおり。打鍵の遅延が体感で変わらない |
| M-14 | S1 | **耐久**（H-3）: Verifier 下で、Shift+Caps を 100 回以上、Caps 単独を 100 回以上、交互に。途中で `pnputil /restart-device`、画面ロック・解除、（USB なら）抜き差し | クラッシュ・ハング・Verifier の検出なし。押しっぱなしのキーなし |

## 試験担当への注意
- 条件の修飾キーを離してから置き換え先を出す処理に、Alt・Win が絡むときは、メニューやスタートが開かないことも見る（Alt の単独押し。ドライバは Ctrl のタップを挟む）。M-1 で、メモ帳の前面に出して、メニューバーが反応しないことも確かめる。
- 注入では、Shift の make と break のタイミング（間隔）を、既存の B-4 と同じ形にする。出力の順序（Shift ↓ → Shift ↑ → 置き換え先 ↓ ↑ → Shift ↓）を、キャプチャで比べる。
- 試験中は、nodoka、PowerToys が動いていないか、各ステップの前に確認する（dev-notes の T-2）。
- 試験後、設定（`--clear`、`--set-features` を元に）を戻す。ドライバは、試験対象のビルドのまま（ハッシュを控えておく）。
- ARM64 は、開発側の PC でビルドできていない（Spectre 対策ライブラリが無い）。環境があれば、ビルドと M-1 だけでも見てほしい。無ければ「未実施」でよい。

## 参考: 所要時間の見積もりのために
- 項目数: M-1〜M-14（M-10 は既存の A・B 系 11 項目を含む。M-14 は耐久で、実時間が掛かる）。第 2 部は L-1〜L-14。
- 初回は、設定の準備（S1〜S4 の登録と、注入スクリプトの作成）に時間が掛かる。既存のスクリプト（`key-override` 系の試験で使ったもの）を流用できる。
- 見積もりには、次を分けて出してほしい: (1) 準備、(2) M-1〜M-9・M-11〜M-13、(3) M-10（回帰）、(4) M-14（耐久）、(5) 第 2 部の L-1〜L-13（L-3・L-9 は 5 分の待ちを含む）、(6) L-14（耐久）。

## 結果
- 第 1 部: `override-modded-key-results.md` に、PASS／FAIL／未実施で書く（FAIL は、再現手順と、注入スクリプト、`testsink` のログ、Verifier の出力を添える）。
- 第 2 部: `license-decouple-results.md` に、同じ形式で書く（FAIL には、DebugView のカーネルログ（`kbdlayer: license ...` の行）、`Parameters` の `LicenseToken`・`LicenseLastState` の値、Verifier の出力を添える）。

---

## 第 2 部: ライセンス検証の切り離し（ACPI PS/2 の内蔵キーボード対応）

- 対象: `main` の `14a8864`（`feature/license-decouple`。コミット `7643b80` と `815c231`）。設計は `docs/ARCHITECTURE.md` の「ライセンスの検証と、使用時の判定」。ID の扱いは `docs/DEVICE-ID.md` の第 6 節。
- 書いた日: 2026-10-28（開発担当）。

### 何を直したか
内蔵の ACPI PS/2 キーボードは、`AddDevice` が起動時の 1 回しか走らず、`pnputil /restart-device` でもやり直せない。以前は `AddDevice` の中でライセンス（`ValidateLicenseToken`）を検証していたので、
起動直後に検証が失敗する環境では、そのキーボードがその起動のあいだずっと認識されなかった（`DeviceId=0`）。
- 検証を `AddDevice` から切り離した。`AddDevice` は、ID の計算、永続設定の復元、i8042prt の書き換えを、ライセンスの状態に関わらず行う。
- ライセンスは、使うときに判定する（`LicenseIsEffective()`）。状態は `VALID`、`PROVISIONAL`（暫定有効）、`INVALID`。
- 起動時、`Parameters\LicenseLastState` が 1 なら `PROVISIONAL`（5 分間だけ有効）、それ以外は `INVALID`。ドライバが 5 秒ごとに再検証し、成功すれば `VALID`、5 分たっても成功しなければ止める。
- サービス（`kasane-license-svc`）が、トークンを書いた・消したあとは `DEFINITIVE`（失敗なら、すぐ `INVALID`）、起動時は `SOFT`（成功なら `VALID`、失敗なら状態を変えない）で、ドライバに再検証させる（IOCTL 0x820）。
- 有効から無効に変わったときは、生きた状態（押下中の修飾キー、保留、レイヤー、NICOLA）を中止する。永続設定と `LayerMapEnabled` は変えない。
- インストーラーは、`LicenseLastState` が無いときだけ 1 を書く。Pro のインストールは、この値を消す。

### 準備
- ビルド: Subscription 版のドライバ（x64 の WHQL 署名済み、または試験署名）。Driver Verifier を `kbdlayer.sys` に有効にする（`/standard`）。
- 状態の観測: DebugView を管理者で起動し、`Capture Kernel` と `Enable Verbose Kernel Output` を有効にする（DbgPrintEx の INFO レベルを見るため、`Debug Print Filter` の `IHVDRIVER` マスクが 0xF 以上であること。レジストリ `HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Debug Print Filter` の `IHVDRIVER`=`0xF`、要再起動）。
  見る行: `kbdlayer: license initial state=<n> (LicenseLastState=<0|1>)`、`kbdlayer: LicenseLastState write failed ...`（出たら異常）。
  補助: `KeymapTool --list`（有効でないあいだは、そのキーボードが出ない）、`testsink` の `ExtraInformation` の上位 16bit（無効なあいだは 0）。
- レジストリ: `HKLM\SYSTEM\CurrentControlSet\Services\kbdlayer\Parameters` の `LicenseToken`、`LicenseLastState`。試験の前に、両方の元の値を控える（`reg export`）。
- 試験中にトークンを書き換えるときは、サービス `kasaneLicenseSvc` を止めておく（起動時に Gumroad の確認でトークンを書き直し、`DEFINITIVE` を呼ぶため）。試験が終わったら、元の値とサービスを戻す。
- 対象のキーボード: **内蔵の ACPI PS/2**（本題）と、USB の HID キーボード 1 台。PS/2 は注入ではなく、実際のキーを押して確かめる（OIB は PS/2 の `AddDevice` を通らない）。
- 第 1 部の S1 の設定（Caps = `LALT(KC_ZKHK)`）を、内蔵キーボードに入れておくと、リマップが効いているかを、見て分かる（Caps で半角/全角が切り替わる、または `testsink` で見る）。

### 試験項目

| # | 前提 | 操作 | 期待値 |
|---|---|---|---|
| L-1 | `LicenseToken` が有効、`LicenseLastState`=1。サービスは通常どおり | PC を再起動。ログオン直後から、内蔵キーボードで Caps を押す | 起動直後から、リマップが効く（`PROVISIONAL` で使える）。`--list` に内蔵キーボードが出る。ドライバのログは `initial state=` の次に、数秒〜数分以内に `VALID` になる。`LicenseLastState` は 1 のまま |
| L-2 | `LicenseToken` が有効。**`LicenseLastState` を削除**（初回インストール直後の状態） | PC を再起動。ログオン直後から、内蔵キーボードで Caps を押す | 起動直後（検証が成功するまで）は素通しでもよい。検証が成功した時点（5 秒ごとの再試行、またはサービスの `SOFT`）で、**再起動も再接続もなしに**、リマップが効き始める。`--list` に出る。`LicenseLastState` が 1 になる。起動直後の検証が最初から成功する環境では、起動直後から効く（それも PASS） |
| L-3 | `LicenseToken` を、1 文字だけ書き換えて壊す。`LicenseLastState`=1。サービスは止める | 再起動。内蔵キーボードで Caps を押す。5 分以上待つ | 起動から 5 分間は、リマップが効く（`PROVISIONAL`）。5 分たったら、効かなくなる（素通し。`--list` から消える）。途中で押下中のキーを取り残さない。5 秒ごとの再試行が、5 分で止まる（ログが増え続けない） |
| L-4 | L-3 のあと（`INVALID`）。`LicenseToken` を元に戻し、サービスを起動 | サービスの起動を待つ | サービスの `SOFT` で `VALID` になり、再接続なしに、リマップが効く。`--list` に戻る。`LicenseLastState` が 1 になる |
| L-5 | `VALID`、リマップ動作中 | ライセンス管理で、ライセンスを**解除**する（サービスが `ClearLicense` を呼び、`DEFINITIVE` で再検証される） | すぐに素通しになる（再起動不要）。`--list` から消える。ログに `INVALID` への遷移。`LicenseLastState` が 0 になる |
| L-6 | `VALID`。S1 の設定。**押下中に無効にする**: Shift ↓ → Caps ↓（押したまま）→ L-5 の解除（または L-3 の期限切れ）→ Caps ↑ → Shift ↑ | 左のとおり | どの修飾キー・Caps も押しっぱなしにならない。そのあとのキー入力が普通に出る。永続設定は残っている（`--get-layermap` などで、解除前と同じ。`LayerMapEnabled` は変わらない）。MT・LT・TapDance・コンボ・NICOLA の保留中に無効にしても、同様 |
| L-7 | L-6 のあと、ライセンスを再度有効にする（`WriteLicenseToken` → `DEFINITIVE`） | **有効に戻した直後の最初のキー**を押す | 最初の押下が捨てられない（`SuppressedDown` を空にしてあるため）。リマップが効く。以前の設定（レイヤーなど）がそのまま使える |
| L-8 | `INVALID` のあいだ（L-5 の解除後、または L-3 の期限後） | `testsink` で、キー入力の `ExtraInformation` を見る。`KeymapTool --list`、`--set-*` を実行 | 上位 16bit が 0（`DeviceId` が書かれない）。`--list` にキーボードが出ない。`FindDeviceExtensionByUniqId` が NULL を返すので、`--set-*` は「キーボードが見つからない」で失敗する。クラッシュしない。有効に戻れば、すべて元に戻る |
| L-9 | `LicenseLastState`=1、`INVALID` 側の準備（L-3 と同じ） | **起動から 5 分以内**（再試行が動いているあいだ）に、ドライバをアンロードする（`sc stop kbdlayer` が通る状態にして）。または `pnputil /restart-device` で、USB キーボードを抜き差しする | Verifier の検出なし。ハング・クラッシュなし。`TeardownLicense` の順序（作業項目の完了待ち→タイマー取消→DPC→解放）の確認 |
| L-10 | i8042prt の書き換えが有効（設定ダイアログの i8042prt タブで、内蔵キーボードの型番にプロファイルを指定。例: JIS = タイプ 7、サブタイプ 2、`PCAT_106KEY`、`kbd106.dll`）。`Parameters\i8042prt` の該当の値を、いったん削除しておく。`LicenseToken` を壊し、`LicenseLastState`=0（`INVALID` のまま起動する） | 再起動。起動直後に `HKLM\SYSTEM\CurrentControlSet\Services\i8042prt\Parameters` を見る | **ライセンスが `INVALID` でも**、`OverrideKeyboardType`・`OverrideKeyboardSubtype`・`OverrideKeyboardIdentifier`・`LayerDriver JPN` が書かれている（`AddDevice` の時にしか効かない処理を、ライセンスに関わらず行うため） |
| L-11 | USB の HID キーボード。`INVALID`（L-5 の解除後） | USB を抜き差しする。そのあと、ライセンスを有効に戻す | 接続したキーボードに、`INVALID` のあいだは何も起きない（素通し）。有効に戻したあと、**再接続せずに**リマップが効く。`DeviceId` が、接続の直後に計算されている（`--list` が、有効に戻した直後から出す） |
| L-12 | インストーラー | (1) 新規インストール（`LicenseLastState` が無い）、(2) `LicenseLastState`=0 のまま上書きインストール（更新・修復）、(3) アンインストール、(4) Subscription から Pro へ | (1) `LicenseLastState`=1 が書かれる。(2) 0 のまま変わらない（値があるときは書かない）。(3) 開発側では未確認。`LicenseLastState` が残るかどうかと、そのあとの再インストールでの値（L-2 と同じ挙動になるか）を記録する。(4) Pro のインストールで、`LicenseLastState`・`LicenseToken`・`License` が消える。`CA_SeedLicenseLastState` は Pro には無い |
| L-13 | サービス | (1) ドライバをロードしない状態（`sc stop kbdlayer`、またはドライバ未導入）で、サービスを起動する。(2) 起動後、ライセンスの付与・解除を行う | (1) サービスが落ちない（`\\.\KbdLayerControl` が開けない失敗は、無視してログに残すだけ。`DebugTrace` のログに `DriverRevalidator: cannot open control device` が出る）。(2) 付与・解除が、ドライバが無くても成功する（ライセンスの書き込み自体は通る） |
| L-14 | **耐久**（H-3）。Verifier 下。`VALID` の状態 | 再起動 20 回以上（コールドブート）、スリープ・復帰、休止状態・復帰（`PROVISIONAL` の 5 分の途中、`VALID` のとき、それぞれ）、ライセンスの付与・解除を交互に 50 回以上、画面ロック・解除 | クラッシュ・ハング・Verifier の検出なし。毎回、再起動後に、リマップが効く（`LicenseLastState` が 1 のあいだ）。解除したあとの再起動では、素通し（`LicenseLastState` が 0）。押しっぱなしのキーなし |

### 回帰（変えていない部分が、変わっていないこと）
- 第 1 部の M-1、M-7（`--off` の取り残しがない。`SetLayerMapEnabledForDevice(FALSE)` を中止処理の共通関数に切り出したため）、M-13（通常のタイピング、緊急解除、`--watch-helper`）を、**`VALID` の状態で**、このビルドでもう一度行う。結果は、従来と同じであること。
- `--list`、`--get-*`、`--set-*` が、`VALID` のとき従来どおり動くこと（一覧の「数える」段階と「書き込む」段階で、同じ判定を使うように変えたため）。
- NICOLA が `VALID` のときだけ有効になること（`NicolaIsActiveLocked` に判定を足したため。IME 連動の NICOLA を使う設定で、1 回確かめる）。

### Pro 版（任意）
開発側では Pro 版のドライバをビルドしていない。ソースの上では、Pro には影響しない（`ARCHITECTURE.md` の比較表）。ビルドできる環境があれば、確かめる。
- `msbuild driver\kbdlayer\kbdlayer.vcxproj /p:Configuration=Release /p:Platform=x64 /p:KbdlayerEdition=Pro` が、警告なしで通ること。
- 出力した `kbdlayer.sys` に、`LicenseToken` の文字列が含まれないこと（`docs/RELEASE-CHECKLIST.md` と同じ確認）。
- 入れて、再起動した直後から、リマップが効くこと（`PROVISIONAL` の待ちがなく、常に有効）。第 1 部の M-1 を 1 回。
- 無ければ「未実施」でよい。

### 注意
- L-3・L-4・L-9・L-10 は、`LicenseToken` と `LicenseLastState` を手で書き換える。**試験の前に控えて、終わったら必ず戻す**。戻さないと、ライセンスが無効のまま、または、有効のように見える状態が残る。
- L-5・L-7 は、Gumroad のライセンスの解除・再付与を伴う。使用回数の上限があるので、**付与の回数を、必要な分だけにする**。繰り返しの試験（L-14 の「付与・解除を交互に 50 回」）は、Gumroad への問い合わせを繰り返さずに済む方法（すでに保存してあるトークンを、書き直す・消す）が要る。その方法が使えなければ、その部分は「未実施」にして、理由を書く。
- Boot 直後に検証が失敗する環境（問題が出た機種）が手元にあれば、**そのPCで L-1・L-2 を最優先で行う**。原因（BCrypt のどの呼び出しが失敗するか）は未特定。DebugView のログと、`LicenseLastState` の値を、必ず添える。
- 内蔵キーボードの試験は、キーボードを押す操作を人が行う。`testsink` の読み取りは、USB・OIB 側の確認に使い、PS/2 は目視でよい。
- ARM64 は、環境があれば、ビルドと L-1 だけでも見てほしい。無ければ「未実施」でよい。
