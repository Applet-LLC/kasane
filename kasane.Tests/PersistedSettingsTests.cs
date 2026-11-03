using Kasane.Interop;
using Kasane.Keycodes;
using Kasane.Model;
using Microsoft.Win32;

namespace Kasane.Tests;

/// <summary>インストール時にドライバのレジストリへ直接書く設定（形式は LayerEngine.c の PersistAll* に合わせる）。</summary>
public class PersistedSettingsTests : IDisposable
{
    private const string TestRoot = @"Software\kasane-tests\PersistedSettings";
    private const string LayerMaps = TestRoot + @"\LayerMaps";

    public PersistedSettingsTests() => Cleanup();
    public void Dispose() => Cleanup();
    private static void Cleanup() => Registry.CurrentUser.DeleteSubKeyTree(TestRoot, throwOnMissingSubKey: false);

    private static DriverSettings Settings(
        byte[]? map = null, uint tapTerm = 0, Dictionary<int, ComboEntry?>? combos = null, uint? features = null) =>
        new(map, tapTerm, 0, 0, 0, 0, 0,
            new Dictionary<int, List<MacroStep>>(),
            combos ?? new Dictionary<int, ComboEntry?>(),
            new Dictionary<int, TapDanceEntry?>(),
            new Dictionary<int, KeyOverrideEntry?>(),
            features, null, false, new List<string>());

    // ---- ModelKey（docs/DEVICE-ID.md 第7節の実機の値）----

    [Theory]
    [InlineData(@"HID\VID_13BA&PID_0001&REV_0210", 0xECBF0000u)]
    [InlineData(@"HID\VID_04FE&PID_0016&REV_0224&MI_00", 0x0B450000u)]
    [InlineData(@"ACPI\VEN_LEN&DEV_0071", 0xF1DF0000u)]
    [InlineData(@"HID\VID_276D&PID_FFE2&REV_0100&MI_00", 0xC5580000u)]
    public void ModelKey_MatchesTheDriver(string hardwareId, uint expected) =>
        Assert.Equal(expected, DeviceKeys.ModelKey(hardwareId));

    [Fact]
    public void ModelKey_EmptyIsNull()
    {
        Assert.Null(DeviceKeys.ModelKey(null));
        Assert.Null(DeviceKeys.ModelKey(""));
        Assert.Equal("M_0B450000", DeviceKeys.ModelSubKeyName(0x0B450000));
    }

    // ---- 形式 ----

    [Fact]
    public void Serialize_SizesMatchTheDriver()
    {
        Assert.Equal(33280, PersistedSettings.SerializeMacros(new Dictionary<int, List<MacroStep>>()).Length);
        Assert.Equal(192, PersistedSettings.SerializeCombos(new Dictionary<int, ComboEntry?>()).Length);
        Assert.Equal(320, PersistedSettings.SerializeTapDances(new Dictionary<int, TapDanceEntry?>()).Length);
        Assert.Equal(1536, PersistedSettings.SerializeKeyOverrides(new Dictionary<int, KeyOverrideEntry?>()).Length);
    }

    [Fact]
    public void SerializeMacros_PlacesStepsAtTheEntryOffset()
    {
        var blob = PersistedSettings.SerializeMacros(new Dictionary<int, List<MacroStep>>
        {
            [2] = new() { new MacroStep(0x001E, false), new MacroStep(0x001E, true) },
        });
        int off = 2 * 260;
        Assert.Equal(2u, BitConverter.ToUInt32(blob, off));
        Assert.Equal(0x001E, BitConverter.ToUInt16(blob, off + 4));
        Assert.Equal(0, blob[off + 6]);
        Assert.Equal(1, blob[off + 10]); // 2つ目のステップの IsBreak
        Assert.Equal(0u, BitConverter.ToUInt32(blob, 0)); // 他のマクロは空
    }

