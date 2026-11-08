# P9 修正 申し送り: uiAccess 版の自己展開をやめる（U8）、文書を実測に合わせる

- 作業ブランチ: `feature/tray-uiaccess`。対象コミット: `a7f4bd3`（P9 の `ed5c8d5` の上）。
- 書いた日: 2026-10-11（開発担当）。元: `phase-9-results.md` の U8（FAIL）、所見 1・3。ドライバは変更していない。
- 試験には、**このコミットから作った署名済みの MSI** が要る（P9 と同じ。署名のトークンが要る）。作れない環境では「未実施」でよい。

## 変更点
1. **U8 の修正（`kasane-tray-uia` だけ）**: `IncludeNativeLibrariesForSelfExtract=false` にして、起動のたびの `%TEMP%\.net\kasane-tray\` への展開をやめた。
   自己完結の公開物に入る WPF のネイティブ DLL 5 個（`D3DCompiler_47_cor3`・`PenImc_cor3`・`PresentationNative_cor3`・`vcruntime140_cor3`・`wpfgfx_cor3`。トレイは WinForms で、読み込まない）は、公開物から外した（`RemoveUnusedWpfNatives`）。
   → `kasane-tray.exe` 1 つになる。サイズは約 170MB（以前は約 179MB）。
   - 開発側の確認（Release の publish を実機で起動）: 展開が起きない（`DOTNET_BUNDLE_EXTRACT_BASE_DIR` を空のフォルダにして、起動後に 0 ファイル）、モジュール 39 個のうち `%TEMP%` のものは 0、WPF のネイティブ DLL は読み込まれない、トレイは動く。
   - **`kasane-tray-nua.exe`（uiAccess なし）は変えていない**（従来どおり自己展開する。uiAccess ではないので対象外）。
2. **文書を実測に合わせた**: `Launch` の子に uiAccess は引き継がれない（U12）。uiAccess が付くのはシェル（エクスプローラー）が起動したときだけ（所見 1）。確認方法を `HELPER.md` に追記。
3. `Run-All.ps1` の冒頭に、T10 の前提（Debug の nua ビルド、インストール済みの uia は先に終了）を追記。

## 再試験項目

| # | 内容 | 期待値 |
|---|---|---|
| V1 | 新しい MSI を入れる。`Program Files\applet\kasane\tray\` の中身 | `kasane-tray.exe` と `kasane-tray-nua.exe` の 2 つだけ。両方とも署名 Valid。**`kasane-tray.exe` のサイズが約 170MB**（約 179MB なら古いビルド） |
| V2 | **自己展開（U8 の取り直し）**: `%TEMP%\.net\` を空にして（または `DOTNET_BUNDLE_EXTRACT_BASE_DIR` で空のフォルダを指して）、エクスプローラー経由で `tray\kasane-tray.exe` を起動する | `kasane-tray` のフォルダが**作られない**（展開が 0 ファイル）。トレイのアイコンが出て、通常どおり動く |
| V3 | 動いている `kasane-tray.exe`（uiAccess）のモジュール一覧 | `%TEMP%` のものが 1 つも無い。WPF のネイティブ DLL（`wpfgfx` など）が無い。Windows のものと `kasane-tray.exe` だけ |
| V4 | **ログオフ・ログインのあと**（再起動でもよい）、Run キーから起動したトレイ（U3 の持ち越し） | `TokenUIAccess=1`（Process Explorer の UIAccess の列、または昇格したランナーからトークンを読む）。アイコンが出る |
| V5 | U4 の抜き打ち（管理者の窓の前面で `HLP(1)` と F13 のトリガー） | P9 と同じく届く（`HLPx`・`TRGy`）。自己展開をやめた影響が無いこと |
| V6 | U6 の抜き打ち（レイヤー表示、一時停止、IME 連動） | 従来どおり |
| V7 | nua 版（`kasane-tray-nua.exe`）の起動 | 従来どおり起動する（自己展開する。これは仕様）。V2・V3 の対象は uia 版だけ |
| V8 | `HELPER.md` の「管理者として動くウィンドウが前面のとき」の項を読んで、実際と合っているか | 起動の経路による違い（(3)）、Process Explorer での確認方法、`Launch` の子に引き継がれない、が実測と一致する。違えば報告 |
| V9 | U10・U11（上書き・アンインストール）: 旧版が入った環境で新版を上書き／アンインストール | 起動中のトレイ（どちらの名前でも）が自動で終了し、新しい版が入る。`tray\` に 2 つ。ログインで `kasane-tray.exe` が起動。アンインストールで 2 つとも消え、Run キーも消える。**旧版の MSI が要るので、ユーザーの環境で行い、`msiexec /l*v` のログと `tray\` の中身を共有してもらう**（試験担当が行えなければ未実施） |

## 試験担当への注意
- V2 の自己展開の確認は、**ネイティブ DLL が展開される前の状態**で見る（古い `%TEMP%\.net\kasane-tray\` が残っていると紛らわしい。先に削除する）。
- 起動はエクスプローラー経由（`explorer.exe "<exe>"`）で行う。`Start-Process` だと uiAccess が付かない（P9 の所見 1）。
- 試験後、`tray\kasane-tray.exe` が動いている状態（エクスプローラー経由、`TokenUIAccess=1`）に戻す。

## 結果
`phase-9-fix-results.md` に PASS／FAIL／未実施で書く。
