# P9.1 試験結果: uiAccess 版の自己展開をやめる（U8）、文書を実測に合わせる

- 対象: `a7f4bd3`（`feature/tray-uiaccess`。`tray\` の 2 つの exe の ProductVersion が `1.0.0+a7f4bd3…`、署名 Valid）
- 試験日: 2026-10-11（試験担当）
- 結論: **V1〜V8 すべて PASS。V9 は、同じ版の上書き（修復）とアンインストールを PASS。旧版からの上書きは、旧版の MSI が無いため未実施。** P9 の U8（自己展開）は、直っている。

| # | 結果 | 内容（実測） |
|---|---|---|
| V1 | PASS | `tray\` は `kasane-tray.exe`（**170,739,184 B ＝約 170MB**）と `kasane-tray-nua.exe`（178,956,016 B）の 2 つだけ。**両方とも署名 Valid** |
| V2 | PASS | `%TEMP%\.net\kasane-tray*` を削除してから、**エクスプローラー経由**で `tray\kasane-tray.exe` を起動: `kasane-tray` のフォルダは**作られない**（0 個）。トレイは動き、アイコンとツールチップが出る（`TokenUIAccess=1`）。同じ exe を、空の `DOTNET_BUNDLE_EXTRACT_BASE_DIR` を指して起動（`Start-Process`）しても、**展開は 0 ファイル**。ログオン後（再起動後）の起動でも、`%TEMP%\.net` の `kasane-tray` に新しいフォルダは無かった |
| V3 | PASS | 動いている uiAccess のトレイ（再起動後、Run キーからの起動。親はエクスプローラー）のモジュール 38 個: Windows のものと `kasane-tray.exe` 自身だけ。**`%TEMP%` のものも、`wpfgfx`・`PresentationNative`・`PenImc`・`D3DCompiler` も無い**（P9 では 39 個） |
| V4 | PASS | **再起動（14:46）のあと、Run キーから起動したトレイ**（14:49、親は `explorer`）: **`TokenUIAccess=1`**（昇格したランナーからトークンを読んだ）。アイコンが出ている。P9 の持ち越し（U3）が確認できた |
| V5 | PASS | 管理者として動く窓（TextBox つき）を前面にして、`HLP(1)` と F13 のトリガー: **`HLPx`・`TRGy` が届く**（6 回すべて）。普通の窓でも同じ（最初の 1 回だけ `LPx` と先頭の 1 文字が落ちたのは、試験側の前面切り替えの ALT キーが原因で、nua でも同じに出ることを P9 で確認済み） |
| V6 | PASS | uiAccess のトレイで: L1・L2（MO・TG の追従 100 ms 前後）、L4（NICOLA + IME 連動: かなで `レイヤー 1（NICOLA）`、IME オフ 3 回で解放 71〜169 ms）、L9（設定のチェックでオフ 約 1.0 s／オン 約 2.0 s、`tray.json` に保存）、L8（一時停止 → `一時停止中`・`Enabled=0`、再開 → 通常に戻る。メニューの操作は昇格したランナーからのクリック）すべて従来どおり |
| V7 | PASS | `kasane-tray-nua.exe` は起動し、**従来どおり自己展開する**（空の展開先に WPF のネイティブ DLL 5 個）。uia 版だけが展開しない（仕様どおり） |
| V8 | PASS | `HELPER.md` の「管理者として動くウィンドウが前面のとき」の項は実測と一致: uiAccess で届く（V5）、nua・開発ビルドは届かない（P9 の U5）、エクスプローラー以外から起動すると**エラーなしで uiAccess なし**（P9 の `TokenUIAccess=0`）、ログオンし直すと効く（V4）、`Launch` の子に引き継がれない（P9 の U12）。確認していない記述: Process Explorer の「UIAccess」の列（試験担当は、トークンを昇格したランナーから読んで代用）、スタートメニュー・ダブルクリックからの起動（エクスプローラーと同じ経路のはずだが、未確認）。1 点だけ補足: 「署名が信頼されない／Program Files の外だと起動に失敗して何も出ない」は、**エクスプローラー経由の場合の話**で、`Start-Process` 経由なら失敗せず uiAccess なしで動く（P9 の U9）。文書の (3) の記述と合わせて読めば矛盾はないが、この項にも一言あるとよい |
| V9 | PASS（旧版からの上書きは未実施） | ユーザーが行った操作のログ（`%TEMP%\kasane-V9a.log`、`kasane-V9b.log`）と出力から確認。**V9a（`msiexec /i` ）は、旧版からの更新ではなく、同じ製品コード（`{FC5879BF-…}`）の「保守モード」（修復）だった**（ログ: `Product registered: entering maintenance mode`、`FindRelatedProducts` は実行されず、最後は「製品が再構成されました」）。その中で: Restart Manager が使用中のアプリを閉じ（`Successfully shut down all applications that held files in use`）、`CA_StopTray`（終了コード 128 ＝止めるものが無い、で成功扱い）が走り、`kasane-tray.exe`（170,739,184 B）と `kasane-tray-nua.exe` を `Overwrite` で入れ直し（FileVersion が同じでも上書きされる）、Run キーを書き、再起動は求められなかった（`ScheduleReboot` は条件が偽）。再起動後の `tray\` は **2 つだけ**（`kasane-tray-nua.exe` 178,956,016 B、`kasane-tray.exe` 170,739,184 B。更新日時 14:23 は、MSI に入っているビルド時刻のまま）、Run キーは `tray\kasane-tray.exe`。**V9b（`msiexec /x`）**: `RegRemoveValue`（Run キー）、`FileRemove` で 2 つの exe、`製品が削除されました（状態 0）`。実行後、`tray\` フォルダは無く、Run キーも「見つかりません」。**なお、旧版（別の製品コード、または古い構成）からの更新は、旧版の MSI が無く未確認**（P9 の U10 の「`tray\` に古い uiAccess なしの `kasane-tray.exe` が残らない」は、この構成では確かめられない） |

## V9 の補足
- V9a のログの最後に `RESTART MANAGER: Previously shut down applications have been restarted` がある。**Restart Manager が閉じたトレイを再起動したなら、それは CreateProcess 系の起動で、`kasane-tray.exe` が uiAccess なしで動く可能性がある**（P9 の所見 1）。このときは直後に再起動されたため確認していない。次回の上書きでは、更新の直後（再起動前）にトレイのトークン（`TokenUIAccess`）を確認すると分かる。
- V9b のログに `RESTART MANAGER: Failed while restarting applications. Error: 352`（アンインストールで消えたアプリを再起動しようとして失敗。無害と思われる）。
- **この PC は今、kasane がアンインストールされた状態**（`C:\Program Filespplet\kasane` が無い）。以降の試験には、MSI を入れ直す必要がある。

## メモ
- 起動は、すべて `explorer.exe "<exe>"`（uiAccess を付けるため）。
- 試験後: レイヤーマップは全台 `--clear`、`helper.json`・`tray.json`・`tray.log` は削除、昇格ランナーを終了。`kasane-tray.exe`（エクスプローラー経由、`TokenUIAccess=1`、pid 10600）が動いている状態に戻した。リポジトリの作業ツリーは変更なし。
