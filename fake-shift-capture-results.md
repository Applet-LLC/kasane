# 右 Shift が `E0 36` のキーボードの実測（疑似 Shift の捕捉）試験結果

- 対象: 統合ブランチ `test/pause-and-fake-shift`（`d1eaae5`。`feature/capture-fake-shift` の `9c8f55c` を含む）。ドライバ・KeymapTool の入れ替えと Verifier の状態は、`pause-remap-results.md` と同じ（`kbdlayer.sys` の先頭ハッシュ `D58040CD98533752`、Verifier 有効、再起動済み）。
- 試験日: 2026-10-14（試験担当）
- 結論: **試験できる項目（FS1・FS2・FS3・FS7・FS8）は、すべて PASS。** FS4・FS5・FS6 は、`E0 36` を出すキーボードが無く、未実施。FS9 は一部（Verifier 下での繰り返しは済み、抜き差しは未実施）。

| # | 結果 | 内容（実測） |
|---|---|---|
| FS1 | PASS | 通常のキーボード（内蔵）で `--capture 55F70000` に右 Shift（`0x36` を注入）: `captured scancode 0x0036` |
| FS2 | PASS | `--capture 55F70000 6000 --suppress --allow-fake-shift` に PrintScreen（`E0 2A`・`E0 37` の押下と解放を注入）: **`captured scancode 0xE02A`**（前置きの疑似 Shift が記録される。`--suppress` は、PrintScreen が OS に届いて画面キャプチャが起動しないようにするため） |
| FS3 | PASS | 同じ PrintScreen を `--allow-fake-shift` なしで: **`captured scancode 0xE037`**（従来どおり。疑似 Shift は除外） |
| FS4 | 未実施 | `E0 36` を出すキーボードが無い |
| FS5 | 未実施 | 同上（GUI のキャリブレーション。実機が要る） |
| FS6 | 未実施 | 同上 |
| FS7 | PASS | 旧クライアント（`--capture` が 8 バイトの入力を送る旧版の `KeymapTool`。2026-10-12 の Debug ビルド）: PrintScreen を `--suppress` で実測 → `captured scancode 0xE037`、通常のキー（`0x2E`）→ `captured scancode 0x002E`（従来どおり。`Flags=0` として動く） |
| FS8 | PASS | IOCTL を直接呼ぶ（昇格したプロセスから、`IOCTL_KBDLAYER_CAPTURE_NEXT_KEY`＝`0x80208014` に、12 バイトの入力）: `Flags=0x00000002`、`0x80000000`、`0xFFFFFFFE`（未定義のビット）は、**すべて即座に `ERROR_INVALID_PARAMETER`（87）**（経過 0 ms）。`Flags=0x00000001`（有効）は受け付けられ、300 ms で `TimedOut=1`。旧版の 8 バイトの入力も受け付けられ、`TimedOut=1` |
| FS9 | 一部 | Verifier 有効の状態で、FS1・FS2・FS3・FS7・FS8 を実行（クラッシュ・ハングなし、Verifier の検出なし、ミニダンプなし）。キーボードの抜き差しは、この項目としては行っていない（別の試験で、抜き差し・デバイスの再起動を行ったが、`--capture` の最中ではない） |

## メモ
- `--capture ... --suppress` の最中の PrintScreen は、OS に渡されない（画面キャプチャは起動しない）。`--suppress` なしで PrintScreen を捕捉しようとすると、OS の画面キャプチャが起動する恐れがあるため、FS2・FS3・FS7 は `--suppress` で行った（`--suppress` の有無は、捕捉の結果には影響しない）。
- 試験後: 昇格のプロセスをすべて終了、レイヤーマップは変更していない。
