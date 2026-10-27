using System.IO;
using Kasane.Interop;
using Kasane.Keycodes;
using Microsoft.Win32;

namespace Kasane.Model;

/// <summary>
/// ドライバ（kbdlayer.sys）がレジストリに保存する設定（HKLM\SYSTEM\CurrentControlSet\Services\kbdlayer\Parameters\LayerMaps\&lt;名前&gt;）を、
/// ドライバを介さずに直接書く。インストール時（管理者権限はあるがドライバが動いていない）に、設定ファイルの内容をドライバへ反映するためのもの。
/// 書いた設定は、キーボードが接続されたとき（再接続・再起動）にドライバが読み込む。
///
/// 形式は driver/kbdlayer/LayerEngine.c の PersistAll* / Set*ForDevice と同じ（実機のレジストリのサイズで確認済み:
/// Macros 33280 / Combos 192 / TapDances 320 / KeyOverrides 384 バイト）。
/// 置き換える前の値は、1世代だけ「&lt;名前&gt;.bak」に退避する（ドライバは読まない名前）。復元は <see cref="Restore"/>。
/// </summary>
public sealed class PersistedSettings
{
    public const string DefaultLayerMapsPath = @"SYSTEM\CurrentControlSet\Services\kbdlayer\Parameters\LayerMaps";
    public const string BackupSuffix = ".bak";

    // ドライバの既定値（driver/kbdlayer/DeviceExtension.h）。SET_CONFIG は 0 をこの値に直して保存する。
    private const uint DefaultTapTermMs = 200;
    private const uint DefaultComboTermMs = 50;
    private const uint DefaultTapDanceTermMs = 200;
    private const uint FeatureAllV1 = KbdLayerClient.FeatureMacro | KbdLayerClient.FeatureCombo | KbdLayerClient.FeatureTapDance;

    private readonly RegistryKey _hive;
    private readonly string _layerMapsPath;

    /// <param name="hive">通常は HKLM（Registry64）。単体テストでは HKCU の下の作業用キーを使う。</param>
    /// <param name="layerMapsPath">hive からの LayerMaps キーのパス。</param>
    public PersistedSettings(RegistryKey hive, string layerMapsPath = DefaultLayerMapsPath)
    {
        _hive = hive;
        _layerMapsPath = layerMapsPath;
    }

