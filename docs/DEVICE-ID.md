# キーボードID（DeviceId）の算出方法

kasane のドライバ（kbdlayer.sys）が各キーボードに付けるIDを、他のアプリが同じ値で算出するための仕様。
算出方法は kbdaddid と共通で、実装は `driver/common/hash.c`（ハッシュ）、`driver/kbdlayer/Driver.c` の `AddDevice`（本線）、
`driver/common/fallback.c`（フォールバック）にある。

この文書の値は、2026-10-06 に実機（USBキーボード4台＋内蔵ACPI 1台、`DeviceIdMode=0`）で、
`KeymapTool --list` の出力と一致することを確認した（第7節）。

## 1. 概要

各キーボードには、まず次の2つのキーが決まる。

| 名前 | 中身 | 用途 |
|---|---|---|
| `InstanceKey` | DeviceInstanceId のハッシュ | 1台ごとに異なる。永続化スコープ「Instance」 |
| `ModelKey` | HardwareId（先頭の1件）のハッシュ | 同型番で同じ値。永続化スコープ「Model」（既定） |

どちらも **16bitのハッシュを上位16bitに置いた32bit値**（下位16bitは常に0）。`0xAC0C0000` なら、ハッシュは `0xAC0C`。

`DeviceId`（`KBDLAYER_TARGET.UniqId`、キー入力の `ExtraInformation` 上位16bit に載る値）は、`DeviceIdMode` によって、どちらのキーを使うかが決まる。

| `DeviceIdMode` | 名称 | DeviceId に使うキー |
|---|---|---|
| 0（既定。値が無い・読めないときも 0） | デバイスインスタンスIDモード | `InstanceKey` |
| 1 | ハードウェアIDモード | `ModelKey` |

`DeviceIdMode` は `HKLM\SYSTEM\CurrentControlSet\Services\kbdlayer\Parameters\DeviceIdMode`（REG_DWORD）。
`AddDevice` の時点（デバイスの接続時、再起動時、`pnputil /restart-device` 時）にだけ読まれるので、変更後は再接続が要る。
選んだキーが取得できなかった場合などは、第5節のフォールバックで決まる。

## 2. ハッシュ関数（16bit FNV-1a）

文字列を UTF-16LE の2バイトずつ（下位バイト → 上位バイトの順）に分け、1バイトごとに次を行う。

```
hash = 0x9DC5                      // 初期値（32bit FNV の offset basis 0x811C9DC5 の下位16bit）
各バイト b について:
    hash ^= b
    hash = (hash * 0x0193) & 0xFFFF   // 0x0193 は 32bit FNV prime 0x01000193 の下位16bit
結果 = hash << 16
```

- 終端のNULは含めない。空文字列なら `0x9DC50000`。
- 大文字・小文字を区別する（`A` と `a` は別のハッシュ）。**入力文字列の大文字小文字を、ドライバが見る形に合わせる必要がある**（次節）。

参照実装（Python）:

```python
def fnv1a16_upper(s: str) -> int:
    h = 0x9DC5
    for ch in s:
        c = ord(ch)                      # BMP内の文字を想定（UTF-16の1ユニット）
        for b in (c & 0xFF, (c >> 8) & 0xFF):
            h ^= b
            h = (h * 0x0193) & 0xFFFF
    return h << 16
```

BMP外の文字（サロゲートペア）は、UTF-16 の各ユニットを順に処理する。デバイスIDには通常現れない。

## 3. 本線（通常の経路）

`AddDevice` は、PDO（物理デバイスオブジェクト＝キーボードのデバイスノード）から次の2つの文字列を取り、両方のハッシュを常に計算する。

| 文字列 | 取得元 | ハッシュ |
|---|---|---|
| DeviceInstanceId | `DEVPKEY_Device_InstanceId`（文字列型。空でない） | **InstanceKey** |
| HardwareId の先頭の1件 | `DevicePropertyHardwareId`（MULTI_SZ）の最初の文字列 | **ModelKey** |

その後、`DeviceId` を次のように決める。

