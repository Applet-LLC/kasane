# チュートリアル: CLIでキーを入れ替える（JIS / US キーボード）

LayerDriverをインストールしてから、手元のキーボードに合わせてキャリブレーションを行い、
キーの入れ替え（リマップ）を設定・保存し、実際に動くことを確かめるまでの手順です。
`KeymapTool.exe`（コマンドライン）だけで完結します。画面で操作したい場合は、同じ設定を
Keymap Editor（GUI）で行う [TUTORIAL-GUI.md](TUTORIAL-GUI.md) を参照してください
（インストールとライセンスの手順0〜2は共通です）。

この手順で作る設定は次の2つです。お使いのキーボードに合う方を選んでください。

| キーボード | 入れ替えるキー | 使うキーマップ |
|---|---|---|
| JIS（日本語109キー） | **英数キー**（`Caps Lock 英数`と刻印されたキー）と **左Ctrl** | `tutorial-jis109-eisu-lctrl.json` |
| US（英語104キー） | **CapsLock** と **左Ctrl** | `tutorial-us104-caps-lctrl.json` |

> **補足:** JISキーボードの英数キーは、USキーボードのCapsLockと同じ信号（スキャンコード`0x3A`）を
> 送ります。そのためキーマップの中では、どちらも`KC_CAPSLOCK`という名前で書かれています。

