using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Kasane.Keycodes;
using Loc = Kasane.Model.Loc;

namespace Kasane.Interop;

public sealed record DeviceInfo(uint UniqId, bool HasLayerMap, bool Enabled, uint ActiveLayerMask, string HardwareId)
{
    /// <summary>
    /// 画面に出す名前（利用者がつけた名前や Windows 上の名前を含む。<see cref="Kasane.Model.KeyboardNameRules.DisplayName"/>）。
    /// 設定画面が一覧を作るときに付ける。無ければ「UniqId  HardwareId」。UniqId が先頭であることを前提にしている画面がある。
    /// </summary>
    public string? Label { get; init; }

    public string DisplayName => Label ?? $"{UniqId:X8}  {HardwareId}";
}

public sealed record MacroStep(ushort ScanCode, bool IsBreak);

public sealed record ComboEntry(ushort ScanCode1, ushort ScanCode2, KeyAction Output);
/// <summary>KBDLAYER_KEY_OVERRIDE_ENTRY。TriggerModsはKBDLAYER_MOD_*のOR、TriggerScanCodeは出力キー（Enc16）。</summary>
public sealed record KeyOverrideEntry(byte TriggerMods, ushort TriggerScanCode, KeyAction Replacement, byte Flags = 0)
{
    /// <summary>KBDLAYER_KEY_OVERRIDE_FLAG_REPEAT。置き換え先が MACRO のとき、オートリピートでマクロを繰り返す。</summary>
    public const byte FlagRepeat = 0x01;
}

public sealed record TapDanceEntry(KeyAction SingleTap, KeyAction SingleHold, KeyAction DoubleTap, KeyAction TripleTap);

public sealed record DriverConfig(uint TapTermMs, uint ComboTermMs, uint TapDanceTermMs,
    uint TapHoldMode, uint QuickTapTermMs, uint TappingToggle, bool HasTapHoldOptions);

/// <summary>KBDLAYER_DEVICE_SCOPE。Binding は KbdLayerClient.BindingModel / BindingInstance / BindingGlobal。</summary>
public sealed record DeviceScope(uint InstanceKey, uint ModelKey, uint Binding);

/// <summary>KBDLAYER_LAYER_STATE。AppLayerMaskは、アプリ（IME連動など）が立てたレイヤー。</summary>
public sealed record LayerState(uint Seq, uint ActiveLayerMask, uint AppLayerMask, uint DefaultLayer,
    uint EffectiveLayerMask, bool LayerMapEnabled, bool NicolaActive);

/// <summary>型番（ModelKey）ごとの i8042prt\Parameters の値。null の値は「書かない（削除する）」。</summary>
public sealed record I8042Profile(uint ModelKey, uint? Type, uint? Subtype,
    string? Identifier, string? LayerJpn, string? LayerKor);

public sealed record I8042Config(bool Enabled, IReadOnlyList<I8042Profile> Profiles);

/// <summary>KBDLAYER_TAPHOLD_MODE_*（MT/LTのタップ／ホールドの判定方式。タップダンスは対象外）。</summary>
public static class TapHoldModes
{
    public const uint HoldOnOtherKey = 0; // 別のキーを押したらすぐホールド（既定）
    public const uint PermissiveHold = 1; // 別のキーを押して離したらホールド
    public const uint TapPreferred = 2;   // 時間内に離せばタップ（時間切れでホールド）
}

public sealed class KbdLayerException : Exception
{
    public int Win32Error { get; }
    public KbdLayerException(string operation, int win32Error)
        : base(Loc.T($"{operation} に失敗しました: {new Win32Exception(win32Error).Message} (エラー {win32Error})", $"{operation} failed: {new Win32Exception(win32Error).Message} (error {win32Error})"))
    {
        Win32Error = win32Error;
    }
}

/// <summary>
/// \\.\KbdLayerControl（driver/common/kbdlayer_ioctl.h）へのIOCTLクライアント。
/// driver/KeymapTool/IoctlClient.cpp の移植。構造体はすべて #pragma pack(1) なので、
/// BinaryWriter/BinaryReader で同じ順序・同じ幅のまま手で直列化する。
/// </summary>
public sealed partial class KbdLayerClient : IDisposable
{
    public const string ControlDevicePath = @"\\.\KbdLayerControl";

    private const uint FILE_DEVICE_KBDLAYER = 0x00008020;
    private const uint FILE_READ_ACCESS = 0x0001;
    private const uint FILE_WRITE_ACCESS = 0x0002;
    // CTL_CODE(FILE_DEVICE_KBDLAYER, function, METHOD_BUFFERED, access)。
    // アクセスビットの割り当ては kbdlayer_ioctl.h と一致させること（読み出しとON/OFFはREAD、
    // 設定の書き換えとキャプチャはWRITE。WRITEは管理者として実行したときだけ使える）。
    private static uint CtlCode(uint function, uint access) =>
        (FILE_DEVICE_KBDLAYER << 16) | (access << 14) | (function << 2);

