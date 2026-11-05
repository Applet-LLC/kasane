using System.IO;
using Kasane.Keycodes;
using Kasane.Model;
using Xunit;

namespace Kasane.Tests;

/// <summary>IME を変える経路の決め方（<see cref="ImeRoutePlanner"/>）と、tray.json の ImeSwitch（2026-11-05）。</summary>
public class ImeRoutePlannerTests
{
    private static readonly ImeRoute[] ImmThenKey = { ImeRoute.Imm, ImeRoute.VkKey };
    private static readonly ImeRoute[] DriverFirst = { ImeRoute.DriverAltKanji, ImeRoute.Imm, ImeRoute.VkKey };
    private static readonly bool[] Bools = { false, true };

    // 方式を書いた行は、設定・昇格・ドライバ・状態に関わらず、その方式だけ。
    [Theory]
    [InlineData(ImeMethod.Imm, ImeRoute.Imm)]
    [InlineData(ImeMethod.Key, ImeRoute.VkKey)]
    [InlineData(ImeMethod.AltKanji, ImeRoute.AltKanji)]
    [InlineData(ImeMethod.Tsf, ImeRoute.Tsf)]
    public void FixedMethod_IsAlwaysTheOnlyRoute(ImeMethod method, ImeRoute expected)
    {
        foreach (var setting in Enum.GetValues<ImeSwitch>())
        foreach (bool elevated in Bools)
        foreach (bool driver in Bools)
        foreach (bool known in Bools)
            Assert.Equal(new[] { expected }, ImeRoutePlanner.Plan(method, setting, elevated, driver, known));
    }

    // 自動で、通常権限のトレイは、ドライバ・状態に関わらず IMM32 → IME キー（1.00 からの動き）。
    [Fact]
    public void Auto_NotElevated_IsImmThenKey()
    {
        foreach (bool driver in Bools)
        foreach (bool known in Bools)
            Assert.Equal(ImmThenKey, ImeRoutePlanner.Plan(ImeMethod.Auto, ImeSwitch.Auto, false, driver, known));
    }

    [Fact]
    public void Auto_Elevated_DriverUsable_StateKnown_PutsDriverFirst()
    {
        Assert.Equal(DriverFirst, ImeRoutePlanner.Plan(ImeMethod.Auto, ImeSwitch.Auto, true, true, true));
    }

    // ドライバが使えない・状態を読めない（切り替えのキーは送れない）なら、通常権限と同じ。
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void Auto_Elevated_OtherwiseImmThenKey(bool driver, bool known)
    {
        Assert.Equal(ImmThenKey, ImeRoutePlanner.Plan(ImeMethod.Auto, ImeSwitch.Auto, true, driver, known));
    }

    // 絶対指定のキー（IME オン/オフ、変換/無変換）は、昇格・ドライバ・状態に関わらず、そのキー → IMM32。
    [Theory]
    [InlineData(ImeSwitch.ImeKeys, ImeRoute.ImeOnOffKey)]
    [InlineData(ImeSwitch.Henkan, ImeRoute.HenkanKey)]
    public void AbsoluteKeys_ThenImm(ImeSwitch setting, ImeRoute first)
    {
        foreach (bool elevated in Bools)
        foreach (bool driver in Bools)
        foreach (bool known in Bools)
            Assert.Equal(new[] { first, ImeRoute.Imm }, ImeRoutePlanner.Plan(ImeMethod.Auto, setting, elevated, driver, known));
    }

    // 切り替えのキーは、状態を読めたときだけ。読めなければ IMM32 だけ。
    [Fact]
    public void Toggle_NeedsKnownState()
    {
        foreach (bool elevated in Bools)
        foreach (bool driver in Bools)
        {
            Assert.Equal(new[] { ImeRoute.AltKanji, ImeRoute.Imm }, ImeRoutePlanner.Plan(ImeMethod.Auto, ImeSwitch.Toggle, elevated, driver, true));
            Assert.Equal(new[] { ImeRoute.Imm }, ImeRoutePlanner.Plan(ImeMethod.Auto, ImeSwitch.Toggle, elevated, driver, false));
        }
    }

    [Fact]
    public void ImmSetting_IsImmOnly()
    {
        foreach (bool elevated in Bools)
        foreach (bool driver in Bools)
        foreach (bool known in Bools)
            Assert.Equal(new[] { ImeRoute.Imm }, ImeRoutePlanner.Plan(ImeMethod.Auto, ImeSwitch.Imm, elevated, driver, known));
    }

    [Theory]
    [InlineData(null, ImeSwitch.Auto)]
    [InlineData("", ImeSwitch.Auto)]
    [InlineData("auto", ImeSwitch.Auto)]
    [InlineData("ImeKeys", ImeSwitch.ImeKeys)]
    [InlineData(" henkan ", ImeSwitch.Henkan)]
    [InlineData("Toggle", ImeSwitch.Toggle)]
    [InlineData("IMM", ImeSwitch.Imm)]
    [InlineData("tsf", ImeSwitch.Auto)]
    [InlineData("99", ImeSwitch.Auto)]
    public void ImeSwitch_Parse(string? text, ImeSwitch expected)
    {
        Assert.Equal(expected, ImeSwitches.Parse(text));
    }

    // 保存に使う名前（tray.json の値）は変えない。
    [Fact]
    public void ImeSwitch_NamesAreStable()
    {
        Assert.Equal(new[] { "Auto", "ImeKeys", "Henkan", "Toggle", "Imm" }, Enum.GetNames<ImeSwitch>());
    }

    [Fact]
    public void TrayConfig_ImeSwitch_DefaultsToAuto_AndIsNotWritten()
    {
        string dir = Path.Combine(Path.GetTempPath(), "kasane-test-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "tray.json");
        try
        {
            Assert.Equal(ImeSwitch.Auto, new TrayConfig().ImeSwitchValue);

            // 1.01 までの tray.json（項目が無い）は、既定として読める。
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, "{ \"ImeLink\": true, \"ImeSource\": \"Imm32\" }");
            Assert.Equal(ImeSwitch.Auto, TrayConfig.Load(path).ImeSwitchValue);

            // 既定のまま保存しても、項目は書かない。
            new TrayConfig().Save(path);
            Assert.DoesNotContain("ImeSwitch", File.ReadAllText(path));

            new TrayConfig { ImeSwitchName = "Henkan" }.Save(path);
            Assert.Contains("\"ImeSwitch\": \"Henkan\"", File.ReadAllText(path));
            Assert.Equal(ImeSwitch.Henkan, TrayConfig.Load(path).ImeSwitchValue);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    // キーマップの IME オン / IME オフ（USB の LANG1 / LANG2）。QMK の別名も同じキー。
    [Theory]
    [InlineData("KC_LNG1", 0xE0F2)]
    [InlineData("KC_LANG1", 0xE0F2)]
    [InlineData("KC_HAEN", 0xE0F2)]
    [InlineData("KC_LNG2", 0xE0F1)]
    [InlineData("KC_LANG2", 0xE0F1)]
    [InlineData("KC_HANJ", 0xE0F1)]
    public void Keycodes_ImeOnOff(string token, int scan)
    {
        Assert.True(KeycodeTable.TryResolveBaseKey(token, out ushort sc));
        Assert.Equal(scan, sc);
    }

    [Fact]
    public void Keycodes_ImeOnOff_CanonicalNames()
    {
        Assert.Equal("KC_LNG1", KeycodeTable.KeyToken(0xE0F2));
        Assert.Equal("KC_LNG2", KeycodeTable.KeyToken(0xE0F1));
    }
}
