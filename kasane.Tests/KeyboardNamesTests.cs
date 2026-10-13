using Kasane.Model;
using Xunit;

namespace Kasane.Tests;

public class KeyboardNamesTests
{
    private static KeyboardNodeCandidate Node(string instanceId, string? hwid, string friendly = "HID キーボード デバイス", string? shell = null) =>
        new(instanceId, hwid, new KeyboardNameInfo(friendly, shell));

    // docs/DEVICE-ID.md 第7.3節の実機の値（DeviceInstanceId → InstanceKey）
    private const string UsbMi00 = @"HID\VID_04FE&PID_0016&MI_00\d&70295df&0&0000";   // CDEC0000
    private const string UsbSame1 = @"HID\VID_13BA&PID_0001\c&3a55714f&0&0000";       // 391B0000
    private const string UsbSame2 = @"HID\VID_13BA&PID_0001\c&117457ed&0&0000";       // 2BE50000
    private const string Hw13BA = @"HID\VID_13BA&PID_0001&REV_0210";

    [Fact]
    public void PickMatch_FindsTheNodeWhoseInstanceIdHashIsTheUniqId()
    {
        var list = new[] { Node(UsbMi00, @"HID\VID_04FE&PID_0016&REV_0224&MI_00", shell: "Keyboard A"), Node(UsbSame1, Hw13BA), Node(UsbSame2, Hw13BA) };
        Assert.Equal("Keyboard A", KeyboardNameRules.PickMatch(list, 0xCDEC0000, null)!.Names.ShellFriendlyName);
        Assert.Same(list[1], KeyboardNameRules.PickMatch(list, 0x391B0000, Hw13BA));
        Assert.Same(list[2], KeyboardNameRules.PickMatch(list, 0x2BE50000, Hw13BA)); // 同型番の2台目も区別できる
    }

    [Fact]
    public void PickMatch_ReturnsNullWhenNothingMatchesOrUniqIdIsZero()
    {
        var list = new[] { Node(UsbMi00, "x") };
        Assert.Null(KeyboardNameRules.PickMatch(list, 0x12340000, "x"));
        Assert.Null(KeyboardNameRules.PickMatch(list, 0, "x"));
        Assert.Null(KeyboardNameRules.PickMatch(Array.Empty<KeyboardNodeCandidate>(), 0xCDEC0000, null));
    }

    [Fact]
    public void PickMatch_OnHashCollision_NarrowsByHardwareId_OrGivesUp()
    {
        // 同じインスタンスIDの文字列を持つ2つのノード（衝突を模す）。HardwareId で絞れれば選ぶ。
        var a = Node(UsbSame1, "HW_A");
        var b = Node(UsbSame1, "HW_B");
        var list = new[] { a, b };
        Assert.Same(b, KeyboardNameRules.PickMatch(list, 0x391B0000, "HW_B"));
        Assert.Null(KeyboardNameRules.PickMatch(list, 0x391B0000, null));          // 決められない → 誤った名前を出さない
        Assert.Null(KeyboardNameRules.PickMatch(list, 0x391B0000, "HW_C"));
        Assert.Null(KeyboardNameRules.PickMatch(new[] { Node(UsbSame1, "HW_A"), Node(UsbSame1, "HW_A") }, 0x391B0000, "HW_A"));
    }

    [Fact]
    public void PickMatch_IsCaseSensitiveLikeTheDriver()
    {
        // ドライバは大文字小文字を区別してハッシュする。インスタンス部分が大文字の形では一致しない。
        var upper = Node(@"HID\VID_04FE&PID_0016&MI_00\D&70295DF&0&0000", "x");
        Assert.Null(KeyboardNameRules.PickMatch(new[] { upper }, 0xCDEC0000, "x"));
    }

    [Theory]
    [InlineData("HID Keyboard Device", "Some Keyboard", false)]
    [InlineData("hid keyboard device", "Some Keyboard", false)]
    [InlineData("HID キーボード デバイス", null, false)]
    [InlineData("Bluetooth HID Device", null, false)]
    [InlineData("Some Keyboard", "Some Keyboard", false)]   // デバイスマネージャーの名前と同じ
    [InlineData("some keyboard", "Some Keyboard", false)]
    [InlineData("", null, false)]
    [InlineData("   ", null, false)]
    [InlineData(null, null, false)]
    [InlineData("Logicool K380", "HID キーボード デバイス", true)]
    [InlineData("  Logicool K380 ", "HID キーボード デバイス", true)]
    public void IsUseful(string? name, string? deviceManagerName, bool expected) =>
        Assert.Equal(expected, KeyboardNameRules.IsUseful(name, deviceManagerName));

    [Theory]
    [InlineData(@"BTHENUM\DEV_C2F2FFDA05E8\7&1234ab&0&BLUETOOTHDEVICE_C2F2FFDA05E8", "C2F2FFDA05E8")]
    [InlineData(@"HID\{00001124-0000-1000-8000-00805F9B34FB}_VID&0002046D_PID&B35B\9&1a2b&0&c2f2ffda05e8_C00000000", "C2F2FFDA05E8")]
    [InlineData(@"BTHLE\DEV_AABBCCDDEEFF\8&2&0&AABBCCDDEEFF", "AABBCCDDEEFF")]
    [InlineData(@"HID\VID_04FE&PID_0016&MI_00\d&70295df&0&0000", null)]
    [InlineData(@"ACPI\LEN0071\4&e940ebc&0", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ExtractBdAddress(string? instanceId, string? expected) =>
        Assert.Equal(expected, KeyboardNameRules.ExtractBdAddress(instanceId));

    [Fact]
    public void DisplayName_OrderIsUniqIdUserNameFriendlyShellHardwareId()
    {
        var names = new KeyboardNameInfo("HID キーボード デバイス", "Logicool K380");
        Assert.Equal("CDEC0000  仕事用  HID キーボード デバイス  Logicool K380  HID\\X",
            KeyboardNameRules.DisplayName(0xCDEC0000, "仕事用", names, "HID\\X"));
    }

    [Fact]
    public void DisplayName_SkipsMissingParts_AndKeepsUniqIdFirst()
    {
        Assert.Equal("CDEC0000  HID\\X", KeyboardNameRules.DisplayName(0xCDEC0000, null, null, "HID\\X"));
        Assert.Equal("CDEC0000  HID\\X", KeyboardNameRules.DisplayName(0xCDEC0000, "  ", null, "HID\\X"));
        Assert.Equal("00000000", KeyboardNameRules.DisplayName(0, null, null, null));
    }

    [Fact]
    public void DisplayName_DoesNotRepeatTheSameText()
    {
        var names = new KeyboardNameInfo("K380", "k380");
        Assert.Equal("CDEC0000  K380  HID\\X", KeyboardNameRules.DisplayName(0xCDEC0000, "K380", names, "HID\\X"));
    }
}