    [Fact]
    public void SerializeCombos_PlacesUsedAndAction()
    {
        var blob = PersistedSettings.SerializeCombos(new Dictionary<int, ComboEntry?>
        {
            [1] = new ComboEntry(0x001E, 0x0030, new KeyAction(ActionType.Key, 0, 0x0001)),
        });
        Assert.Equal(1u, BitConverter.ToUInt32(blob, 12));
        Assert.Equal(0x001E, BitConverter.ToUInt16(blob, 16));
        Assert.Equal(0x0030, BitConverter.ToUInt16(blob, 18));
        Assert.Equal(ActionType.Key, blob[20]);
        Assert.Equal(0x0001, BitConverter.ToUInt16(blob, 22));
        Assert.Equal(0u, BitConverter.ToUInt32(blob, 0));
    }

    [Fact]
    public void SerializeKeyOverrides_PlacesModsScanCodeAndAction()
    {
        var blob = PersistedSettings.SerializeKeyOverrides(new Dictionary<int, KeyOverrideEntry?>
        {
            [0] = new KeyOverrideEntry(0x02, 0x000E, new KeyAction(ActionType.Key, 0, 0x0053)),
        });
        Assert.Equal(1u, BitConverter.ToUInt32(blob, 0));
        Assert.Equal(0x02, blob[4]);
        Assert.Equal(0, blob[5]);
        Assert.Equal(0x000E, BitConverter.ToUInt16(blob, 6));
        Assert.Equal(0x0053, BitConverter.ToUInt16(blob, 10));
    }

    // 実機（ドライバが書いたレジストリ。HKLM\...\LayerMaps\M_F1DF0000、2026-10-12 に採取）の先頭バイトとの一致。
    [Fact]
    public void SerializeMacros_MatchesTheDriversOwnBytes()
    {
        var blob = PersistedSettings.SerializeMacros(new Dictionary<int, List<MacroStep>>
        {
            [0] = new() { new MacroStep(0xE04B, false), new MacroStep(0xE04B, true), new MacroStep(0xE048, false), new MacroStep(0xE048, true) },
        });
        // StepCount=4、続けて (E04B 押下)(E04B 解放)(E048 押下)(E048 解放)。
        Assert.Equal(Convert.FromHexString("04000000" + "4BE00000" + "4BE00100" + "48E00000" + "48E00100"), blob[..20]);
    }

    [Fact]
    public void SerializeKeyOverrides_MatchesTheDriversOwnBytes()
    {
        var blob = PersistedSettings.SerializeKeyOverrides(new Dictionary<int, KeyOverrideEntry?>
        {
            [0] = new KeyOverrideEntry(0x01, 0x001E, new KeyAction(ActionType.Key, 0, 0xE047)),
        });
        Assert.Equal(Convert.FromHexString("010000000100" + "1E00" + "020047E0"), blob[..12]);
    }

    [Fact]
    public void BuildValues_AppliesDriverDefaultsAndMigratedMarker()
    {
        var v = PersistedSettings.BuildValues(Settings(map: new byte[] { 1, 2, 3 })).ToDictionary(x => x.Name, x => x.Value);
        Assert.Equal(1, v["Enabled"]);
        Assert.Equal(200, v["TapTermMs"]);   // 0 → 既定
        Assert.Equal(50, v["ComboTermMs"]);
        Assert.Equal(200, v["TapDanceTermMs"]);
        Assert.Equal(5, v["TappingToggle"]);
        Assert.Equal(1, v["Migrated"]);      // 旧形式の移行で、書いた値が上書きされないように
        Assert.False(v.ContainsKey("Combos")); // 空なら書かない
        Assert.False(v.ContainsKey("EnabledFeatures")); // 触らない
    }

    [Fact]
    public void BuildValues_NoLayerMap_IsDisabled()
    {
        var v = PersistedSettings.BuildValues(Settings(map: null)).ToDictionary(x => x.Name, x => x.Value);
        Assert.False(v.ContainsKey("LayerMap"));
        Assert.Equal(0, v["Enabled"]);
    }

    [Fact]
    public void BuildValues_Features_WritesLegacyAndExValues()
    {
        var v = PersistedSettings.BuildValues(Settings(features: 0xB)).ToDictionary(x => x.Name, x => x.Value);
        Assert.Equal(0x3, v["EnabledFeatures"]);   // 旧値は3機能のビットだけ
        Assert.Equal(0xB, v["EnabledFeaturesEx"]);
    }