- `DeviceIdMode = 0` → `DeviceId = InstanceKey`
- `DeviceIdMode = 1` → `DeviceId = ModelKey`

### 3.1 入力文字列の正確な形

**加工はしない。** 正規化（文字の除去、大文字化）も、他のHardwareIdとの連結も無い。OSが返した文字列をそのままハッシュする。

**HardwareId**: MULTI_SZ の先頭の1件だけ（NULまで）。2件目以降は使わない。先頭は最も具体的なIDで、改訂番号（`REV_xxxx`）を含む。
例: `HID\VID_04FE&PID_0016&REV_0224&MI_00`。改訂番号が違えば、同じ製品でも ModelKey は変わる。

**DeviceInstanceId**: `<バス>\<ハードウェアID>\<インスタンス部分>` の全体。**大文字小文字に注意:**
カーネルが返す文字列は、前半（バス・VID/PID）が大文字、**後半（インスタンス部分）が小文字**になっている。

```
HID\VID_04FE&PID_0016&MI_00\d&70295df&0&0000      ← ドライバが見る形（これをハッシュする）
HID\VID_04FE&PID_0016&MI_00\D&70295DF&0&0000      ← Get-PnpDevice 等が返す形（そのままでは一致しない）
```

ユーザーモードで正しい文字列を得る確実な方法は、**レジストリ `HKLM\SYSTEM\CurrentControlSet\Enum\<バス>\<ハードウェアID>\<インスタンス>` のキー名をそのまま連結する**こと。
キー名はOSが保持している元の大文字小文字のまま見える（上の例なら `Enum\HID\VID_04FE&PID_0016&MI_00` の下に `d&70295df&0&0000`）。
CM_Get_Device_ID や Get-PnpDevice の戻り値は、インスタンス部分が大文字になる場合がある。その場合は、最後の `\` より後ろを小文字にすれば、
今回確認した5台では一致した。ただし USB のシリアル番号を持つデバイスは、インスタンス部分がシリアル番号そのもの（デバイスが報告する大文字小文字）になりうるので、
その場合は小文字化せず、レジストリのキー名を使う（今回の実機には該当するデバイスが無く、未確認）。

### 3.2 キーボードの種類ごとの例

| 種類 | HardwareId の先頭（ModelKey の入力） | DeviceInstanceId（InstanceKey の入力） |
|---|---|---|
| USB/Bluetooth の HID キーボード | `HID\VID_xxxx&PID_xxxx&REV_xxxx`（複合デバイスは末尾に `&MI_00`） | `HID\VID_xxxx&PID_xxxx[&MI_00]\<generated>&0&0000` |
| 内蔵（PS/2 エミュレーション、ACPI） | `ACPI\VEN_LEN&DEV_0071`（機種による） | `ACPI\LEN0071\4&e940ebc&0` |

`<generated>` 部分（`d&70295df&0&0000` や `c&117457ed&0&0000`）は、OSが接続位置から作る。
**同じキーボードでも、別のUSBポートに挿すと InstanceKey が変わる**。同型番を2台挿せば ModelKey は同じ、InstanceKey は別になる。

## 4. ハードウェアIDモード / デバイスインスタンスIDモードの、それぞれの結果

| モード | DeviceId | 同型番2台 | ポートを変えると | 衝突 |
|---|---|---|---|---|
| 0（既定） | InstanceKey | 別の値 | 変わる | 16bitなので、まれに別の1台と一致する |
| 1 | ModelKey | **同じ値**（区別できない） | 変わらない | 同上 |

IDを1台に確定できる保証はない（16bitのハッシュ）。他のアプリは、IDが一致しても「同じキーボードとは限らない」前提で作ること。
kasane 自身も、`DeviceId` は一意でない前提で設計されている（`kbdlayer_ioctl.h` の E-2 のコメント）。

## 5. フォールバック

**通常は起きない。** 次のどれかのときだけ、`DeviceId` が本線で決まらず、フォールバックの列に進む。

| 条件 | 詳細 |
|---|---|
| A. モード0で InstanceKey が 0 | DeviceInstanceId が取得できなかった（取得失敗、文字列型でない、NUL終端が無い、空）、またはハッシュがたまたま `0x0000`（確率 1/65536） |
| B. モード1で ModelKey が 0 | HardwareId が取得できなかった（またはハッシュが `0x0000`） |
| C. `DeviceIdMode` が 0 でも 1 でもない | 例えば 2。どちらの分岐にも入らない |

**モード0で InstanceKey が取れないとき、モード1（ModelKey）には切り替わらない**（逆も同じ）。いきなり次のフォールバック列に進む。

フォールバックは次の順に試し、最初に成功したものを `DeviceId` とする。
いずれも結果は「正規化した文字列のハッシュ」で、結果の形は同じ（上位16bit）。

### 5.1 正規化（フォールバックでだけ使う）

HardwareId や CompatibleId の文字列に対して、次を行う。

1. 先頭の1件（NULまで）だけを使う。
2. 次の文字だけを残し、他は捨てる: `0-9` `A-Z` `a-z` `\` `_` `-` `.` `{` `}` `&` `:`
   （`*` や `#`、空白などは消える。たとえば `*LEN0071` は `LEN0071` になる）