    public static PersistedSettings OpenLocalMachine() =>
        new(RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64));

    // ------------------------------------------------------------------
    // 形式（純粋な変換。単体テストできる）
    // ------------------------------------------------------------------

    private const int MacroEntryBytes = 4 + KbdLayerClient.MaxMacroSteps * 4;
    private const int ComboEntryBytes = 4 + 8;
    private const int TapDanceEntryBytes = 4 + 16;
    private const int KeyOverrideEntryBytes = 4 + 8;

    private static void WriteAction(BinaryWriter w, KeyAction a)
    {
        w.Write(a.Type);
        w.Write(a.Param1);
        w.Write(a.Param2);
    }

    /// <summary>"Macros" 値: 128個ぶん固定長（StepCount + 64ステップ。ステップは ScanCode/IsBreak/予約）。</summary>
    public static byte[] SerializeMacros(IReadOnlyDictionary<int, List<MacroStep>> macros)
    {
        var buffer = new byte[KbdLayerClient.MaxMacroCount * MacroEntryBytes];
        using var ms = new MemoryStream(buffer);
        using var w = new BinaryWriter(ms);
        for (int id = 0; id < KbdLayerClient.MaxMacroCount; id++)
        {
            ms.Position = (long)id * MacroEntryBytes;
            if (!macros.TryGetValue(id, out var steps) || steps.Count == 0) continue;
            if (steps.Count > KbdLayerClient.MaxMacroSteps)
                throw new InvalidOperationException(Loc.T($"マクロ{id}のステップ数 {steps.Count} が上限 {KbdLayerClient.MaxMacroSteps} を超えています", $"Macro {id} has {steps.Count} steps, which exceeds the limit of {KbdLayerClient.MaxMacroSteps}"));
            w.Write((uint)steps.Count);
            foreach (var s in steps)
            {
                w.Write(s.ScanCode);
                w.Write((byte)(s.IsBreak ? 1 : 0));
                w.Write((byte)0);
            }
        }
        return buffer;
    }

    /// <summary>"Combos" 値: 16個ぶん固定長（Used + ScanCode1 + ScanCode2 + 出力動作）。</summary>
    public static byte[] SerializeCombos(IReadOnlyDictionary<int, ComboEntry?> combos)
    {
        var buffer = new byte[KbdLayerClient.MaxComboCount * ComboEntryBytes];
        using var ms = new MemoryStream(buffer);
        using var w = new BinaryWriter(ms);
        foreach (var (id, e) in combos)
        {
            if (e == null) continue;
            if (id < 0 || id >= KbdLayerClient.MaxComboCount) throw new InvalidOperationException(Loc.T($"コンボの番号 {id} が範囲外です", $"Combo number {id} is out of range"));
            ms.Position = (long)id * ComboEntryBytes;
            w.Write(1u);
            w.Write(e.ScanCode1);
            w.Write(e.ScanCode2);
            WriteAction(w, e.Output);
        }
        return buffer;
    }

    /// <summary>"TapDances" 値: 16個ぶん固定長（Used + 1回タップ・長押し・2回・3回の動作）。</summary>
    public static byte[] SerializeTapDances(IReadOnlyDictionary<int, TapDanceEntry?> tapDances)
    {
        var buffer = new byte[KbdLayerClient.MaxTapDanceCount * TapDanceEntryBytes];
        using var ms = new MemoryStream(buffer);
        using var w = new BinaryWriter(ms);
        foreach (var (id, e) in tapDances)
        {
            if (e == null) continue;
            if (id < 0 || id >= KbdLayerClient.MaxTapDanceCount) throw new InvalidOperationException(Loc.T($"タップダンスの番号 {id} が範囲外です", $"Tap dance number {id} is out of range"));
            ms.Position = (long)id * TapDanceEntryBytes;
            w.Write(1u);
            WriteAction(w, e.SingleTap);
            WriteAction(w, e.SingleHold);
            WriteAction(w, e.DoubleTap);
            WriteAction(w, e.TripleTap);
        }
        return buffer;
    }

    /// <summary>"KeyOverrides" 値: 32個ぶん固定長（Used + 修飾キー + 予約 + スキャンコード + 置き換え先の動作）。</summary>
    public static byte[] SerializeKeyOverrides(IReadOnlyDictionary<int, KeyOverrideEntry?> keyOverrides)
    {
        var buffer = new byte[KbdLayerClient.MaxKeyOverrideCount * KeyOverrideEntryBytes];
        using var ms = new MemoryStream(buffer);
        using var w = new BinaryWriter(ms);
        foreach (var (id, e) in keyOverrides)
        {
            if (e == null) continue;
            if (id < 0 || id >= KbdLayerClient.MaxKeyOverrideCount) throw new InvalidOperationException(Loc.T($"キー置換の番号 {id} が範囲外です", $"Key override number {id} is out of range"));
            ms.Position = (long)id * KeyOverrideEntryBytes;
            w.Write(1u);
            w.Write(e.TriggerMods);
            w.Write(e.Flags);
            w.Write(e.TriggerScanCode);
            WriteAction(w, e.Replacement);
        }
        return buffer;
    }

    /// <summary>1つの保存先に書く値の一覧（名前、値、種類）。SET_CONFIG などの IOCTL が書くものと同じ。</summary>
    public static List<(string Name, object Value, RegistryValueKind Kind)> BuildValues(DriverSettings s)
    {
        if (s.TapHoldMode > TapHoldModes.TapPreferred)
            throw new InvalidOperationException(Loc.T($"タップホールドの判定方式 {s.TapHoldMode} が範囲外です", $"Tap-hold mode {s.TapHoldMode} is out of range"));
        uint toggle = s.TappingToggle != 0 ? s.TappingToggle : KbdLayerClient.DefaultTappingToggle;
        if (toggle > KbdLayerClient.MaxTappingToggle)
            throw new InvalidOperationException(Loc.T($"TTの回数 {toggle} が上限 {KbdLayerClient.MaxTappingToggle} を超えています", $"TT tap count {toggle} exceeds the limit of {KbdLayerClient.MaxTappingToggle}"));

        var values = new List<(string, object, RegistryValueKind)>();
        void Dword(string name, uint v) => values.Add((name, unchecked((int)v), RegistryValueKind.DWord));
        void Binary(string name, byte[] v) => values.Add((name, v, RegistryValueKind.Binary));

        if (s.LayerMap != null) Binary("LayerMap", s.LayerMap);
        Dword("Enabled", s.LayerMap != null ? 1u : 0u);
        Dword("TapTermMs", s.TapTermMs != 0 ? s.TapTermMs : DefaultTapTermMs);
        Dword("ComboTermMs", s.ComboTermMs != 0 ? s.ComboTermMs : DefaultComboTermMs);
        Dword("TapDanceTermMs", s.TapDanceTermMs != 0 ? s.TapDanceTermMs : DefaultTapDanceTermMs);
        Dword("TapHoldMode", s.TapHoldMode);
        Dword("QuickTapTermMs", s.QuickTapTermMs);
        Dword("TappingToggle", toggle);
        if (s.Macros.Values.Any(m => m.Count > 0)) Binary("Macros", SerializeMacros(s.Macros));
        if (s.Combos.Values.Any(c => c != null)) Binary("Combos", SerializeCombos(s.Combos));
        if (s.TapDances.Values.Any(t => t != null)) Binary("TapDances", SerializeTapDances(s.TapDances));
        if (s.KeyOverrides.Values.Any(k => k != null)) Binary("KeyOverrides", SerializeKeyOverrides(s.KeyOverrides));
        if (s.Features is uint f)
        {
            if ((f & ~KbdLayerClient.FeatureAll) != 0) throw new InvalidOperationException(Loc.T($"機能のON/OFFの値 0x{f:X} に未知のビットがあります", $"The feature on/off value 0x{f:X} has unknown bits"));
            Dword("EnabledFeatures", f & FeatureAllV1); // 旧ドライバ向けの値（3機能のビットだけ）
            Dword("EnabledFeaturesEx", f);
        }
        if (s.Nicola is { Length: > 0 }) Binary("Nicola", s.Nicola);
        // これが無いと、ドライバが旧形式（LayerMaps\<DeviceId>）からの移行を試み、書いた値を上書きしうる。
        Dword("Migrated", 1);
        return values;
    }

    // ------------------------------------------------------------------
    // レジストリ
    // ------------------------------------------------------------------

    /// <summary>この保存先にレイヤーマップ（または何らかの設定）が既にあるか。</summary>
    public bool Exists(string name)
    {
        using var key = _hive.OpenSubKey($@"{_layerMapsPath}\{name}");
        return key != null && key.ValueCount > 0;
    }

    public bool HasBackup(string name) => Exists(name + BackupSuffix);

    /// <summary>退避（.bak）がある保存先の名前（".bak" を除く。例: "M_0B450000"）。</summary>
    public List<string> ListBackups()
    {
        using var key = _hive.OpenSubKey(_layerMapsPath);
        if (key == null) return new List<string>();
        return key.GetSubKeyNames()
            .Where(n => n.EndsWith(BackupSuffix, StringComparison.OrdinalIgnoreCase))
            .Select(n => n[..^BackupSuffix.Length])
            .Where(HasBackup)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 保存先 <paramref name="name"/> を、<paramref name="settings"/> の内容で置き換える。すでに設定があれば、置き換える前に
    /// 「&lt;name&gt;.bak」へ1世代だけ退避する（前の退避は捨てる）。書く前に値を作るので、形式に問題があれば何も変えずに例外になる。
    /// </summary>
    /// <returns>退避した（前の設定があった）なら true。</returns>
    public bool Write(string name, DriverSettings settings)
    {
        var values = BuildValues(settings); // 先に作って検証する

        bool backedUp = false;
        if (Exists(name))
        {
            _hive.DeleteSubKeyTree($@"{_layerMapsPath}\{name}{BackupSuffix}", throwOnMissingSubKey: false);
            CopyValues(name, name + BackupSuffix);
            backedUp = true;
        }

        using var key = _hive.CreateSubKey($@"{_layerMapsPath}\{name}", writable: true)
                        ?? throw new IOException(Loc.T($"レジストリキー {name} を作れません", $"Cannot create registry key {name}"));
        foreach (var n in key.GetValueNames()) key.DeleteValue(n);
        foreach (var (n, v, kind) in values) key.SetValue(n, v, kind);
        return backedUp;
    }

    // ------------------------------------------------------------------
    // ドライバ全体の設定（復旧用）。Parameters の直下に、ドライバが書くのと同じ形で書く。
    // ------------------------------------------------------------------

    private string ParametersPath
    {
        get
        {
            int i = _layerMapsPath.LastIndexOf('\\');
            return i < 0 ? _layerMapsPath : _layerMapsPath[..i];
        }
    }

    /// <summary>
    /// 緊急解除の組み合わせ（Parameters\EmergencyRelease の EmergencyReleaseKeyCount と EmergencyReleaseScanCodes）。
    /// ドライバの検証と同じく、2〜6個で、重複なし（ドライバは、合わない値を読むと、既定の組み合わせに戻す）。
    /// </summary>
    public void WriteEmergencyRelease(IReadOnlyList<ushort> scanCodes)
    {
        if (scanCodes.Count < KbdLayerClient.EmergencyMinKeys || scanCodes.Count > KbdLayerClient.EmergencyMaxKeys)
            throw new InvalidOperationException(Loc.T($"緊急解除のキーは{KbdLayerClient.EmergencyMinKeys}〜{KbdLayerClient.EmergencyMaxKeys}個です", $"The emergency release needs {KbdLayerClient.EmergencyMinKeys} to {KbdLayerClient.EmergencyMaxKeys} keys"));
        if (scanCodes.Distinct().Count() != scanCodes.Count)
            throw new InvalidOperationException(Loc.T("緊急解除のキーが重複しています", "The emergency release keys contain duplicates"));
        var bytes = new byte[scanCodes.Count * sizeof(ushort)];
        for (int i = 0; i < scanCodes.Count; i++) BitConverter.GetBytes(scanCodes[i]).CopyTo(bytes, i * sizeof(ushort));
        using var key = _hive.CreateSubKey($@"{ParametersPath}\EmergencyRelease", writable: true)
                        ?? throw new IOException(Loc.T("レジストリキー EmergencyRelease を作れません", "Cannot create registry key EmergencyRelease"));
        key.SetValue("EmergencyReleaseKeyCount", scanCodes.Count, RegistryValueKind.DWord);
        key.SetValue("EmergencyReleaseScanCodes", bytes, RegistryValueKind.Binary);
    }

    /// <summary>i8042prt の設定（Parameters\I8042Override。バイト列は <see cref="KbdLayerClient.SerializeI8042Config"/>）。</summary>
    public void WriteI8042Config(I8042Config config)
    {
        if (config.Profiles.Any(p => p.ModelKey == 0))
            throw new InvalidOperationException(Loc.T("i8042prt のプロファイルの ModelKey に 0 は使えません", "ModelKey 0 cannot be used in an i8042prt profile"));
        if (config.Profiles.Select(p => p.ModelKey).Distinct().Count() != config.Profiles.Count)
            throw new InvalidOperationException(Loc.T("i8042prt のプロファイルの ModelKey が重複しています", "The i8042prt profiles contain a duplicate ModelKey"));
        if (config.Profiles.Any(p => new[] { p.Identifier, p.LayerJpn, p.LayerKor }.Any(s => s != null && s.Length >= KbdLayerClient.I8042StrChars)))
            throw new InvalidOperationException(Loc.T($"i8042prt の文字列は{KbdLayerClient.I8042StrChars - 1}文字までです", $"i8042prt strings are limited to {KbdLayerClient.I8042StrChars - 1} characters"));
        byte[] bytes = KbdLayerClient.SerializeI8042Config(config); // 先に作って検証する
        using var key = _hive.CreateSubKey(ParametersPath, writable: true)
                        ?? throw new IOException(Loc.T("レジストリキー Parameters を作れません", "Cannot create registry key Parameters"));
        key.SetValue("I8042Override", bytes, RegistryValueKind.Binary);
    }

    /// <summary>キーボード（InstanceKey = UniqId）ごとの共有範囲（Parameters\DeviceBinding の値。値名は InstanceKey の8桁の16進数）。</summary>
    public void WriteDeviceBinding(uint instanceKey, uint binding)
    {
        if (instanceKey == 0 || binding > KbdLayerClient.BindingGlobal)
            throw new InvalidOperationException(Loc.T("共有範囲の値が不正です", "Invalid sharing scope value"));
        using var key = _hive.CreateSubKey($@"{ParametersPath}\DeviceBinding", writable: true)
                        ?? throw new IOException(Loc.T("レジストリキー DeviceBinding を作れません", "Cannot create registry key DeviceBinding"));
        key.SetValue(instanceKey.ToString("X8"), (int)binding, RegistryValueKind.DWord);
    }

    /// <summary>
    /// 退避（.bak）と本体を入れ替える。もう一度呼べば、元に戻る。退避が無ければ false。
    /// ドライバは、キーボードが接続されたときに読むので、反映にはキーボードの接続し直し（または再起動）が要る。
    /// </summary>
    public bool Restore(string name)
    {
        string backup = name + BackupSuffix;
        if (!HasBackup(name)) return false;

        string temp = name + ".swap";
        _hive.DeleteSubKeyTree($@"{_layerMapsPath}\{temp}", throwOnMissingSubKey: false);
        if (Exists(name)) CopyValues(name, temp);           // 今の本体を一時退避
        ReplaceValues(backup, name);                        // .bak → 本体
        if (Exists(temp)) ReplaceValues(temp, backup);      // 今の本体 → .bak（入れ替え）
        else _hive.DeleteSubKeyTree($@"{_layerMapsPath}\{backup}", throwOnMissingSubKey: false);
        _hive.DeleteSubKeyTree($@"{_layerMapsPath}\{temp}", throwOnMissingSubKey: false);
        return true;
    }

    private void CopyValues(string from, string to)
    {
        using var src = _hive.OpenSubKey($@"{_layerMapsPath}\{from}");
        if (src == null) return;
        using var dst = _hive.CreateSubKey($@"{_layerMapsPath}\{to}", writable: true)
                        ?? throw new IOException(Loc.T($"レジストリキー {to} を作れません", $"Cannot create registry key {to}"));
        foreach (var n in dst.GetValueNames()) dst.DeleteValue(n);
        foreach (var n in src.GetValueNames())
            dst.SetValue(n, src.GetValue(n, null, RegistryValueOptions.DoNotExpandEnvironmentNames)!, src.GetValueKind(n));
    }

    /// <summary>to の値を、from の値で置き換える（from は残す）。</summary>
    private void ReplaceValues(string from, string to) => CopyValues(from, to);
}