テンキー単体（17キー）のキーボードを使う場合は、手順0〜4を行ったあと
[テンキー単体の場合](#テンキー単体の場合)に進んでください。

---

## 全体の流れ

1. インストールする（再起動あり）
2. ライセンスを有効にする（再起動あり）
3. 作業フォルダとキーマップを用意する
4. 設定したいキーボードのIDを調べる
5. キャリブレーション（全キーを順番に押す）
6. キャリブレーション結果を確認する
7. キーマップを書き込み、有効にする
8. 動作を確認する
9. 再起動後も設定が残っていることを確認する

キーを2つ入れ替えるだけなら、手順5〜7を短くできる近道もあります
（[近道: 2キーだけ設定する](#近道-2キーだけ設定する)）。まずは通常の手順で全体像をつかむことをおすすめします。

---

## 0. 前提条件

- Windows 11 / Windows 10 1903以降（x64 または ARM64）
- インストールには管理者権限が必要です。手順3以降の`KeymapTool.exe`も、設定の書き込み（`--apply`・`--set-*`・`--clear`系）と
  キャリブレーション（`--calibrate`・`--capture`系）は**管理者として実行したコマンドプロンプト／PowerShell**で行ってください。
  管理者権限なしで使えるのは、一覧・設定の読み出し（`--list`・`--get`系）とON/OFF（`--on`/`--off`）だけです
  （一般ユーザー権限のアプリにキー入力を盗み見られたり、勝手にキー入力を仕込まれたりしないように、ドライバが制限しています）。
- 有効なLayerDriverのライセンスキーと、アクティベート時のインターネット接続が必要です。
- **テスト署名モード**: 現在のドライバはWHQL署名を取得していないため、テスト署名モードを
  有効にしたPCでのみ読み込まれます。未設定の場合は、管理者のPowerShellで次を実行して再起動します
  （UEFIのセキュアブートが有効だと設定できません。先にセキュアブートを無効にしてください）。

  ```powershell
  bcdedit /set testsigning on
  ```

---

## 1. インストールする

1. インストーラー（MSI）を実行します。
2. 途中の「デバイスの識別方法」画面では、通常は既定の **デバイスインスタンスパス** のままで構いません。
   - デバイスインスタンスパス: 接続先（USBポート等）ごとに別のキーボードとして扱います。
     **同じキーボードでも、別のUSBポートに挿すと設定が引き継がれません。**
   - デバイスID: キーボード固有のIDで区別します。ポートを差し替えても設定が引き継がれます。
3. 完了したら **PCを再起動** します（ドライバは再起動後に有効になります）。

インストール先は `C:\Program Files\layerdriver-subscription\` です。
`KeymapTool.exe`はこのフォルダにあります。

## 2. ライセンスを有効にする

1. 再起動してログインすると、**LayerDriver License Manager** が自動で開きます
   （開かない場合はスタートメニューから起動します）。
2. ライセンスキーを入力して **アクティベート** を押します。
3. 「OK（ライセンス有効）」と表示されたら、**もう一度PCを再起動** します。

> **なぜ2回目の再起動が必要か:** ドライバがライセンスを確認するのは、キーボードを認識した瞬間だけです。
> アクティベート直後はまだ古い状態のままなので、再起動（USBキーボードなら抜き差しでも可）で
> キーボードを認識し直させる必要があります。

## 3. 作業フォルダとキーマップを用意する

PowerShellを開き（管理者でなくて構いません）、作業フォルダを作ってそこに移動します。
キャリブレーション結果のファイルはこのフォルダに保存されます。

```powershell
mkdir $env:USERPROFILE\LayerDriver
cd $env:USERPROFILE\LayerDriver
Set-Alias KeymapTool "C:\Program Files\layerdriver-subscription\KeymapTool.exe"
```

`Set-Alias`は、長いパスを毎回入力しなくて済むようにするための設定です
（PowerShellを開き直したら、もう一度実行してください）。

次に、使うキーマップファイルを作業フォルダにコピーします。キーマップはインストール先の
`C:\Program Files\layerdriver-subscription\samples\` にあります（リポジトリでは `driver/KeymapTool/samples/`）。

```powershell
Copy-Item "C:\Program Files\layerdriver-subscription\samples\tutorial-*.json" .
```

- JISキーボード: `tutorial-jis109-eisu-lctrl.json`
- USキーボード: `tutorial-us104-caps-lctrl.json`

どちらも「全キーがそのまま動き、2つのキーだけが入れ替わる」内容の1レイヤーのキーマップです。

## 4. 設定したいキーボードのIDを調べる

```powershell
KeymapTool --list
```

出力例:

```
DeviceCount=2
  UniqId=3F2A9C01 HasLayerMap=0 Enabled=0 ActiveLayerMask=0x00 HardwareId=HID\VID_046D&PID_C31C&REV_6400&MI_00
  UniqId=81D0E47B HasLayerMap=0 Enabled=0 ActiveLayerMask=0x00 HardwareId=ACPI\VEN_MSFT&DEV_0001
```

キーボードごとに1行表示されます。`UniqId`の8桁の値が、以降のコマンドで使うIDです。

- ノートPCでは、内蔵キーボード（`ACPI\...`で始まることが多い）と外付けキーボード（`HID\VID_...`）が
  両方表示されます。
- どれが目的のキーボードか分からないときは、次のコマンドを実行してから5秒以内に
  **目的のキーボードで** 何かキーを押します。`captured scancode ...` と出ればそのIDで正解です
  （`timed out` なら別のIDを試します）。

  ```powershell
  KeymapTool --capture 3F2A9C01 5000
  ```

以降の例では、調べたIDを `<ID>` と書きます。実際の値（例: `3F2A9C01`）に置き換えてください。

> `UniqId=00000000` と表示される場合は、ライセンスがまだ有効になっていません（手順2を参照）。

## 5. キャリブレーション（全キーを順番に押す）

キャリブレーションは、キーマップの「n番目」が、あなたのキーボードの **どの物理キー** かを
ドライバに覚えさせる作業です。案内に従ってキーを1つずつ押していきます。

### 始める前に（重要）

コマンドの最後に付けている `--suppress` は、キャリブレーション中に押したキーを
**Windowsに渡さない**（オフモード）ための指定です。これが無いと、押したEnter・Winキー・
PrintScreenなどがそのままWindowsに届き、スタートメニューが開いたり、PowerShellに文字が溜まって
ツールの終了後にコマンドとして実行されたりします。

`--suppress` を付けている間は、対象のキーボードで押したキーは何も起こしません
（CapsLock等のランプも変わりません）。次のことを覚えておいてください。

- **途中でやめたいとき:** PowerShellのウィンドウを **マウスで閉じて** ください
  （対象のキーボードからは`Ctrl+C`も届きません）。ウィンドウを閉じた瞬間にキーボードは元に戻ります。
- **キーを押さずに約60秒たつと** 自動的に中断され、キーボードは元に戻ります（何も保存されません）。
- コマンドが終われば（成功・失敗とも）、キーボードはすぐ元に戻ります。
- ノートPCのタッチパッドやマウスは、キャリブレーション中も普通に使えます。

### 実行する

JISキーボード（109キー）:

```powershell
KeymapTool --calibrate-simple <ID> 109 profile-jis.json --suppress
```

USキーボード（104キー）:

```powershell
KeymapTool --calibrate-simple <ID> 104 profile-us.json --suppress
```

画面に `[1/109] Press the key at matrix (row=0, col=0)...` のように番号が表示されます。
下の順番どおりに押してください。キーボードを **上の段から順に、各段の中では左から右へ** なぞる順番です。
右側の小さな島（Insert〜PageDown、矢印キー）とテンキーも、その段に含まれます。

**JISキーボード（109キー）**

| 番号 | 押すキー |
|---|---|
| 1〜16 | Esc, F1〜F12, PrintScreen, ScrollLock, Pause |
| 17〜31 | 半角/全角, 1〜0, -, ^, ¥, BackSpace |
| 32〜34 | Insert, Home, PageUp |
| 35〜38 | NumLock, テンキー/, テンキー*, テンキー- |
| 39〜52 | Tab, Q〜P, @, [, Enter |
| 53〜55 | Delete, End, PageDown |
| 56〜59 | テンキー7, テンキー8, テンキー9, テンキー+ |
| 60〜72 | **英数（Caps Lock）**, A〜L, ;, :, ]（む） |
| 73〜75 | テンキー4, テンキー5, テンキー6 |
| 76〜88 | 左Shift, Z〜M, `,`, `.`, /, \（ろ）, 右Shift |
| 89 | ↑ |
| 90〜93 | テンキー1, テンキー2, テンキー3, テンキーEnter |
| 94〜104 | **左Ctrl**, 左Win, 左Alt, 無変換, Space, 変換, カタカナ/ひらがな, 右Alt, 右Win, アプリケーション, 右Ctrl |
| 105〜107 | ←, ↓, → |
| 108〜109 | テンキー0, テンキー. |

**USキーボード（104キー）**

| 番号 | 押すキー |
|---|---|
| 1〜16 | Esc, F1〜F12, PrintScreen, ScrollLock, Pause |
| 17〜30 | `` ` ``, 1〜0, -, =, Backspace |
| 31〜33 | Insert, Home, PageUp |
| 34〜37 | NumLock, テンキー/, テンキー*, テンキー- |
| 38〜51 | Tab, Q〜P, [, ], \ |
| 52〜54 | Delete, End, PageDown |
| 55〜58 | テンキー7, テンキー8, テンキー9, テンキー+ |
| 59〜71 | **CapsLock**, A〜L, ;, ', Enter |
| 72〜74 | テンキー4, テンキー5, テンキー6 |
| 75〜86 | 左Shift, Z〜M, `,`, `.`, /, 右Shift |
| 87 | ↑ |
| 88〜91 | テンキー1, テンキー2, テンキー3, テンキーEnter |
| 92〜99 | **左Ctrl**, 左Win, 左Alt, Space, 右Alt, 右Win, Menu（アプリケーション）, 右Ctrl |
| 100〜102 | ←, ↓, → |
| 103〜104 | テンキー0, テンキー. |

最後に `Calibration profile saved to profile-jis.json`（USは`profile-us.json`）と出れば完了です。

- **押し間違えたとき:** PowerShellのウィンドウをマウスで閉じて中断し、PowerShellを開き直して
  （手順3の`cd`と`Set-Alias`も実行し直して）最初からやり直してください。1つ前に戻る機能はありません。
  中断した場合、プロファイルは保存されません。
- 10秒間キーを押さないと `(no key detected within timeout, retrying)` と表示されますが、
  故障ではありません。同じ番号のまま待っているので、そのまま続けて押してください
  （約60秒押さないままだと中断されます）。
- テンキーの無いキーボード（テンキーレス）では、この表の数字どおりに押すことができません。
  この場合は[近道: 2キーだけ設定する](#近道-2キーだけ設定する)の方法を使ってください。

## 6. キャリブレーション結果を確認する

押し間違えがないか、保存されたファイルを確認します（ファイル名はUSなら`profile-us.json`）。

```powershell
$p = Get-Content .\profile-jis.json -Raw | ConvertFrom-Json
$i = 0; $p.positions | ForEach-Object { $i++; '{0,3}: 0x{1:X4}' -f $i, $_.scanCode }
$p.positions | Group-Object scanCode | Where-Object Count -gt 1
```

1行目・2行目で「番号: スキャンコード」の一覧が表示されます。特に次の値を確認してください。

| 確認する番号 | JIS | US | 期待される値 |
|---|---|---|---|
| PrintScreen | 14 | 14 | `0xE037` |
| Pause | 16 | 16 | `0xE11D` |
| 英数 / CapsLock | 60 | 59 | `0x003A` |
| 左Ctrl | 94 | 92 | `0x001D` |

3行目のコマンドは、**同じキーが2回記録されていないか** を調べるものです。何も表示されなければ問題ありません。
何か表示された場合は、同じキーを2回押しています。手順5をやり直してください。

## 7. キーマップを書き込み、有効にする

JISキーボード:

```powershell
KeymapTool --apply-simple <ID> tutorial-jis109-eisu-lctrl.json profile-jis.json
KeymapTool --on <ID>
```

USキーボード:

```powershell
KeymapTool --apply-simple <ID> tutorial-us104-caps-lctrl.json profile-us.json
KeymapTool --on <ID>
```

- `--apply-simple` はキーマップをドライバに書き込みます。この時点ではまだ無効（OFF）で、
  `SET_LAYERMAP ok ... Note: still OFF until you run --on` と表示されます。
- `--on` で有効になり、その瞬間から入れ替えが効きます。
- 書き込んだ設定と有効/無効の状態は、ドライバが自動的に保存します（別途の保存操作は不要です）。

`KeymapTool --list` を実行すると、対象のキーボードが `HasLayerMap=1 Enabled=1` になっています。

## 8. 動作を確認する

メモ帳を開いて確認します。

**JISキーボード（英数 ⇔ 左Ctrl）**

- **英数キー** を押しながら `A` → 全選択される（英数キーが左Ctrlとして動く）。
- **英数キー** を押しながら `C` / `V` → コピー / 貼り付けができる。
- 日本語入力（IME）をオンにした状態で **左Ctrl** を押す → 英数キーとして動く
  （入力モードが英数に切り替わる）。
- **Shift + 左Ctrl** → CapsLockが切り替わる（元の英数キーの `Shift+英数` と同じ動作）。
- 右Ctrlは今までどおりCtrlとして動く（入れ替えたのは左Ctrlだけ）。
- その他のキーが今までどおり動く。

**USキーボード（CapsLock ⇔ 左Ctrl）**

- **CapsLock** を押しながら `A` → 全選択される（CapsLockキーが左Ctrlとして動く）。
- **CapsLock** を押しながら `C` / `V` → コピー / 貼り付けができる。
- **左Ctrl** を押す → CapsLockが切り替わる（ランプが点灯/消灯し、以降の文字が大文字/小文字になる）。
- 右Ctrlは今までどおりCtrlとして動く。
- その他のキーが今までどおり動く。

## 9. 再起動後も設定が残っていることを確認する

PCを再起動し、ログイン後にもう一度手順8を試します。設定はドライバが保存しているので、
再起動後も`KeymapTool`を実行しなくても入れ替えが効いた状態で起動します。

---

## 設定を変える・元に戻す

| やりたいこと | コマンド |
|---|---|
| 一時的に入れ替えを止める（設定は残る） | `KeymapTool --off <ID>` |
| 再開する | `KeymapTool --on <ID>` |
| キーマップを書き換える | `KeymapTool --off <ID>` → JSONを編集 → `KeymapTool --apply-simple <ID> <キーマップ> <プロファイル>` → `KeymapTool --on <ID>` |
| 設定を完全に消す | `KeymapTool --off <ID>` → `KeymapTool --clear <ID>` |

- **ONのままでは`--apply-simple`や`--clear`はできません。** 必ず先に`--off`してください。
- キーマップを書き換えるだけなら、キャリブレーションをやり直す必要はありません
  （同じキーボードなら、手順5で作ったプロファイルをそのまま使えます）。
- キーマップのJSONは、`layout`の中の配列が「キャリブレーションで押した順番」に対応しています。
  例えばJISのキーマップで英数キーの位置（60番目）を別のキーにしたい場合は、配列の60番目の値
  （`KC_LCTRL`）を別のキーコード（例: `KC_ESC`）に書き換えます。
- キーコードには、修飾キー付きのキーも書けます。
  - `LCTL(KC_C)`（Ctrl+C）。短縮形の `C(KC_C)` でも書けます。
  - 修飾キーは `LCTL` `LSFT` `LALT` `LGUI` `RCTL` `RSFT` `RALT` `RGUI`、短縮形は `C` `S` `A` `G` です。
  - 重ねるときは `LCTL(LSFT(KC_T))` のように入れ子にします。
  - 押したまま次のキーを押すと、足した修飾キーは次のキーには掛かりません。
- `LM(1,MOD_LCTL)` は、押している間レイヤー1を有効にし、Ctrlも押した状態にします。
  - 修飾キーを複数指定するときは `MOD_LCTL|MOD_LSFT` のように `|` でつなぎます。
- マクロ（`--set-macro <ID> <番号> <キー...>`）は128個（0〜127）まで作れます。
  - キー列にも `LCTL(KC_C)` のような修飾キー付きのキーを書けます。
  - 修飾キー1つにつき2ステップ使います。1つのマクロは64ステップまでです。
- タップダンス（`--set-tapdance <ID> <番号> <1回タップ> <長押し> <2回タップ> <3回タップ>`）のタップ側には、
  `LCTL(KC_C)` のような修飾キー付きのキーと `MACRO(n)` も書けます。
  - 例: `KeymapTool --set-tapdance <ID> 0 KC_ESC "MT(MOD_LCTL,KC_NO)" "LCTL(KC_C)" "MACRO(0)"`
  - `KC_NO` か `""` にした欄は「動作なし」です（例: 長押しを使わないなら長押しを `KC_NO` に）。
- レイヤー操作には `TO(n)`（レイヤーnだけを有効にする）と `TT(n)`（押している間はMO、続けてタップすると
  ON/OFFを固定。回数は既定5回で、`--set-config ... ttcount=<回数>` で変更）も使えます。
- `MT(...)`・`LT(...)` を押している間に別のキーを押すと長押し扱いになり、そのキーは切り替わった後のレイヤーで
  入力されます。短く押した直後にもう一度押し続けると、短く押したときのキーのリピートになります。
- `OSM(MOD_LSFT)` は One Shot の修飾キーです。短く押して離すと、次に押すキー1回だけに Shift が掛かります
  （押したまま使えば普通の Shift。待っている間にもう一度押すと取り消し）。`OSM(MOD_LCTL|MOD_LSFT)` のように複数も可。
- キー置換（Key Override）: `KeymapTool --set-key-override <ID> <番号> <修飾キー> <キー> <置き換え先>`
  - 例: `KeymapTool --set-key-override <ID> 0 MOD_LSFT KC_BSPACE KC_DELETE`（Shift+BS → Delete）
  - 修飾キーは `"MOD_LCTL|MOD_LSFT"` のように複数書けます（全部押されているときに置き換え。左右は区別しない）。
  - 置き換え先には `"LCTL(KC_Y)"` のような修飾付きキーも書けます。番号（0〜31）だけを指定すると消えます。
- MT/LT の判定方式は `--set-config` で変えられます（既定は `mode=hold`）。
  - `KeymapTool --set-config <ID> 200 50 200 mode=permissive` … 別のキーを押して**離したら**長押し扱い
  - `mode=tap` … 時間内（TapTerm）に MT/LT を離せば短押し扱い
  - `quicktap=<ミリ秒>|off|default`（短押し直後の押し続けでリピートする時間）、`ttcount=<回数>`（TT の固定に必要なタップ回数）
  - 書かなかった項目は今の値のままです。`--get-config <ID>` で確認できます。

---

## テンキー単体の場合

テンキー単体（NumLock、/、*、-、0〜9、.、+、Enterの17キー）のキーボードでの例です。
**NumLockの状態に関係なく、数字キーが常に数字を入力する** ようにします。

普通、NumLockがOFFのときテンキーの数字キーは Home・End・矢印などとして働きます。この設定では
テンキーの数字キーを、キーボード最上段の数字キー（`KC_1`〜`KC_0`）と同じ信号に置き換えます。
最上段の数字はNumLockの影響を受けないので、いつでも数字が入力されます（`.`も同様に`KC_DOT`にします）。
使うキーマップは `tutorial-10key-numbers.json` です（手順3でコピー済み）。

### キャリブレーション

手順5の代わりに、次を実行します（キー数は`17`）。

```powershell
KeymapTool --calibrate-simple <ID> 17 profile-10key.json --suppress
```

押す順番は、**上の段から順に、各段の中では左から右へ** です。

| 番号 | 押すキー |
|---|---|
| 1〜4 | NumLock, /, *, - |
| 5〜8 | 7, 8, 9, + |
| 9〜11 | 4, 5, 6 |
| 12〜15 | 1, 2, 3, Enter |
| 16〜17 | 0, . |

### 書き込みと確認

```powershell
KeymapTool --apply-simple <ID> tutorial-10key-numbers.json profile-10key.json
KeymapTool --on <ID>
```

メモ帳を開き、NumLockを **OFF** にしてから（ランプが消えた状態）数字キーと`.`を押します。
矢印やHome/Endとして動かず、数字とピリオドが入力されれば成功です。NumLockをONにしても同じく数字が入力されます。
`/`・`*`・`-`・`+`・Enterは今までどおり動きます。

> **補足:** 日本語入力（IME）がオンのときに全角の数字が入力される場合は、IMEの設定によるものです
> （テンキーの数字と最上段の数字で、IMEの扱いが異なることがあります）。

---

## 近道: 2キーだけ設定する

入れ替えたいキーが少ないときは、全キーをキャリブレーションする必要はありません。
キーマップに含まれていないキーは、そのまま（入れ替えなしで）動くためです。
テンキーレスなど、表とキーの数が合わないキーボードでもこの方法が使えます。

キーマップ `tutorial-swap-2key.json`（JIS/US共通）を作業フォルダにコピーし、手順5〜7の代わりに次を実行します。

```powershell
KeymapTool --calibrate-simple <ID> 2 profile-2key.json --suppress
```

案内されたら、**1番目に英数キー（USはCapsLock）**、**2番目に左Ctrl** を押します。

```powershell
KeymapTool --apply-simple <ID> tutorial-swap-2key.json profile-2key.json
KeymapTool --on <ID>
```

動作確認（手順8）と再起動後の確認（手順9）は同じです。

---

## 親指シフト（NICOLA）を使う

NICOLA配列（親指シフト）での入力に切り替えられます。サンプル `nicola.json` と
`test-keymap-nicola-toggle.json` を作業フォルダにコピーしてください。

1. **IMEの入力方式を決める。** `nicola.json` の `"output"` を、IMEがローマ字入力なら `"romaji"`（既定）、
   かな入力なら `"jis-kana"` にします。
2. **親指キーを決める。** 既定は親指左=無変換・親指右=変換で、単独で押したときは無変換/変換として働きます。
   変えたいときは `"thumbLeft"`/`"thumbRight"`（親指キー）と `"thumbLeftTap"`/`"thumbRightTap"`（単独で押したときに
   送るキー）を書き換えます。例えば親指左をスペースにするなら両方 `KC_SPC` にします。
3. **切り替えキーを1つ決めてキャリブレーションする。** 押すたびにNICOLAのON/OFFを切り替えるキー
   （例: カタカナひらがな）を1回押します。

```powershell
KeymapTool --dump-nicola nicola.json
KeymapTool --calibrate-simple <ID> 1 nicola-profile.json --suppress
KeymapTool --apply-simple <ID> test-keymap-nicola-toggle.json nicola-profile.json
KeymapTool --set-nicola <ID> nicola.json
KeymapTool --on <ID>
```

`--dump-nicola` は、ドライバに送らずに各キーの出力を表示します（IMEの方式で打てない文字は警告が出ます）。

IMEをオンにしてから切り替えキーを押すと親指シフトになり、もう一度押すと元に戻ります。
ON/OFFに合わせてIMEもオン/オフしたい場合は、`"onEnter"`/`"onExit"` に、使っているIMEの設定で
IMEオン/オフに割り当てたキー（例: `KC_HENK`/`KC_MHEN`）を指定します。
判定の細かい調整（同時打鍵判定時間など）は `samples/README.md` の NICOLA の節を参照してください。

やめるときは次を実行します。

```powershell
KeymapTool --clear-nicola <ID>
```

---

## うまくいかないとき

| 症状 | 原因と対処 |
|---|---|
| `KeymapTool`が `Failed to open \\.\KbdLayerControl (kbdlayer.sys installed? ...)` で終了する | ドライバが読み込まれていません（または別の`KeymapTool`が実行中です）。インストール後に再起動したか、テスト署名モードが有効か（前提条件）を確認してください。 |
| `--list`で`UniqId=00000000`しか出ない | ライセンスが有効になっていません。手順2のアクティベートと、その後の再起動を行ってください。 |
| `--apply-simple`が失敗する | ONのままになっていないか確認し、`--off`してから再実行してください。キーマップとプロファイルのキー数が違う場合（例: JISのキーマップにUSのプロファイル）もエラーになります。 |
| `--on`が失敗する | キーマップがまだ書き込まれていません。先に`--apply-simple`を実行してください。 |
| 入れ替えが効かない | `--list`で対象のIDが`HasLayerMap=1 Enabled=1`か確認します。別のキーボードのIDに設定していないかも確認してください（手順4の`--capture`）。 |
| USBポートを差し替えたら設定が消えた | インストール時の識別方法が「デバイスインスタンスパス」だと、ポートごとに別のキーボード扱いになります。新しいポートでも同じ手順で設定するか、元のポートに戻してください。 |
| キャリブレーション後、PowerShellに身に覚えのない文字が入力されている | `--suppress`を付け忘れたため、キャリブレーション中のキー入力がPowerShellに届いたものです。Escで消してください。 |
| `--suppress`で`IOCTL_KBDLAYER_SET_CAPTURE_SUPPRESS failed`と出る | 別のKeymapTool（またはGUI）が同じキーボードでキャリブレーション中です。そちらを終了してから実行してください。 |
| キャリブレーションの途中でキーボードが効かなくなった | `--suppress`の動作です。PowerShellのウィンドウをマウスで閉じるか、約60秒待つと元に戻ります。 |