3. すべて大文字にする。

結果が512文字に収まらないときは、大文字化されない切り詰めになる（実在のIDでは起きない）。

### 5.2 フォールバックの列

| 順 | 名前 | 入力文字列 | 成功条件 |
|---|---|---|---|
| 1 | `TryContainerId` | `正規化(HardwareId先頭)` + `_` + `{ContainerId}` | ContainerId（GUID型）と HardwareId の両方が取れる |
| 2 | `TryIoGetDeviceProperty`（= `TryQueryId`） | `IRP_MN_QUERY_ID(BusQueryInstanceID)` の戻り値。**インスタンス部分だけ**（`d&70295df&0&0000`）。加工しない | バスドライバが成功を返す |
| 3 | `TryCompatibleIds` | `正規化(CompatibleIds)` の、正規化後に空でない最初の1件 | CompatibleIds が取れ、空でない |
| 4 | `TryHardwareIdWithUniq` | `正規化(HardwareId先頭)` + `_` + `uniqNum`（10進、先頭0なし） | HardwareId が取れる |
| 5 | `UseFixedValue` | （文字列なし）`DeviceId = 0xFFF20000 \| (uniqNum & 0xFFFF)` | 常に成功 |

詳細:

- **1 ContainerId**: GUID は `{%08X-%04X-%04X-%02X%02X-%02X%02X%02X%02X%02X%02X}`（16進は大文字）の形。例: `HID\VID_04FE&PID_0016&REV_0224&MI_00_{F3422685-9A9A-11F1-A5C8-A0510B9EB6FE}`
  （正規化で `HID\VID_04FE&PID_0016&REV_0224&MI_00` になり、`_` と GUID が続く）。
  内蔵キーボードの ContainerId は、全機器共通の `{00000000-0000-0000-FFFF-FFFFFFFFFFFF}` になるので、
  内蔵キーボードが複数ある（通常はない）と、同じ値になる。
  ContainerId は物理デバイス（箱）に対して1つなので、USB の1つの箱に複数のキーボードインターフェースがあっても、HardwareId が異なれば別の値になる。
- **2 QUERY_ID**: 本線の InstanceKey とは**別の値**になる（本線は全体のパス、これはインスタンス部分だけ）。
  バスドライバが返した文字列の大文字小文字のままハッシュするので、実際の文字列は、ドライバの出力（デバッグログ `fallback: TryQueryId succeeded`）で確認すること。
  今回の実機では、この経路は通っていないので未検証。
- **4 uniqNum**: ドライバの読み込み以来の `AddDevice` の呼び出し回数（1から。IoCreateDevice に失敗した呼び出しも数える）。
  **再起動やデバイスの列挙順で変わる**ので、再起動をまたいで安定しない。ライセンス無効のデバイスも数える。
- **5 固定値**: `0xFFF2` を上位16bitに置くので、`ExtraInformation` に載る値（`DeviceId & 0xFFFF0000`）は、**全台が `0xFFF20000` で同じ**になる。
  下位16bit（uniqNum）は IOCTL の `UniqId` には残るが、キー入力には載らない。

