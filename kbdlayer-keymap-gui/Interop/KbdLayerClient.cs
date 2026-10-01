using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using KbdLayerKeymapGui.Keycodes;

namespace KbdLayerKeymapGui.Interop;

public sealed record DeviceInfo(uint UniqId, bool HasLayerMap, bool Enabled, uint ActiveLayerMask, string HardwareId)
{
    public string DisplayName => $"{UniqId:X8}  {HardwareId}";
}

public sealed record MacroStep(ushort ScanCode, bool IsBreak);

public sealed record ComboEntry(ushort ScanCode1, ushort ScanCode2, KeyAction Output);
/// <summary>KBDLAYER_KEY_OVERRIDE_ENTRY。TriggerModsはKBDLAYER_MOD_*のOR、TriggerScanCodeは出力キー（Enc16）。</summary>
public sealed record KeyOverrideEntry(byte TriggerMods, ushort TriggerScanCode, KeyAction Replacement);

public sealed record TapDanceEntry(KeyAction SingleTap, KeyAction SingleHold, KeyAction DoubleTap, KeyAction TripleTap);

public sealed record DriverConfig(uint TapTermMs, uint ComboTermMs, uint TapDanceTermMs,
    uint TapHoldMode, uint QuickTapTermMs, uint TappingToggle, bool HasTapHoldOptions);

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
        : base($"{operation} に失敗しました: {new Win32Exception(win32Error).Message} (エラー {win32Error})")
    {
        Win32Error = win32Error;
    }
}

