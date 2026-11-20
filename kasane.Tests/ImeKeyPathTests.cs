using System.IO;
using Kasane.Model;
using Kasane.Model.Portable;
using Xunit;

namespace Kasane.Tests;

/// <summary>IME のキーの送り方（<see cref="ImeKeyPath"/>。tray.json の ImeKeyPath。2026-11-20）。</summary>
public class ImeKeyPathTests : IDisposable
{
    private static readonly bool[] Bools = { false, true };
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kasane-test-" + Guid.NewGuid().ToString("N"));

    public ImeKeyPathTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // 自動は、今までと同じ計画（設定を足す前の Plan と同じ）。
    [Fact]
    public void Auto_IsUnchanged()
    {
        foreach (var setting in Enum.GetValues<ImeSwitch>())
        foreach (var method in Enum.GetValues<ImeMethod>())
        foreach (bool elevated in Bools)
        foreach (bool driver in Bools)
        foreach (bool known in Bools)
            Assert.Equal(ImeRoutePlanner.Plan(method, setting, elevated, driver, known),
                         ImeRoutePlanner.Plan(method, setting, ImeKeyPath.Auto, elevated, driver, known));
    }

    // SendInput: 管理者のトレイでも、ドライバ経由の Alt+半角/全角を使わない。
    [Fact]
    public void SendInput_AutoSetting_NeverUsesDriver()
    {
        foreach (bool elevated in Bools)
        foreach (bool driver in Bools)
        foreach (bool known in Bools)
            Assert.Equal(new[] { ImeRoute.Imm, ImeRoute.VkKey }, ImeRoutePlanner.Plan(ImeMethod.Auto, ImeSwitch.Auto, ImeKeyPath.SendInput, elevated, driver, known));
    }

    // Driver: SendInput の IME キー（VkKey）へは戻さない。ドライバで送れないときは IMM32 だけ。
    [Fact]
    public void Driver_AutoSetting_NeverFallsBackToSendInputKey()
    {
        Assert.Equal(new[] { ImeRoute.DriverAltKanji, ImeRoute.Imm }, ImeRoutePlanner.Plan(ImeMethod.Auto, ImeSwitch.Auto, ImeKeyPath.Driver, true, true, true));
        foreach (bool elevated in Bools)
        foreach (bool driver in Bools)
        foreach (bool known in Bools)
            Assert.DoesNotContain(ImeRoute.VkKey, ImeRoutePlanner.Plan(ImeMethod.Auto, ImeSwitch.Auto, ImeKeyPath.Driver, elevated, driver, known));
        Assert.Equal(new[] { ImeRoute.Imm }, ImeRoutePlanner.Plan(ImeMethod.Auto, ImeSwitch.Auto, ImeKeyPath.Driver, false, true, true));
    }

    // ImeKeys / Henkan / Toggle と、方式を書いた行の計画は、送り方で変わらない（どちらで送るかはトレイが決める）。
    [Fact]
    public void OtherSettings_PlanDoesNotDependOnKeyPath()
    {
        foreach (var path in Enum.GetValues<ImeKeyPath>())
        foreach (var setting in new[] { ImeSwitch.ImeKeys, ImeSwitch.Henkan, ImeSwitch.Toggle, ImeSwitch.Imm })
        foreach (bool elevated in Bools)
        foreach (bool known in Bools)
            Assert.Equal(ImeRoutePlanner.Plan(ImeMethod.Auto, setting, elevated, true, known),
                         ImeRoutePlanner.Plan(ImeMethod.Auto, setting, path, elevated, true, known));
    }

    [Theory]
    [InlineData(null, ImeKeyPath.Auto)]
    [InlineData("", ImeKeyPath.Auto)]
    [InlineData("driver", ImeKeyPath.Driver)]
    [InlineData(" SendInput ", ImeKeyPath.SendInput)]
    [InlineData("nope", ImeKeyPath.Auto)]
    [InlineData("7", ImeKeyPath.Auto)]
    public void Parse(string? text, ImeKeyPath expected) => Assert.Equal(expected, ImeSwitches.ParseKeyPath(text));

    [Fact]
    public void NamesAreStable() => Assert.Equal(new[] { "Auto", "Driver", "SendInput" }, Enum.GetNames<ImeKeyPath>());

    [Fact]
    public void TrayConfig_DefaultNotWritten_AndRoundTrips()
    {
        string path = Path.Combine(_dir, "tray.json");
        new TrayConfig().Save(path);
        Assert.DoesNotContain("ImeKeyPath", File.ReadAllText(path));
        Assert.Equal(ImeKeyPath.Auto, TrayConfig.Load(path).ImeKeyPathValue);
        new TrayConfig { ImeKeyPathName = "SendInput" }.Save(path);
        Assert.Contains("\"ImeKeyPath\": \"SendInput\"", File.ReadAllText(path));
        Assert.Equal(ImeKeyPath.SendInput, TrayConfig.Load(path).ImeKeyPathValue);
    }

    [Fact]
    public void Recovery_RoundTrips_AndMissingFieldKeepsCurrent()
    {
        string path = Path.Combine(_dir, "tray.json");
        new TrayConfig { ImeKeyPathName = "Driver" }.Save(path);
        var r = RecoveryExporter.Collect(null, Array.Empty<uint>(), _dir, new PortableReport());
        Assert.Equal("Driver", r.TraySettings!.ImeKeyPath);

        RecoveryImporter.ApplyFiles(new PortableRecovery { TraySettings = new PortableTraySettings { ColorMode = "Dark" } }, _dir, new PortableReport());
        Assert.Equal(ImeKeyPath.Driver, TrayConfig.Load(path).ImeKeyPathValue);
        RecoveryImporter.ApplyFiles(new PortableRecovery { TraySettings = new PortableTraySettings { ImeKeyPath = "NoSuch" } }, _dir, new PortableReport());
        Assert.Equal(ImeKeyPath.Driver, TrayConfig.Load(path).ImeKeyPathValue);
        RecoveryImporter.ApplyFiles(new PortableRecovery { TraySettings = new PortableTraySettings { ImeKeyPath = "Auto" } }, _dir, new PortableReport());
        Assert.Null(TrayConfig.Load(path).ImeKeyPathName);
    }
}
