# sirius（天狼）

TSF（Text Services Framework）で IME の状態（ON/OFF・変換モード）を読むための、フック用の DLL のソースです。
kasane-tray が、前面のアプリの IME の状態を、IMM32 に加えて TSF でも読むために使います（計画: フェーズ2 で組み込み）。

- 出典: のどか（nodoka）で使っている天狼（sirius）の `sirius_hook`・`include\sirius`・`sdk\getCommonValues`
  （http://www.sirius.spline.tv/wiki/WikiStart ）。
- ライセンス: MIT License, Copyright (C) 2008-2010, MATSUMOTO Reiji。全ファイルの冒頭に表記しています。
  ライセンス全文は `third-party\sirius-MIT-LICENSE.txt`。

## 構成

| フォルダ | 内容 |
|---|---|
| `sirius_hook\` | DLL 本体（x64 のみ）。出力: `sirius\bin\<構成>\sirius_hook_for_kasane_x64.dll` |
| `include\sirius\` | 共通ヘッダ（`commonValues.h` ほか） |
| `sdk\getCommonValues\` | DLL をロードして、共有メモリの IME の状態を表示するサンプル（診断用。元のまま） |

ビルド: `MSBuild sirius\sirius_hook\sirius_hook.vcxproj -p:Configuration=Release -p:Platform=x64`（VS 2022、v143、CRT は `/MT`）。

## 仕組み

`SiriusSetupHook` が、`SetWindowsHookEx` のグローバルフック（`WH_CALLWNDPROCRET`・`WH_CALLWNDPROC`・`WH_GETMESSAGE`）を入れる。
同じデスクトップの GUI スレッドに DLL が読み込まれ、そのスレッドで TSF のコンパートメント
（`KEYBOARD_OPENCLOSE`・`INPUTMODE_CONVERSION`）を監視して、値を共有メモリ（`CcommonValues`）に書く。
別プロセスの TSF の値は、プロセスの外からは読めないため（`docs/KNOWN-ISSUES.md` 項目4）、この方式をとる。

## のどかの元のソースとの違い

取り込みのコミット（元のまま）の後に、次を変えていきます（変更は `git log -- sirius` で追えます）。

- x64 だけにした（`sirius_hook.vcxproj`）。出力先は `sirius\bin\`。
- （予定）共有メモリ名・登録メッセージ名を kasane 専用にし、のどかとの衝突を避ける。
- （予定）のどかへの通知（`nodokaTasktray`、`WM_APP+123`）を削除。
- （予定）Explorer の起動が遅くなる不具合を直す。

## 既知の不具合

- **フックを入れると Explorer の起動が遅くなる**（のどか側でも既知）。原因の候補は、`DllMain` での重い初期化、
  共有メモリの取得に失敗したプロセスでの全メッセージ再試行など。直すまでは、リリースに入れない。
- 32bit（x86）のプロセスには、x64 の DLL は入らない（IMM32 のみ）。