/// <summary>
/// \\.\KbdLayerControl（driver/common/kbdlayer_ioctl.h）へのIOCTLクライアント。
/// driver/KeymapTool/IoctlClient.cpp の移植。構造体はすべて #pragma pack(1) なので、
/// BinaryWriter/BinaryReader で同じ順序・同じ幅のまま手で直列化する。
/// </summary>
public sealed class KbdLayerClient : IDisposable
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
    private static readonly uint IOCTL_SET_KEY_OVERRIDE = CtlCode(0x81A, FILE_WRITE_ACCESS);
    private static readonly uint IOCTL_GET_KEY_OVERRIDE = CtlCode(0x81B, FILE_READ_ACCESS);
    public const int MaxKeyOverrideCount = 32;  // KBDLAYER_MAX_KEY_OVERRIDE_COUNT

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
        var data = Ioctl("デバイス一覧の取得", IOCTL_LIST_DEVICES, null, 4 + 64 * DeviceInfoSize);
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
        var data = Ioctl("レイヤーマップの取得", IOCTL_GET_LAYERMAP, BitConverter.GetBytes(uniqId), 4 + MaxMapBlobBytes);
        uint len = BitConverter.ToUInt32(data, 0);
        return data[4..(4 + (int)len)];
    }

    public void SetLayerMap(uint uniqId, byte[] blob) =>
        Ioctl("レイヤーマップの書き込み", IOCTL_SET_LAYERMAP, Build(w =>
        {
            w.Write(uniqId);
            w.Write((uint)blob.Length);
            w.Write(blob);
        }), 0);

    public void ClearLayerMap(uint uniqId) =>
        Ioctl("レイヤーマップの消去", IOCTL_CLEAR_LAYERMAP, BitConverter.GetBytes(uniqId), 0);

    public void SetEnabled(uint uniqId, bool enabled) =>
        Ioctl(enabled ? "有効化(ON)" : "無効化(OFF)", IOCTL_SET_LAYERMAP_ENABLED, Build(w =>
        {
            w.Write(uniqId);
            w.Write(enabled ? 1u : 0u);
        }), 0);

    /// <summary>次の物理キー押下1回分のスキャンコード（Enc16）。timeoutMs以内に無ければ null。</summary>
    public ushort? CaptureNextKey(uint uniqId, uint timeoutMs)
    {
        var data = Ioctl("キーの取得", IOCTL_CAPTURE_NEXT_KEY, Build(w =>
        {
            w.Write(uniqId);
            w.Write(timeoutMs);
        }), 4);
        ushort sc = BitConverter.ToUInt16(data, 0);
        bool timedOut = BitConverter.ToUInt16(data, 2) != 0;
        return timedOut ? null : sc;
    }

    /// <summary>
    /// SET_CONFIG v2（判定方式・Quick Tap・TTの回数を含む）で送る。古いドライバは、長い入力の先頭の
    /// 3項目（TapTerm/ComboTerm/TapDanceTerm）だけを使い、残りは無視する。
    /// </summary>
    public void SetConfig(uint uniqId, uint tapTermMs, uint comboTermMs, uint tapDanceTermMs,
                          uint tapHoldMode = TapHoldModes.HoldOnOtherKey, uint quickTapTermMs = 0, uint tappingToggle = 0) =>
        Ioctl("タイミング設定の書き込み", IOCTL_SET_CONFIG, Build(w =>
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
        var data = Ioctl("タイミング設定の取得", IOCTL_GET_CONFIG, BitConverter.GetBytes(uniqId), 24);
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
        Ioctl($"マクロ{macroId}の書き込み", IOCTL_SET_MACRO, Build(w =>
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
        var data = Ioctl($"マクロ{macroId}の取得", IOCTL_GET_MACRO, Build(w =>
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
        Ioctl($"コンボ{comboId}の書き込み", IOCTL_SET_COMBO, Build(w =>
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
        var data = Ioctl($"コンボ{comboId}の取得", IOCTL_GET_COMBO, Build(w =>
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
        Ioctl($"キー置換{id}の書き込み", IOCTL_SET_KEY_OVERRIDE, Build(w =>
        {
            w.Write(uniqId);
            w.Write((uint)id);
            w.Write((byte)(entry?.TriggerMods ?? 0));
            w.Write((byte)0); // Flags（予約）
            w.Write((ushort)(entry?.TriggerScanCode ?? 0));
            WriteAction(w, entry?.Replacement ?? KeyAction.None);
        }), 0);

    public KeyOverrideEntry? GetKeyOverride(uint uniqId, int id)
    {
        var data = Ioctl($"キー置換{id}の取得", IOCTL_GET_KEY_OVERRIDE, Build(w =>
        {
            w.Write(uniqId);
            w.Write((uint)id);
        }), 4 + 8);
        using var r = new BinaryReader(new MemoryStream(data));
        bool used = r.ReadUInt32() != 0;
        byte mods = r.ReadByte();
        r.ReadByte(); // Flags
        var e = new KeyOverrideEntry(mods, r.ReadUInt16(), ReadAction(r));
        return used ? e : null;
    }

    /// <summary>entry=null はそのタップダンスIDのクリア（SingleTap.Type=NONEがクリアの合図）。</summary>
    public void SetTapDance(uint uniqId, int tapDanceId, TapDanceEntry? entry) =>
        Ioctl($"タップダンス{tapDanceId}の書き込み", IOCTL_SET_TAPDANCE, Build(w =>
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
        var data = Ioctl($"タップダンス{tapDanceId}の取得", IOCTL_GET_TAPDANCE, Build(w =>
        {
            w.Write(uniqId);
            w.Write((uint)tapDanceId);
        }), 4 + 16);
        using var r = new BinaryReader(new MemoryStream(data));
        bool used = r.ReadUInt32() != 0;
        var e = new TapDanceEntry(ReadAction(r), ReadAction(r), ReadAction(r), ReadAction(r));
        return used ? e : null;
    }

    /// <summary>
    /// ドライバ側の入力抑止（CLIの --suppress と同じ）。GUIのキャリブレーションはLLフックで
    /// 入力を捨てるためこれを使わないが、同じIOCTLセットを揃えておく。
    /// </summary>
    public void SetCaptureSuppress(uint uniqId, bool enable) =>
        Ioctl("入力抑止の切り替え", IOCTL_SET_CAPTURE_SUPPRESS, Build(w =>
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
