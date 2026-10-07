# P1 申し送り: 出力キーの拡張（F13–F24・ブラウザー／電源キー・`SC_XXXX`・GUI の `HLP(id)`）

- 作業ブランチ: `claude/tender-euler-xxfl7a`
- 対象コミット: `dcbe9ef`（P0 の `f57644d` と、ドキュメントの注記 `2628e5d` の上）
- 書いた日: 2026-10-08（開発担当）
- P0 の結果（`phase-0-results.md`、全 PASS）を受けて実施した。H14 の INFO（HLP のキーもコンボのメンバーになり、コンボが優先される）は、`samples/README.md` と `docs/ARCHITECTURE.md` に追記した（`2628e5d`）。

## 何が変わったか

**ドライバは変えていない**（P0 のまま）。ユーザーモードのツールだけの変更。

- **名前表**（KeymapTool `driver/KeymapTool/KeycodeTable.cpp` と kasane `kasane/Keycodes/KeycodeTable.cs`。既存の並びの末尾に追加）
  - F13〜F23 = `0x64`〜`0x6E`、F24 = `0x76`
  - ブラウザー・アプリ起動キー（すべて E0 付き）

    | キー | 値 | キー | 値 | キー | 値 |
    |---|---|---|---|---|---|
    | `KC_WSCH` | E065 | `KC_WHOM` | E032 | `KC_WBAK` | E06A |
    | `KC_WFWD` | E069 | `KC_WSTP` | E068 | `KC_WREF` | E067 |
    | `KC_WFAV` | E066 | `KC_MAIL` | E06C | `KC_MSEL` | E06D |
    | `KC_MYCM` | E06B | | | | |

  - 電源: `KC_PWR` E05E、`KC_SLEP` E05F、`KC_WAKE` E063
  - QMK の長い名前（`KC_WWW_SEARCH` など）も別名として使える。
- **`SC_XXXX`**: Enc16 の 16 進 4 桁で、任意のスキャンコードを出力に書ける。
  - 上位 2 桁は 00 / E0 / E1、下位 2 桁は 0 以外。大文字・小文字は問わない。
  - `LCTL(SC_E06A)` や `MT(MOD_LSFT,SC_0064)` のタップ側にも書ける。
  - ドライバから読み戻したキーに名前が無いときは、`KC_UNKNOWN_XXXX` ではなく `SC_XXXX` と表示する（読み込み直せる）。
- **GUI が `HLP(id)` を理解する**（キーコード欄に書ける。読み戻しも `HLP(id)`）。先頭が 0 の数（`HLP(010)`）は、KeymapTool と GUI の両方で 10 進として扱う。
- **キー割り当て画面**
  - 「ファンクション」に F1〜F24 を並べた。
  - 「メディア」を「メディア・システム」に改名し、新しいキーを足した。**カテゴリの数は 10 のまま**なので、UIA T7 のインデックスは変わらない（コメントだけ直した）。
- **テスト**（`kasane.Tests/KeycodeTableTests.cs`）: 次のケースを足した。
  - 新しいキーの値
  - `SC_` の解決と往復
  - `HLP` の解決と往復
  - 不正な形の拒否
  - カタログの全キーコードが解決できること
- **ドキュメント**: `docs/ARCHITECTURE.md` に「出力に選べるキー」の節を足した。

## ビルドとインストール

- `kasane.sln` をビルドする（kasane、kasane-tray、KeymapTool、kasane.Tests）。ドライバの再ビルドと再インストールは不要（P0 のドライバのまま）。
- `tools/Check-KeycodeTables.ps1` を実行して OK になること。
  - こちらでは同じ規則を Python で確かめた。結果は、両方 223 件で、名前・値・並び順とも一致。
- `dotnet test kasane.Tests` を実行する。**C# はこちらでビルドしていない**（.NET SDK を入れられない環境のため）。コンパイルエラーや失敗が出たら、そのまま結果に書いてほしい。
- C++ の解決（`KeycodeTable::Resolve`）は、こちらで Linux 上でビルドして、`KC_F13` / `SC_E06A` / `LCTL(SC_E06A)` / `HLP(0x102)` / 不正な形の拒否を確かめた。

