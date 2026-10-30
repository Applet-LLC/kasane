# 買い切り版(kasane Pro)のドライバ

Pro のドライバは、サブスク版とは別のバイナリです。`KBDLAYER_PRO` を定義してビルドし、
`LicenseToken` を読まず、`AddDevice` で `License=1` に固定します（`token.c` は含まれません）。

## ビルド

```
msbuild driver\kbdlayer\kbdlayer.vcxproj        /p:Configuration=Release /p:Platform=x64   /p:KbdlayerEdition=Pro
msbuild driver\kbdlayer\kbdlayer_arm64.vcxproj  /p:Configuration=Release /p:Platform=ARM64 /p:KbdlayerEdition=Pro
```

出力は `driver\kbdlayer\x64\Release-Pro\` と `driver\kbdlayer\ARM64\Release-Pro\` です。
サブスク版の `Release\` とは別なので、取り違えません。

## WHQL署名済みパッケージの置き場所

サブスク版の `driver\signed\` とは別に、次に置きます（どちらも git 管理外）。

| アーキテクチャ | 置き場所 |
|---|---|
| x64 | `driver\signed-pro\kbdlayer\`（`kbdlayer.inf`, `kbdlayer.sys`, `kbdlayer.cat`） |
| ARM64 | `driver\signed-pro\kbdlayer-arm64\`（同上） |

`setup\Prepare-Staging.ps1 -Edition Pro`（または環境変数 `KASANE_EDITION=Pro`）は、
この置き場所と `Release-Pro` の出力だけを見ます。`driver\signed\` や `x64\Release\` のドライバは使いません。

## 確認

- `kbdlayer.sys` に `LicenseToken` の文字列が含まれないこと。
- サブスク版と同じ PC に同時にインストールしないこと（サービス名 `kbdlayer` が同じ）。
