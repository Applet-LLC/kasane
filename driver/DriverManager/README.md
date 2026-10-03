# DriverManager

Forked from kbdaddid_subscribe's `addid/DriverManager/README.md` for this project (formerly LayerDriver, now kasane)
(renames: kbdaddid -> kbdlayer; mouaddid/mouse support removed, kasane has no mouse component).

## 概要

DriverManagerは、kbdlayer.sys、および nodokad.sys のインストール・アンインストールを支援するコマンドラインユーティリティです。

## 主な機能

- ドライバ（kbdlayer, nodokad）のインストール・アンインストール
- UpperFiltersレジストリの自動編集
- INFファイルの検出とドライバパッケージ登録
- アンインストール時に、オプションで INF のパス（または INF が入ったフォルダ）を指定可能（古いドライバ・12形式のアンインストール用）
- 操作完了後に再起動が必要な場合は通知

## ディレクトリ構成と配置

DriverManager.exeは、以下のようなディレクトリ構成で使用してください。

```
x64/
└─ Debug/ または Release/
    ├─ DriverManager.exe
    ├─ kbdlayer/
    │   ├─ kbdlayer.sys
    │   └─ kbdlayer.inf
    └─ nodokad/
        ├─ nodokad.sys
        └─ nodokad.inf
```

DriverManager.exeは、自身が存在するディレクトリ（例: x64/Debug/）内の kbdlayer/、nodokad/ サブディレクトリにある .inf / .sys ファイルを参照します。  
ビルド後、これらのフォルダ・ファイルを x64/Debug/ または x64/Release/ 配下にコピーしてから実行してください。

## 使い方

### コマンド形式

```
DriverManager.exe [command] [driver_type] [optional_inf_path]
```

- **command**: `install` または `uninstall`
- **driver_type**: `keyboard` | `nodokad`
- **optional_inf_path**: アンインストール時のみ指定可能。INF ファイルのパス、または INF が入ったフォルダのパス。指定しない場合は exe と同じディレクトリ内の `driver_type` フォルダの INF を参照します。

### インストール

#### キーボードドライバ
```
DriverManager.exe install keyboard
```

#### nodokad（キーボード上位フィルタ）
```
DriverManager.exe install nodokad
```
exe と同じディレクトリの `nodokad\nodokad.inf` および `nodokad.sys` を参照します。

### アンインストール

#### キーボードドライバ
```
DriverManager.exe uninstall keyboard
```

#### nodokad（通常）
```
DriverManager.exe uninstall nodokad
```
exe と同じディレクトリの `nodokad\nodokad.inf` を参照してパッケージをアンインストールします。

#### nodokad（古いドライバ・12形式の場合）

以前 DirID 12 形式でインストールした nodokad をアンインストールする場合は、インストール時に使った INF のパス（または INF が入ったフォルダのパス）を第3引数で指定してください。

例（フォルダを指定）:
```
DriverManager.exe uninstall nodokad "C:\path\to\keyremap.Win\nodokad\drivers\x64"
```

例（INF のフルパスを指定）:
```
DriverManager.exe uninstall nodokad "C:\path\to\keyremap.Win\nodokad\drivers\x64\nodokad.inf"
```

手順の目安: サービス停止（`sc stop nodokad` など）→ 上記 uninstall → 再起動 → 新しい nodokad.inf / nodokad.sys を exe 配下の `nodokad\` に置き → `install nodokad` で新ドライバをインストール。