## 試験項目

注入で確かめられるのは「入力側」のキーなので、**出力側**（F13 など）は testsink の vk と sc で見る（T-10）。**F13 を区切りに使っている試験（T-9）では、F13 を出力に使うケースだけ区切りを F14 などに変える**か、F13 の出力を区切りと区別できる並びにしてほしい。

| # | 内容 | 期待値 | 参考 |
|---|---|---|---|
| K1 | `tools/Check-KeycodeTables.ps1` | OK（終了コード 0） | |
| K2 | `dotnet test kasane.Tests` | すべて合格（新しいテストを含む） | |
| K3 | KeymapTool で、4 キーに `KC_F13`、`KC_F24`、`SC_E06A`、`LCTL(SC_0064)` を割り当てたキーマップを適用し、注入で各キーをタップする | testsink: F13 = vk 0x7C / sc 0x64、F24 = vk 0x87 / sc 0x76、`SC_E06A` = ブラウザーの「戻る」（vk 0xA6、sc 0x6A、ext=1）、`LCTL(SC_0064)` = Ctrl の down → F13 → Ctrl の up | T-10 |
| K4 | `--get <ID>` で読み戻す | Type=2、Param2 がそれぞれ 0x0064 / 0x0076 / 0xE06A。修飾付きは Type=13、Param1=1 | |
| K5 | GUI: キー割り当て画面の「ファンクション」に F1〜F24、「メディア・システム」に新しいキーが並ぶ。F13 を選んで「キーボードに適用」 → 注入でタップ | testsink に F13（vk 0x7C） | UIA |
| K6 | GUI: キーコード欄に `SC_E06A` と `HLP(5)` を手で入力して適用。保存して開き直す | どちらも受け付けられ、開き直しても同じ表示。`HLP(5)` のキーは `--watch-helper` に `HLP(5)` が出る（P0 と同じ） | |
| K7 | GUI の読み戻し: プロジェクトファイルを消してから GUI を開く（ドライバから読み込む）。対象は、KeymapTool で `SC_E07F` のような名前の無いキーを入れたマップ | そのキーが `SC_E07F` と表示され、そのまま「適用」してもエラーにならない（以前は `KC_UNKNOWN_E07F` で適用できなかった） | |
| K8 | UIA の T7（カテゴリのインデックス）と T1〜T10 | `FAIL COUNT: 0` | |
| K9 | 注意して試す: `KC_SLEP` / `KC_WAKE` / `KC_PWR` | **注入しない**でよい。電源キーは PC の状態を変える（T-20）。名前表の値だけを K4 と同様に読み戻しで確かめてほしい | T-20 |

## 静的にしか確認していない点

- C# のコンパイル（`TryParseRawScanCode` の `ushort.TryParse(ReadOnlySpan<char>, …)` など）と、単体テストの合否。
- GUI の表示（キートップの短い名前: F13 は「F13」、`KC_WSCH` は「Search」など）。
- ブラウザーのキー（E06A など）が、注入でも物理キーと同じ vk になるか。Windows の対応表どおりなら、vk 0xA6〜0xAC、0xB4〜0xB7 になるはず。

## 試験担当への注意

- ブラウザーのキーを注入すると、前面のアプリ（ブラウザーやエクスプローラー）が「戻る」などの動作をする。testsink が前面にあることを確かめてから注入する（T-7）。
- `KC_MAIL` / `KC_MYCM` / `KC_MSEL` / `KC_WSCH` は、Windows がアプリ（メール、エクスプローラーなど）を起動することがある。必要なら、名前表の読み戻し（K4）だけにしてよい。

## チェックリストに照らした確認

- ドライバの変更が無いので、kbd-filter-driver のチェックリストの対象外。
- key-remap-engine
  - K-4（E0/E1 込みで識別）: `SC_XXXX` はプレフィックスを含む 4 桁で書き、E0 と E1 を区別する。
  - M-4（指定しない修飾キーに触れない）: 新しいキーは単純キー（Type=2）として出るので、今までのキーと同じ扱い。
