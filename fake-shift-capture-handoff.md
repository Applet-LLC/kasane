# 右 Shift が E0 36 のキーボードの実測（疑似 Shift の捕捉）申し送り

- ブランチ: `feature/capture-fake-shift`（`main` の上）。**実機が無いため未試験**。将来、`E0 36` を出すキーボードが手に入ったときの試験項目。
- 変更: `CAPTURE_NEXT_KEY` の入力に `Flags` を追加（ドライバ）。`KeymapTool --capture ... --allow-fake-shift`。GUI は Shift のスロット（左右）の実測中だけ付ける。`WAIT_KEY_EVENT` は変更なし。

| # | 内容 | 期待値 |
|---|---|---|
| FS1 | 通常のキーボードで `--capture <UniqId>` に右 Shift | `0x0036` |
| FS2 | 通常のキーボードで `--capture <UniqId> --allow-fake-shift` に PrintScreen | 前置きの `0xE02A` が記録される（仕様。Shift 以外では使わない） |
| FS3 | 同、`--allow-fake-shift` なしで PrintScreen | `0xE037`（従来どおり。疑似 Shift は除外） |
| FS4 | **`E0 36` を出すキーボード**で `--allow-fake-shift` に右 Shift | `0xE036` が記録される。印なしでは記録されない（タイムアウト） |
| FS5 | GUI のキャリブレーション（個別）で、右 Shift のスロットを選んで押す | `0xE036` が記録される。PrintScreen のスロットでは `0xE037` |
| FS6 | FS5 の後に適用。右 Shift の位置のキーを押す | リマップが効く。出力が `0x36`（E0 なし） |
| FS7 | 旧クライアント互換: 8 バイトの入力を送る古い KeymapTool/GUI | 従来どおり動く（`Flags=0`） |
| FS8 | 不正: `Flags` に未定義のビット | `STATUS_INVALID_PARAMETER`（`ERROR_INVALID_PARAMETER`） |
| FS9 | Verifier 下で、上を繰り返す。キーボードの抜き差し | クラッシュ・ハングなし |

FS1・FS3・FS7・FS8 は、`E0 36` のキーボードが無くても試験できる（FS8 は IOCTL を直接呼ぶ）。