実際には、1の ContainerId か 2 の QUERY_ID のどちらかが成功することが多く、4と5まで進むのは、デバイスノードの情報がほとんど取れないときだけ。

## 6. ID が付かない場合

- **ライセンスが無効**（`LicenseToken` が無い・署名が合わない）なら、`DeviceId = 0`。ID の計算自体を行わない
  （上記の InstanceKey / ModelKey も 0）。キー入力は素通しで、`ExtraInformation` の上位16bitは書き込まれない。
- `DeviceId = 0` は「未識別」を表す特別値。他のアプリも、上位16bitが0なら「kasane が識別していない入力」として扱うこと。

## 7. 他のアプリでの使い方

### 7.1 キー入力から読む

kbdlayer は、ドライバを通ったキーイベントの `ExtraInformation` を次のように更新する。

```
ExtraInformation = (元のExtraInformation & 0x0000FFFF) | (DeviceId & 0xFFFF0000)
```

- 下位16bitは元の値（他のフィルタやアプリ用）をそのまま残す。
- Raw Input の `RAWKEYBOARD.ExtraInformation`、低レベルフックの `KBDLLHOOKSTRUCT.dwExtraInfo` に載る。
  `ExtraInformation >> 16` が16bitのハッシュ。
- kasane が合成して出すキー（マクロなど）にも、同じ `DeviceId` が載る。

### 7.2 算出して照合する

各キーボードについて、次の手順でIDを求められる。

1. キーボードのデバイスノードを列挙する（`GUID_DEVINTERFACE_KEYBOARD` ではなく、クラス `Keyboard` のデバイスノード）。
2. **モード0**: レジストリ `Enum` のキー名から `<バス>\<ハードウェアID>\<インスタンス>` を組み立て、ハッシュ（第2節）。
3. **モード1**: そのデバイスノードの `DEVPKEY_Device_HardwareIds` の先頭の1件をハッシュする。
4. 上位16bitを、キー入力の `ExtraInformation >> 16` と比べる。

### 7.3 実機での確認

`DeviceIdMode=0`、同型番のUSBキーボード2台を含む5台で、`KeymapTool --list` の値と算出値が一致した。

| 機器 | 入力文字列 | InstanceKey（算出 = 実機） | ModelKey（算出 = 実機） |
|---|---|---|---|
| 内蔵（ACPI） | `ACPI\LEN0071\4&e940ebc&0` / `ACPI\VEN_LEN&DEV_0071` | `55F70000` | `F1DF0000` |
| USB（MI_00あり） | `HID\VID_04FE&PID_0016&MI_00\d&70295df&0&0000` / `HID\VID_04FE&PID_0016&REV_0224&MI_00` | `CDEC0000` | `0B450000` |
| USB（同型番1台目） | `HID\VID_13BA&PID_0001\c&3a55714f&0&0000` / `HID\VID_13BA&PID_0001&REV_0210` | `391B0000` | `ECBF0000` |
| USB（同型番2台目） | `HID\VID_13BA&PID_0001\c&117457ed&0&0000` / 同上 | `2BE50000` | `ECBF0000` |
| USB（MI_00あり） | `HID\VID_276D&PID_FFE2&MI_00\d&6696b51&0&0000` / `HID\VID_276D&PID_FFE2&REV_0100&MI_00` | `C6DE0000` | `C5580000` |

フォールバックの経路は、実機では通っていない（本線で全台が決まった）ため、第5節の記述はコードの読み取りによるもので、未検証。

### 7.4 参照実装（C#）

```csharp
static uint Fnv1aHash16Upper(string s)
{
    ushort h = 0x9DC5;
    foreach (char ch in s)                         // C# の char は UTF-16 のユニット
    {
        h = (ushort)((h ^ (byte)(ch & 0xFF)) * 0x0193);
        h = (ushort)((h ^ (byte)(ch >> 8)) * 0x0193);
    }
    return (uint)h << 16;
}
```