    [Fact]
    public void BuildValues_RejectsUnknownFeatureBits() =>
        Assert.Throws<InvalidOperationException>(() => PersistedSettings.BuildValues(Settings(features: 0x100)));

    // ---- レジストリ（HKCU の作業用キー）----

    private static PersistedSettings Store() => new(Registry.CurrentUser, LayerMaps);

    [Fact]
    public void Write_Fresh_HasNoBackup()
    {
        var store = Store();
        Assert.False(store.Write("M_0B450000", Settings(map: new byte[] { 1 }, tapTerm: 150)));
        Assert.True(store.Exists("M_0B450000"));
        Assert.False(store.HasBackup("M_0B450000"));
        using var key = Registry.CurrentUser.OpenSubKey(LayerMaps + @"\M_0B450000")!;
        Assert.Equal(150, key.GetValue("TapTermMs"));
        Assert.Equal(1, key.GetValue("Migrated"));
    }

    [Fact]
    public void Write_Existing_BacksUpOneGeneration()
    {
        var store = Store();
        store.Write("M_X", Settings(map: new byte[] { 1 }, tapTerm: 111));
        Assert.True(store.Write("M_X", Settings(map: new byte[] { 2 }, tapTerm: 222)));
        Assert.True(store.Write("M_X", Settings(map: new byte[] { 3 }, tapTerm: 333))); // 退避は1世代だけ（前の退避は捨てる）

        using var key = Registry.CurrentUser.OpenSubKey(LayerMaps + @"\M_X")!;
        Assert.Equal(333, key.GetValue("TapTermMs"));
        using var bak = Registry.CurrentUser.OpenSubKey(LayerMaps + @"\M_X.bak")!;
        Assert.Equal(222, bak.GetValue("TapTermMs"));
        Assert.Equal(new[] { "M_X" }, store.ListBackups());
    }

    [Fact]
    public void Write_ReplacesValuesNotInTheNewSettings()
    {
        var store = Store();
        var combos = new Dictionary<int, ComboEntry?> { [0] = new ComboEntry(1, 2, new KeyAction(ActionType.Key, 0, 3)) };
        store.Write("M_X", Settings(map: new byte[] { 1 }, combos: combos));
        store.Write("M_X", Settings(map: new byte[] { 1 })); // コンボ無し
        using var key = Registry.CurrentUser.OpenSubKey(LayerMaps + @"\M_X")!;
        Assert.Null(key.GetValue("Combos")); // 古い値が残らない
    }

    [Fact]
    public void Restore_SwapsBackupAndCurrent_AndSwapsBackAgain()
    {
        var store = Store();
        store.Write("M_X", Settings(map: new byte[] { 1 }, tapTerm: 111));
        store.Write("M_X", Settings(map: new byte[] { 2 }, tapTerm: 222));

        Assert.True(store.Restore("M_X"));
        using (var key = Registry.CurrentUser.OpenSubKey(LayerMaps + @"\M_X")!) Assert.Equal(111, key.GetValue("TapTermMs"));
        using (var bak = Registry.CurrentUser.OpenSubKey(LayerMaps + @"\M_X.bak")!) Assert.Equal(222, bak.GetValue("TapTermMs"));

        Assert.True(store.Restore("M_X")); // もう一度で元に戻る
        using (var key = Registry.CurrentUser.OpenSubKey(LayerMaps + @"\M_X")!) Assert.Equal(222, key.GetValue("TapTermMs"));
        Assert.Null(Registry.CurrentUser.OpenSubKey(LayerMaps + @"\M_X.swap"));
    }

    [Fact]
    public void Restore_WithoutBackup_ReturnsFalse()
    {
        var store = Store();
        store.Write("M_X", Settings(map: new byte[] { 1 }));
        Assert.False(store.Restore("M_X"));
        Assert.Empty(store.ListBackups());
    }
}