    private static readonly uint IOCTL_LIST_DEVICES = CtlCode(0x800, FILE_READ_ACCESS);
    private static readonly uint IOCTL_GET_LAYERMAP = CtlCode(0x801, FILE_READ_ACCESS);
    private static readonly uint IOCTL_SET_LAYERMAP = CtlCode(0x802, FILE_WRITE_ACCESS);
    private static readonly uint IOCTL_CLEAR_LAYERMAP = CtlCode(0x803, FILE_WRITE_ACCESS);
    private static readonly uint IOCTL_SET_LAYERMAP_ENABLED = CtlCode(0x804, FILE_READ_ACCESS);
    private static readonly uint IOCTL_CAPTURE_NEXT_KEY = CtlCode(0x805, FILE_WRITE_ACCESS);
    private static readonly uint IOCTL_SET_CONFIG = CtlCode(0x806, FILE_WRITE_ACCESS);
    private static readonly uint IOCTL_SET_MACRO = CtlCode(0x807, FILE_WRITE_ACCESS);
    private static readonly uint IOCTL_GET_MACRO = CtlCode(0x808, FILE_READ_ACCESS);
    private static readonly uint IOCTL_SET_COMBO = CtlCode(0x809, FILE_WRITE_ACCESS);
    private static readonly uint IOCTL_GET_COMBO = CtlCode(0x80A, FILE_READ_ACCESS);
    private static readonly uint IOCTL_SET_TAPDANCE = CtlCode(0x80B, FILE_WRITE_ACCESS);
    private static readonly uint IOCTL_GET_TAPDANCE = CtlCode(0x80C, FILE_READ_ACCESS);
    private static readonly uint IOCTL_SET_CAPTURE_SUPPRESS = CtlCode(0x80D, FILE_WRITE_ACCESS);
    private static readonly uint IOCTL_GET_CONFIG = CtlCode(0x80E, FILE_READ_ACCESS);
    private static readonly uint IOCTL_SET_NICOLA = CtlCode(0x80F, FILE_WRITE_ACCESS);
    private static readonly uint IOCTL_GET_NICOLA = CtlCode(0x810, FILE_READ_ACCESS);
    private static readonly uint IOCTL_GET_DEVICE_SCOPE = CtlCode(0x814, FILE_READ_ACCESS);
    private static readonly uint IOCTL_SET_DEVICE_SCOPE = CtlCode(0x815, FILE_WRITE_ACCESS);
    private static readonly uint IOCTL_GET_EMERGENCY_RELEASE = CtlCode(0x816, FILE_READ_ACCESS);
    private static readonly uint IOCTL_SET_EMERGENCY_RELEASE = CtlCode(0x817, FILE_WRITE_ACCESS);
    private static readonly uint IOCTL_GET_FEATURES = CtlCode(0x818, FILE_READ_ACCESS);
    private static readonly uint IOCTL_SET_FEATURES = CtlCode(0x819, FILE_WRITE_ACCESS);
    private static readonly uint IOCTL_GET_LAYER_STATE = CtlCode(0x811, FILE_READ_ACCESS);
    private static readonly uint IOCTL_SET_LAYER_STATE = CtlCode(0x813, FILE_READ_ACCESS);
    private static readonly uint IOCTL_SET_KEY_OVERRIDE = CtlCode(0x81A, FILE_WRITE_ACCESS);
    private static readonly uint IOCTL_GET_KEY_OVERRIDE = CtlCode(0x81B, FILE_READ_ACCESS);
    private static readonly uint IOCTL_GET_I8042_CONFIG = CtlCode(0x81C, FILE_READ_ACCESS);
    private static readonly uint IOCTL_SET_I8042_CONFIG = CtlCode(0x81D, FILE_WRITE_ACCESS);
    public const int MaxKeyOverrideCount = 128; // KBDLAYER_MAX_KEY_OVERRIDE_COUNT（ドライバの値と必ず一致させる。旧ドライバは32）

    public const int MaxLayers = 16;            // KBDLAYER_MAX_LAYERS（ドライバの値と必ず一致させる）
    public const int MaxKeys = 256;             // KBDLAYER_MAX_KEYS
    public const int MaxMacroCount = 128;       // KBDLAYER_MAX_MACRO_COUNT（ドライバの値と必ず一致させる）
    public const int MaxMacroSteps = 64;        // KBDLAYER_MAX_MACRO_STEPS
    public const int MaxComboCount = 16;        // KBDLAYER_MAX_COMBO_COUNT
    public const int MaxTapDanceCount = 16;     // KBDLAYER_MAX_TAPDANCE_COUNT

    private const int DeviceInfoSize = 4 * 4 + 256 * 2;          // KBDLAYER_DEVICE_INFO
    private const int KeyEntrySize = 2 + MaxLayers * 4;           // KBDLAYER_KEY_ENTRY
    private const int MapHeaderSize = 8;                          // KBDLAYER_MAP_HEADER（Header + KeyCount。LayerCountは廃止）
    private const int MaxMapBlobBytes = MapHeaderSize + MaxKeys * KeyEntrySize;

    private readonly SafeFileHandle _handle;

