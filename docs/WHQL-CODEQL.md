# WHQL 申請の CodeQL 解析と DVL 作成（kasane-whql）

kbdlayer ドライバを WHQL に申請するときの、CodeQL 解析から DVL（Driver Verification Log）の作成までを、`kasane-whql` で実行する。対象は Subsc と Pro の各 x64 / ARM64、合わせて 4 本。

HLK 版フォルダの作成と、ドライバ・シンボルの配置は、従来どおり `E:\HLK\make_hlk_folders.bat` で行う。署名後のパッケージを `driver\signed`、`driver\signed-pro` に置く作業も、このツールの対象外にしている。

## 事前に必要なもの

- `E:\codeql-home\codeql-2.20.1\codeql.cmd`（版は 2.20.1 に固定している）
- `K:\BuildEnv\SetupBuildEnv.cmd`（`build_codeql_*.bat` が呼ぶ）
- HLK 版フォルダ（例: `E:\HLK\2026-11-26 LayerDriver Pro 1.03`）。フォルダ名に `Pro` か `Subsc` が入っていること。
- VS2022 の WDK 拡張（DVL を作るときに使う）

パスと版は、実行ファイルと同じ場所の `kasane-whql.json` で変えられる。

## 使い方

`kasane.sln` の Drivers フォルダにある `kasane-whql` プロジェクトをビルドして使う。リリース用のフルビルドには含めていないので（構成マネージャーの「ビルド」のチェックを外してある）、使うときにプロジェクトを右クリックしてビルドするか、スタートアッププロジェクトにして実行する。出力は `kasane-whql\bin\<プラットフォーム>\<構成>\net8.0-windows\kasane-whql.exe`（Release x64 なら `bin\x64\Release\...`）で、設定ファイル `kasane-whql.json` も同じ場所にコピーされる。VS から実行するときは、`kasane-whql` をスタートアッププロジェクトにして、プロジェクトのプロパティの「デバッグ」にあるアプリケーション引数へ、下のコマンドの `check` 以降を入れる。

リポジトリのルートからは、次のように実行できる。引数なしで実行すると使い方を表示する。

```
dotnet run --project kasane-whql -c Release -- check
dotnet run --project kasane-whql -c Release -- run --hlk-subsc "E:\HLK\<Subsc の版フォルダ>" --hlk-pro "E:\HLK\<Pro の版フォルダ>"
dotnet run --project kasane-whql -c Release -- verify --hlk-subsc "..." --hlk-pro "..."
```

- `check`: 前提のものがそろっているかだけを確かめる。最初に実行する。
- `run`: 4 本を順に処理する。`--edition subsc|pro` と `--arch x64|arm64` で絞れる。1 本が失敗しても残りは続け、最後に一覧を出す。
- `verify`: HLK 版フォルダの成果物（driver、Symbol、SARIF、DVL）がそろっていて、中身が妥当かを確かめる。

## run が 1 本ごとにやること

1. `E:\codeql-home\databases-*` の中身を空にする（この名前のフォルダしか消さない）。
2. `codeql database create` を実行する。ビルドには `driver\kbdlayer\build_codeql_*.bat` を使う。
3. DB を検査する。ソースの場所が `driver\kbdlayer` であること、Pro の DB に `token.c` が無く、Subsc の DB には有ることを見る。
4. `codeql database analyze` で SARIF を作り、検査する。
5. SARIF を `<HLK 版フォルダ>\<x64 または ARM64>\` にコピーする。
6. DVL を作る（下記）。検査して、同じフォルダに `kbdlayer.DVL.XML` として置く。
7. 実行の記録を `<HLK 版フォルダ>\whql-run.log` に追記する。SARIF と DVL の SHA-256 も入る。

Pro の成果物を Subsc のフォルダに置く、といった取り違えは、フォルダ名と対象の版が合わない時点で、1 本も動かす前に止める。

## DVL の作り方

`--dvl manual`（既定）では、ツールが SARIF を `driver\kbdlayer` に置いて待つ。表示に従い、VS2022 で対象のプロジェクト（ARM64 は `kbdlayer arm64`）を選び、Driver → Create Driver Verification Log を実行して Enter を押す。Enter 以降の検査と回収は自動で、`driver\kbdlayer` に置いた SARIF と DVL も片づける。クリップボードのエラーダイアログは、× で閉じてよい。

`--dvl cli` では、`dvl.exe /manualCreate` で作る。VS を操作しなくて済むが、2026-11-26 に試したところ、Code Analysis の結果が入らず、DVL の項目が `CodeAnalysis.NORUN` になった。VS 拡張で作った DVL は `CodeAnalysis.Summary` になる。Semmle の項目は両者で一致した。HLK に出す DVL は、確認が済むまで manual で作ること。

## 失敗したとき

- `database create` が失敗する: `whql-run.log` のビルドのエラーを見る。同じ bat を単独で実行すると原因を追いやすい。
- DB の検査で止まる: 別のドライバの DB が残っているか、版違いのビルドが混ざっている。メッセージのとおり、DB のフォルダを確認する。
- DVL の検査で止まる: 項目名の CPU が対象と違うか、SARIF が反映されていない。SARIF が `Detected` と表示されたことを確認して、その 1 本をやり直す（`--edition` と `--arch` で絞る）。