    /// <summary>
    /// 読み取り専用で開いている（管理者として実行していない）。一覧・読み出し・ON/OFFは使えるが、
    /// 設定の書き換え（適用）とキャプチャ（キーボードの特定・キャリブレーション）はドライバに拒否される。
    /// </summary>
    public bool IsReadOnly { get; }

    private KbdLayerClient(SafeFileHandle handle, bool readOnly)
    {
        _handle = handle;
        IsReadOnly = readOnly;
    }

    /// <summary>
    /// コントロールデバイスを開く。ドライバが無い場合は null を返し、理由を error に入れる。
    /// 制御デバイスはEveryoneに読み取りしか許していないので、昇格していなければ読み取り専用で開き直す。
    /// </summary>
    public static KbdLayerClient? TryOpen(out string? error)
    {
        bool readOnly = false;
        var h = NativeMethods.CreateFileW(ControlDevicePath,
            NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE, 0, IntPtr.Zero,
            NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid && Marshal.GetLastWin32Error() == NativeMethods.ERROR_ACCESS_DENIED)
        {
            h.Dispose();
            h = NativeMethods.CreateFileW(ControlDevicePath,
                NativeMethods.GENERIC_READ, 0, IntPtr.Zero,
                NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);
            readOnly = true;
        }
        if (h.IsInvalid)
        {
            error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            h.Dispose();
            return null;
        }
        error = null;
        return new KbdLayerClient(h, readOnly);
    }

    public void Dispose() => _handle.Dispose();

    private byte[] Ioctl(string operation, uint code, byte[]? input, int outputSize)
    {
        var output = outputSize > 0 ? new byte[outputSize] : null;
        if (!NativeMethods.DeviceIoControl(_handle, code, input, (uint)(input?.Length ?? 0),
                output, (uint)outputSize, out uint returned, IntPtr.Zero))
        {
            throw new KbdLayerException(operation, Marshal.GetLastWin32Error());
        }
        if (output == null) return Array.Empty<byte>();
        return returned < output.Length ? output[..(int)returned] : output;
    }

    private static byte[] Build(Action<BinaryWriter> write)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms)) write(w);
        return ms.ToArray();
    }

    private static void WriteAction(BinaryWriter w, KeyAction a)
    {
        w.Write(a.Type);
        w.Write(a.Param1);
        w.Write(a.Param2);
    }

    private static KeyAction ReadAction(BinaryReader r) => new(r.ReadByte(), r.ReadByte(), r.ReadUInt16());

    public List<DeviceInfo> ListDevices()
    {
        var data = Ioctl(Loc.T("デバイス一覧の取得", "Getting the device list"), IOCTL_LIST_DEVICES, null, 4 + 64 * DeviceInfoSize);
        using var r = new BinaryReader(new MemoryStream(data));
        uint count = r.ReadUInt32();
        var list = new List<DeviceInfo>();
        for (int i = 0; i < count; i++)
        {
            uint id = r.ReadUInt32();
            bool has = r.ReadUInt32() != 0;
            bool enabled = r.ReadUInt32() != 0;
            uint mask = r.ReadUInt32();
            var hw = Encoding.Unicode.GetString(r.ReadBytes(256 * 2));
            int nul = hw.IndexOf('\0');
            list.Add(new DeviceInfo(id, has, enabled, mask, nul >= 0 ? hw[..nul] : hw));
        }
        return list;
    }

    /// <summary>レイヤーマップのblob（KBDLAYER_MAP_HEADER + entries）。未設定なら空配列。</summary>
    public byte[] GetLayerMap(uint uniqId)
    {
        var data = Ioctl(Loc.T("レイヤーマップの取得", "Getting the layer map"), IOCTL_GET_LAYERMAP, BitConverter.GetBytes(uniqId), 4 + MaxMapBlobBytes);
        uint len = BitConverter.ToUInt32(data, 0);
        return data[4..(4 + (int)len)];
    }

    public void SetLayerMap(uint uniqId, byte[] blob) =>
        Ioctl(Loc.T("レイヤーマップの書き込み", "Writing the layer map"), IOCTL_SET_LAYERMAP, Build(w =>
        {
            w.Write(uniqId);
            w.Write((uint)blob.Length);
            w.Write(blob);
        }), 0);

    public void ClearLayerMap(uint uniqId) =>
        Ioctl(Loc.T("レイヤーマップの消去", "Clearing the layer map"), IOCTL_CLEAR_LAYERMAP, BitConverter.GetBytes(uniqId), 0);

    public void SetEnabled(uint uniqId, bool enabled) =>
        Ioctl(enabled ? Loc.T("有効化(ON)", "Enabling (ON)") : Loc.T("無効化(OFF)", "Disabling (OFF)"), IOCTL_SET_LAYERMAP_ENABLED, Build(w =>
        {
            w.Write(uniqId);
            w.Write(enabled ? 1u : 0u);
        }), 0);

    /// <summary>CAPTURE_NEXT_KEY の Flags: 疑似Shift（E0 2A / E0 36）も捕捉する。</summary>
    public const uint CaptureFlagAllowFakeShift = 1;

    /// <summary>
    /// 次の物理キー押下1回分のスキャンコード（Enc16）。timeoutMs以内に無ければ null。
    /// allowFakeShift: 右ShiftをE0 36で出すキーボードを実測するとき、Shiftのスロットだけで true にする
    /// （PrintScreenで指定すると、前置きのE0 2Aを捕まえる）。false のときは従来どおり8バイトの入力を送る（旧ドライバとも互換）。
    /// </summary>
    public ushort? CaptureNextKey(uint uniqId, uint timeoutMs, bool allowFakeShift = false)
    {
        var data = Ioctl(Loc.T("キーの取得", "Capturing a key"), IOCTL_CAPTURE_NEXT_KEY, BuildCaptureRequest(uniqId, timeoutMs, allowFakeShift), 4);
        ushort sc = BitConverter.ToUInt16(data, 0);
        bool timedOut = BitConverter.ToUInt16(data, 2) != 0;
        return timedOut ? null : sc;
    }

    /// <summary>CAPTURE_NEXT_KEY の入力。フラグが無いときは旧形式（8バイト）のままにして、旧ドライバでも動かす。</summary>
    public static byte[] BuildCaptureRequest(uint uniqId, uint timeoutMs, bool allowFakeShift) =>
        Build(w =>
        {
            w.Write(uniqId);
            w.Write(timeoutMs);
            if (allowFakeShift) w.Write(CaptureFlagAllowFakeShift);
        });

    /// <summary>
    /// SET_CONFIG v2（判定方式・Quick Tap・TTの回数を含む）で送る。古いドライバは、長い入力の先頭の
    /// 3項目（TapTerm/ComboTerm/TapDanceTerm）だけを使い、残りは無視する。
    /// </summary>
    public void SetConfig(uint uniqId, uint tapTermMs, uint comboTermMs, uint tapDanceTermMs,
                          uint tapHoldMode = TapHoldModes.HoldOnOtherKey, uint quickTapTermMs = 0, uint tappingToggle = 0) =>
        Ioctl(Loc.T("タイミング設定の書き込み", "Writing the timing settings"), IOCTL_SET_CONFIG, Build(w =>
        {
            w.Write(uniqId);
            w.Write(tapTermMs);
            w.Write(comboTermMs);
            w.Write(tapDanceTermMs);
            w.Write(tapHoldMode);
            w.Write(quickTapTermMs);
            w.Write(tappingToggle);
        }), 0);

    /// <summary>古いドライバは先頭の3項目だけを返す。そのときは追加した項目を既定値にする（HasTapHoldOptions=false）。</summary>
    public DriverConfig GetConfig(uint uniqId)
    {
        var data = Ioctl(Loc.T("タイミング設定の取得", "Getting the timing settings"), IOCTL_GET_CONFIG, BitConverter.GetBytes(uniqId), 24);
        uint U(int offset) => BitConverter.ToUInt32(data, offset);
        if (data.Length >= 24)
            return new DriverConfig(U(0), U(4), U(8), U(12), U(16), U(20), HasTapHoldOptions: true);
        return new DriverConfig(U(0), U(4), U(8), TapHoldModes.HoldOnOtherKey, 0, DefaultTappingToggle, HasTapHoldOptions: false);
    }

    public const uint DefaultTappingToggle = 5;          // KBDLAYER_DEFAULT_TAPPING_TOGGLE
    public const uint MaxTappingToggle = 32;             // KBDLAYER_MAX_TAPPING_TOGGLE
    public const uint QuickTapTermDisabled = 0xFFFFFFFF; // KBDLAYER_QUICK_TAP_TERM_DISABLED

    /// <summary>stepsが空ならそのマクロIDをクリアする。</summary>
    public void SetMacro(uint uniqId, int macroId, IReadOnlyList<MacroStep> steps) =>
        Ioctl(Loc.T($"マクロ{macroId}の書き込み", $"Writing macro {macroId}"), IOCTL_SET_MACRO, Build(w =>
        {
            w.Write(uniqId);
            w.Write((uint)macroId);
            w.Write((uint)steps.Count);
            foreach (var s in steps)
            {
                w.Write(s.ScanCode);
                w.Write((byte)(s.IsBreak ? 1 : 0));
                w.Write((byte)0);
            }
        }), 0);

    public List<MacroStep> GetMacro(uint uniqId, int macroId)
    {
        var data = Ioctl(Loc.T($"マクロ{macroId}の取得", $"Getting macro {macroId}"), IOCTL_GET_MACRO, Build(w =>
        {
            w.Write(uniqId);
            w.Write((uint)macroId);
        }), 4 + MaxMacroSteps * 4);
        using var r = new BinaryReader(new MemoryStream(data));
        uint count = r.ReadUInt32();
        var steps = new List<MacroStep>();
        for (int i = 0; i < count; i++)
        {
            ushort sc = r.ReadUInt16();
            bool isBreak = r.ReadByte() != 0;
            r.ReadByte();
            steps.Add(new MacroStep(sc, isBreak));
        }
        return steps;
    }

    /// <summary>entry=null はそのコンボIDのクリア（Output.Type=NONEがクリアの合図）。</summary>
    public void SetCombo(uint uniqId, int comboId, ComboEntry? entry) =>
        Ioctl(Loc.T($"コンボ{comboId}の書き込み", $"Writing combo {comboId}"), IOCTL_SET_COMBO, Build(w =>
        {
            w.Write(uniqId);
            w.Write((uint)comboId);
            // ushort?とintの??はintになり4バイト書かれてしまうので、必ずushortへ戻す。
            w.Write((ushort)(entry?.ScanCode1 ?? 0));
            w.Write((ushort)(entry?.ScanCode2 ?? 0));
            WriteAction(w, entry?.Output ?? KeyAction.None);
        }), 0);

    public ComboEntry? GetCombo(uint uniqId, int comboId)
    {
        var data = Ioctl(Loc.T($"コンボ{comboId}の取得", $"Getting combo {comboId}"), IOCTL_GET_COMBO, Build(w =>
        {
            w.Write(uniqId);
            w.Write((uint)comboId);
        }), 4 + 8);
        using var r = new BinaryReader(new MemoryStream(data));
        bool used = r.ReadUInt32() != 0;
        var e = new ComboEntry(r.ReadUInt16(), r.ReadUInt16(), ReadAction(r));
        return used ? e : null;
    }

    /// <summary>entry=null はそのIDのクリア（Replacement.Type=NONEがクリアの合図）。</summary>
    public void SetKeyOverride(uint uniqId, int id, KeyOverrideEntry? entry) =>
        Ioctl(Loc.T($"キー置換{id}の書き込み", $"Writing key override {id}"), IOCTL_SET_KEY_OVERRIDE, Build(w =>
        {
            w.Write(uniqId);
            w.Write((uint)id);
            w.Write((byte)(entry?.TriggerMods ?? 0));
            w.Write((byte)(entry?.Flags ?? 0)); // Flags（KeyOverrideEntry.FlagRepeat）
            w.Write((ushort)(entry?.TriggerScanCode ?? 0));
            WriteAction(w, entry?.Replacement ?? KeyAction.None);
        }), 0);

    public KeyOverrideEntry? GetKeyOverride(uint uniqId, int id)
    {
        var data = Ioctl(Loc.T($"キー置換{id}の取得", $"Getting key override {id}"), IOCTL_GET_KEY_OVERRIDE, Build(w =>
        {
            w.Write(uniqId);
            w.Write((uint)id);
        }), 4 + 8);
        using var r = new BinaryReader(new MemoryStream(data));
        bool used = r.ReadUInt32() != 0;
        byte mods = r.ReadByte();
        byte flags = r.ReadByte();
        ushort triggerScanCode = r.ReadUInt16();
        var e = new KeyOverrideEntry(mods, triggerScanCode, ReadAction(r), flags);
        return used ? e : null;
    }

    /// <summary>entry=null はそのタップダンスIDのクリア（SingleTap.Type=NONEがクリアの合図）。</summary>
    public void SetTapDance(uint uniqId, int tapDanceId, TapDanceEntry? entry) =>
        Ioctl(Loc.T($"タップダンス{tapDanceId}の書き込み", $"Writing tap dance {tapDanceId}"), IOCTL_SET_TAPDANCE, Build(w =>
        {
            w.Write(uniqId);
            w.Write((uint)tapDanceId);
            WriteAction(w, entry?.SingleTap ?? KeyAction.None);
            WriteAction(w, entry?.SingleHold ?? KeyAction.None);
            WriteAction(w, entry?.DoubleTap ?? KeyAction.None);
            WriteAction(w, entry?.TripleTap ?? KeyAction.None);
        }), 0);

    public TapDanceEntry? GetTapDance(uint uniqId, int tapDanceId)
    {
        var data = Ioctl(Loc.T($"タップダンス{tapDanceId}の取得", $"Getting tap dance {tapDanceId}"), IOCTL_GET_TAPDANCE, Build(w =>
        {
            w.Write(uniqId);
            w.Write((uint)tapDanceId);
        }), 4 + 16);
        using var r = new BinaryReader(new MemoryStream(data));
        bool used = r.ReadUInt32() != 0;
        var e = new TapDanceEntry(ReadAction(r), ReadAction(r), ReadAction(r), ReadAction(r));
        return used ? e : null;
    }

    // --- レイヤー状態とアプリ管理レイヤー（IME連動） ---

    public const uint LayerOpOn = 1;      // KBDLAYER_LAYER_OP_ON（Layerのビットを立てる）
    public const uint LayerOpOff = 2;     // KBDLAYER_LAYER_OP_OFF
    public const uint LayerOpToggle = 3;  // KBDLAYER_LAYER_OP_TOGGLE
    public const uint LayerOpSetMask = 4; // KBDLAYER_LAYER_OP_SET_MASK
    /// <summary>この変更でNICOLAのON/OFFが切り替わっても、OnEnterKey/OnExitKeyを送らない（IME側の変化を起点にするとき）。</summary>
    public const uint LayerFlagNoNicolaKeys = 1; // KBDLAYER_LAYER_STATE_FLAG_NO_NICOLA_KEYS
    private const int LayerStateSize = 7 * 4;

    private static LayerState ParseLayerState(byte[] data)
    {
        uint U(int i) => BitConverter.ToUInt32(data, i * 4);
        return new LayerState(U(0), U(1), U(2), U(3), U(4), U(5) != 0, U(6) != 0);
    }

    public LayerState GetLayerState(uint uniqId) =>
        ParseLayerState(Ioctl(Loc.T("レイヤー状態の取得", "Getting the layer state"), IOCTL_GET_LAYER_STATE, BitConverter.GetBytes(uniqId), LayerStateSize));

    /// <summary>
    /// アプリ管理レイヤーの操作（読み取り側のIOCTLなので昇格は要らない）。最後に呼んだハンドルが所有者になり、
    /// そのハンドルを閉じる（プロセスの終了を含む）と、アプリ管理レイヤーは0に戻る。変更後の状態を返す。
    /// </summary>
    public LayerState SetLayerState(uint uniqId, uint op, uint layer, uint mask = 0, uint flags = 0)
    {
        var data = Ioctl(Loc.T("アプリ管理レイヤーの変更", "Changing the app-managed layer"), IOCTL_SET_LAYER_STATE, Build(w =>
        {
            w.Write(uniqId);
            w.Write(op);
            w.Write(layer);
            w.Write(mask);
            w.Write(flags);
        }), LayerStateSize);
        return data.Length >= LayerStateSize ? ParseLayerState(data) : GetLayerState(uniqId);
    }

    // --- 機能別ON/OFF（キーボードごと） ---

    public const uint FeatureMacro = 0x1;       // KBDLAYER_FEATURE_MACRO
    public const uint FeatureCombo = 0x2;       // KBDLAYER_FEATURE_COMBO
    public const uint FeatureTapDance = 0x4;    // KBDLAYER_FEATURE_TAPDANCE
    public const uint FeatureKeyOverride = 0x8; // KBDLAYER_FEATURE_KEYOVERRIDE
    public const uint FeatureAll = FeatureMacro | FeatureCombo | FeatureTapDance | FeatureKeyOverride;

    public uint GetFeatures(uint uniqId) =>
        BitConverter.ToUInt32(Ioctl(Loc.T("機能のON/OFFの取得", "Getting the feature ON/OFF state"), IOCTL_GET_FEATURES, BitConverter.GetBytes(uniqId), 4), 0);

    /// <summary>未知のビットを含むとドライバに拒否される（Key Override以前のドライバにFeatureKeyOverrideを送った場合も）。</summary>
    public void SetFeatures(uint uniqId, uint features) =>
        Ioctl(Loc.T("機能のON/OFFの書き込み", "Writing the feature ON/OFF state"), IOCTL_SET_FEATURES, Build(w =>
        {
            w.Write(uniqId);
            w.Write(features);
        }), 0);

    // --- 設定の共有範囲（E-2。キーボードごと） ---

    public const uint BindingModel = 0;    // KBDLAYER_BINDING_MODEL（同型番で共有。既定）
    public const uint BindingInstance = 1; // KBDLAYER_BINDING_INSTANCE（この1台だけ）
    public const uint BindingGlobal = 2;   // KBDLAYER_BINDING_GLOBAL（すべてのキーボードで共有）

    public DeviceScope GetDeviceScope(uint uniqId)
    {
        var data = Ioctl(Loc.T("共有範囲の取得", "Getting the sharing scope"), IOCTL_GET_DEVICE_SCOPE, BitConverter.GetBytes(uniqId), 12);
        return new DeviceScope(BitConverter.ToUInt32(data, 0), BitConverter.ToUInt32(data, 4), BitConverter.ToUInt32(data, 8));
    }

    /// <summary>Model→Instanceでは、ドライバが今の設定をこの1台用にコピーしてから切り替える。</summary>
    public DeviceScope SetDeviceScope(uint uniqId, uint binding)
    {
        var data = Ioctl(Loc.T("共有範囲の変更", "Changing the sharing scope"), IOCTL_SET_DEVICE_SCOPE, Build(w =>
        {
            w.Write(uniqId);
            w.Write(binding);
        }), 12);
        return data.Length >= 12
            ? new DeviceScope(BitConverter.ToUInt32(data, 0), BitConverter.ToUInt32(data, 4), BitConverter.ToUInt32(data, 8))
            : GetDeviceScope(uniqId);
    }

    // --- 緊急解除（ドライバ全体で1つ。KBDLAYER_TARGETを取らない） ---

    public const int EmergencyMinKeys = 2;      // KBDLAYER_EMERGENCY_MIN_KEYS
    public const int EmergencyMaxKeys = 6;      // KBDLAYER_EMERGENCY_MAX_KEYS
    public const int EmergencyHoldMs = 3000;    // KBDLAYER_EMERGENCY_HOLD_MS
    private const int EmergencyConfigSize = 4 + EmergencyMaxKeys * 2;

    /// <summary>物理スキャンコード（Enc16）の組み合わせ。</summary>
    public List<ushort> GetEmergencyRelease()
    {
        var data = Ioctl(Loc.T("緊急解除の組み合わせの取得", "Getting the emergency release combination"), IOCTL_GET_EMERGENCY_RELEASE, null, EmergencyConfigSize);
        int count = (int)Math.Min(BitConverter.ToUInt32(data, 0), (uint)EmergencyMaxKeys);
        var keys = new List<ushort>();
        for (int i = 0; i < count; i++) keys.Add(BitConverter.ToUInt16(data, 4 + i * 2));
        return keys;
    }

    /// <summary>2〜6個の異なるキー。条件を満たさなければドライバが拒否し、設定は変わらない。</summary>
    public void SetEmergencyRelease(IReadOnlyList<ushort> scanCodes) =>
        Ioctl(Loc.T("緊急解除の組み合わせの書き込み", "Writing the emergency release combination"), IOCTL_SET_EMERGENCY_RELEASE, Build(w =>
        {
            w.Write((uint)scanCodes.Count);
            for (int i = 0; i < EmergencyMaxKeys; i++) w.Write(i < scanCodes.Count ? scanCodes[i] : (ushort)0);
        }), 0);

    // --- i8042prt の書き換え設定（ドライバ全体で1つ。KBDLAYER_I8042_CONFIG） ---

    public const int I8042MaxProfiles = 16;     // KBDLAYER_I8042_MAX_PROFILES
    public const int I8042StrChars = 32;        // KBDLAYER_I8042_STR_CHARS（終端のNULを含む）
    private const int I8042ProfileSize = 16 + 3 * I8042StrChars * 2;
    private const int I8042ConfigSize = 8 + I8042MaxProfiles * I8042ProfileSize;
    private const uint I8042HasType = 1, I8042HasSubtype = 2, I8042HasIdentifier = 4, I8042HasLayerJpn = 8, I8042HasLayerKor = 16;

    public I8042Config GetI8042Config()
    {
        var data = Ioctl(Loc.T("i8042prt設定の取得", "Getting the i8042prt settings"), IOCTL_GET_I8042_CONFIG, null, I8042ConfigSize);
        bool enabled = BitConverter.ToUInt32(data, 0) != 0;
        int count = (int)Math.Min(BitConverter.ToUInt32(data, 4), (uint)I8042MaxProfiles);
        var list = new List<I8042Profile>();
        for (int i = 0; i < count; i++)
        {
            int o = 8 + i * I8042ProfileSize;
            uint flags = BitConverter.ToUInt32(data, o + 4);
            string? Str(uint bit, int index)
            {
                if ((flags & bit) == 0) return null;
                var chars = Encoding.Unicode.GetString(data, o + 16 + index * I8042StrChars * 2, I8042StrChars * 2);
                int nul = chars.IndexOf('\0');
                return nul >= 0 ? chars[..nul] : chars;
            }
            list.Add(new I8042Profile(BitConverter.ToUInt32(data, o),
                (flags & I8042HasType) != 0 ? BitConverter.ToUInt32(data, o + 8) : null,
                (flags & I8042HasSubtype) != 0 ? BitConverter.ToUInt32(data, o + 12) : null,
                Str(I8042HasIdentifier, 0), Str(I8042HasLayerJpn, 1), Str(I8042HasLayerKor, 2)));
        }
        return new I8042Config(enabled, list);
    }

    /// <summary>全体置換。不正な入力（文字・長さ・重複・個数）はドライバが拒否し、設定は変わらない。</summary>
    public void SetI8042Config(I8042Config config) =>
        Ioctl(Loc.T("i8042prt設定の書き込み", "Writing the i8042prt settings"), IOCTL_SET_I8042_CONFIG, SerializeI8042Config(config), 0);

    /// <summary>
    /// i8042prt の設定のバイト列（KBDLAYER_I8042_CONFIG。IOCTL の入力と、レジストリの Parameters\I8042Override の値は同じ形で、常にこのサイズ全体）。
    /// インストール時に、ドライバを介さずレジストリへ書くときにも使う（<see cref="PersistedSettings.WriteI8042Config"/>）。
    /// </summary>
    public static byte[] SerializeI8042Config(I8042Config config)
    {
        if (config.Profiles.Count > I8042MaxProfiles)
            throw new ArgumentException(Loc.T($"プロファイルは{I8042MaxProfiles}個までです。", $"A maximum of {I8042MaxProfiles} profiles is allowed."), nameof(config));
        return Build(w =>
        {
            w.Write(config.Enabled ? 1u : 0u);
            w.Write((uint)config.Profiles.Count);
            for (int i = 0; i < I8042MaxProfiles; i++)
            {
                var p = i < config.Profiles.Count ? config.Profiles[i] : null;
                uint flags = 0;
                if (p?.Type != null) flags |= I8042HasType;
                if (p?.Subtype != null) flags |= I8042HasSubtype;
                if (!string.IsNullOrEmpty(p?.Identifier)) flags |= I8042HasIdentifier;
                if (!string.IsNullOrEmpty(p?.LayerJpn)) flags |= I8042HasLayerJpn;
                if (!string.IsNullOrEmpty(p?.LayerKor)) flags |= I8042HasLayerKor;
                w.Write(p?.ModelKey ?? 0u);
                w.Write(flags);
                w.Write(p?.Type ?? 0u);
                w.Write(p?.Subtype ?? 0u);
                foreach (var str in new[] { p?.Identifier, p?.LayerJpn, p?.LayerKor })
                {
                    var buf = new byte[I8042StrChars * 2];
                    var text = str ?? "";
                    // 長すぎる値は切らずに拒否させる（ドライバ側で終端が無く不正になる長さにする）
                    var bytes = Encoding.Unicode.GetBytes(text);
                    Array.Copy(bytes, buf, Math.Min(bytes.Length, buf.Length));
                    w.Write(buf);
                }
            }
        });
    }

    // --- NICOLA（キーボードごと。blob = KBDLAYER_NICOLA_HEADER + KBDLAYER_NICOLA_KEY_ENTRY[]） ---

    public const int NicolaHeaderSize = 44;      // sizeof(KBDLAYER_NICOLA_HEADER)
    public const int NicolaKeyEntrySize = 160;   // sizeof(KBDLAYER_NICOLA_KEY_ENTRY)
    public const int NicolaMaxKeys = 64;         // KBDLAYER_NICOLA_MAX_KEYS
    public const int NicolaMaxSteps = 12;        // KBDLAYER_NICOLA_MAX_STEPS
    private const int MaxNicolaBlobBytes = NicolaHeaderSize + NicolaMaxKeys * NicolaKeyEntrySize;

    /// <summary>未設定なら空配列。</summary>
    public byte[] GetNicola(uint uniqId)
    {
        var data = Ioctl(Loc.T("NICOLAの設定の取得", "Getting the NICOLA settings"), IOCTL_GET_NICOLA, BitConverter.GetBytes(uniqId), 4 + MaxNicolaBlobBytes);
        uint len = BitConverter.ToUInt32(data, 0);
        return data[4..(4 + (int)len)];
    }

    /// <summary>blobが空なら設定の消去。</summary>
    public void SetNicola(uint uniqId, byte[] blob) =>
        Ioctl(blob.Length == 0 ? Loc.T("NICOLAの設定の消去", "Clearing the NICOLA settings") : Loc.T("NICOLAの設定の書き込み", "Writing the NICOLA settings"), IOCTL_SET_NICOLA, Build(w =>
        {
            w.Write(uniqId);
            w.Write((uint)blob.Length);
            w.Write(blob);
        }), 0);

    /// <summary>
    /// ドライバ側の入力抑止（CLIの --suppress と同じ）。GUIのキャリブレーションはLLフックで
    /// 入力を捨てるためこれを使わないが、同じIOCTLセットを揃えておく。
    /// </summary>
    public void SetCaptureSuppress(uint uniqId, bool enable) =>
        Ioctl(Loc.T("入力抑止の切り替え", "Switching input suppression"), IOCTL_SET_CAPTURE_SUPPRESS, Build(w =>
        {
            w.Write(uniqId);
            w.Write(enable ? 1u : 0u);
        }), 0);

    // --- レイヤーマップblob（KBDLAYER_MAP_HEADER + KBDLAYER_KEY_ENTRY[]）の解析 ---

    public sealed record LayerMapEntry(ushort SourceScanCode, KeyAction[] Actions);

    /// <summary>
    /// レイヤーマップのblobを読む。ドライバのレイヤー数は常に MaxLayers（LayerCountフィールドは
    /// 廃止）なので、戻り値の LayerCount は「KC_TRNS 以外が入っている最後のレイヤー + 1」
    /// （最低1）から推定する。未使用レイヤーは TRANSPARENT で埋めて送っているため、2層の
    /// キーマップを読み込んだときに16層ぶんのタブが出ないようにするため。
    /// </summary>
    public static (int LayerCount, List<LayerMapEntry> Entries) ParseLayerMapBlob(byte[] blob)
    {
        var entries = new List<LayerMapEntry>();
        if (blob.Length < MapHeaderSize) return (0, entries);
        using var r = new BinaryReader(new MemoryStream(blob));
        r.ReadUInt32(); // Header（予約）
        int keyCount = (int)r.ReadUInt32();
        int usedLayerCount = 1;
        for (int i = 0; i < keyCount; i++)
        {
            ushort sc = r.ReadUInt16();
            var actions = new KeyAction[MaxLayers];
            for (int L = 0; L < MaxLayers; L++)
            {
                actions[L] = ReadAction(r);
                if (actions[L].Type != ActionType.Transparent && L + 1 > usedLayerCount) usedLayerCount = L + 1;
            }
            entries.Add(new LayerMapEntry(sc, actions));
        }
        return (usedLayerCount, entries);
    }
}
