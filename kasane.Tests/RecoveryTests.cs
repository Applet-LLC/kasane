using Kasane.Interop;
using Microsoft.Win32;
using Kasane.Model;
using Kasane.Model.Portable;

namespace Kasane.Tests;

/// <summary>全体一括のエクスポートに加える「復旧用の設定」（トレイの設定、緊急解除、i8042prt、共有範囲、利用者のレイアウト）。</summary>
public class RecoveryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kasane-recovery-" + Guid.NewGuid().ToString("N"));

    public RecoveryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (Exception) { }
    }

    private sealed class FakeDriver : IRecoveryDriver
    {
        public List<ushort> Emergency { get; set; } = new() { 0x1D, 0x2A, 0x01 };
        public I8042Config I8042 { get; set; } = new(true, new[] { new I8042Profile(0x1234, 7, 0, "id", "jpn.dll", null) });
        public Dictionary<uint, uint> Scopes { get; } = new() { [0x55F70000] = KbdLayerClient.BindingInstance };
        public bool FailEmergency { get; set; }

        public List<ushort> GetEmergencyRelease() => Emergency;
        public void SetEmergencyRelease(IReadOnlyList<ushort> scanCodes)
        {
            if (FailEmergency) throw new KbdLayerException("test", 5);
            Emergency = scanCodes.ToList();
        }
        public I8042Config GetI8042Config() => I8042;
        public void SetI8042Config(I8042Config config) => I8042 = config;
        public DeviceScope GetDeviceScope(uint uniqId) => new(uniqId, 0, Scopes.GetValueOrDefault(uniqId));
        public DeviceScope SetDeviceScope(uint uniqId, uint binding) { Scopes[uniqId] = binding; return GetDeviceScope(uniqId); }
    }

    private void WriteTray(Action<TrayConfig> edit)
    {
        var tray = new TrayConfig();
        edit(tray);
        tray.Save(Path.Combine(_dir, "tray.json"));
    }

    private void WriteLayout(string name, string json = "[]")
    {
        string layouts = Path.Combine(_dir, "Layouts");
        Directory.CreateDirectory(layouts);
        File.WriteAllText(Path.Combine(layouts, $"kle-{name}.json"), json);
    }

    [Fact]
    public void Collect_GathersTrayDriverAndLayouts()
    {
        WriteTray(t => { t.DesktopViewMode = 3; t.ColorModeName = "Dark"; t.ShowLayerOnIcon = false; });
        WriteLayout("my-custom-1", "[[\"a\"]]");
        var report = new PortableReport();

        var r = RecoveryExporter.Collect(new FakeDriver(), new uint[] { 0x55F70000, 0 }, _dir, report);

        Assert.Equal(3, r.TraySettings!.DesktopViewMode);
        Assert.Equal("Dark", r.TraySettings.ColorMode);
        Assert.False(r.TraySettings.ShowLayerOnIcon);
        Assert.Equal(new ushort[] { 0x1D, 0x2A, 0x01 }, r.EmergencyRelease);
        Assert.True(r.I8042!.Enabled);
        Assert.Equal(0x1234u, r.I8042.Profiles.Single().ModelKey);
        Assert.Equal("0x55F70000", r.DeviceScopes!.Single().UniqId); // UniqId が 0 のものは読まない
        Assert.Equal(KbdLayerClient.BindingInstance, r.DeviceScopes.Single().Binding);
        Assert.Equal("MY-CUSTOM-1", r.Layouts!.Single().Name); // レイアウト名は、ファイル名から作る（大文字にする。KeyboardLayout.NameFromPath）
        Assert.False(report.HasErrors);
    }

    [Fact]
    public void Collect_WithoutDriver_KeepsFileItemsAndWarns()
    {
        WriteTray(t => t.DesktopViewMode = 2);
        var report = new PortableReport();

        var r = RecoveryExporter.Collect(null, new uint[] { 0x55F70000 }, _dir, report);

        Assert.Equal(2, r.TraySettings!.DesktopViewMode);
        Assert.Null(r.EmergencyRelease);
        Assert.Null(r.I8042);
        Assert.Null(r.DeviceScopes);
        Assert.Equal(1, report.WarningCount);
    }

    [Fact]
    public void RecoveryRoundTripsThroughJson_AndAKeyboardlessFileIsAccepted()
    {
        var file = PortableExporter.NewFile(PortableScope.All);
        file.Recovery = RecoveryExporter.Collect(new FakeDriver(), new uint[] { 0x55F70000 }, _dir, new PortableReport());

        Assert.True(PortableIO.TryParse(PortableIO.ToJson(file), out var parsed, out string? error), error);
        Assert.Equal(new ushort[] { 0x1D, 0x2A, 0x01 }, parsed!.Recovery!.EmergencyRelease);
        Assert.Equal(0x1234u, parsed.Recovery.I8042!.Profiles.Single().ModelKey);
    }

    [Fact]
    public void ApplyFiles_RestoresTrayFieldsPresentInTheFileOnly_AndLayoutsWithoutOverwriting()
    {
        WriteTray(t => { t.UiLanguageName = "Ja"; t.RemapOnlyWhileTrayRuns = false; });
        WriteLayout("existing", "OLD");
        var recovery = new PortableRecovery
        {
            TraySettings = new PortableTraySettings { DesktopViewMode = 4, ColorMode = "Dark", ImeSource = "NoSuchMode" },
            Layouts = new()
            {
                new PortableUserLayout { Name = "new-one", Json = "NEW" },
                new PortableUserLayout { Name = "existing", Json = "REPLACED" },
            },
        };
        var report = new PortableReport();

        RecoveryImporter.ApplyFiles(recovery, _dir, report);

        var tray = TrayConfig.Load(Path.Combine(_dir, "tray.json"));
        Assert.Equal(4, tray.DesktopViewMode);
        Assert.Equal("Dark", tray.ColorModeName);
        Assert.Equal("Ja", tray.UiLanguageName);         // ファイルに無い項目は、今のまま
        Assert.False(tray.RemapOnlyWhileTrayRuns);       // 同上
        Assert.NotEqual("NoSuchMode", tray.ImeSource);   // 不正な値は、読み込まない
        Assert.Equal("NEW", File.ReadAllText(Path.Combine(_dir, "Layouts", "kle-new-one.json")));
        Assert.Equal("OLD", File.ReadAllText(Path.Combine(_dir, "Layouts", "kle-existing.json")));
        Assert.False(report.HasErrors);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a..b")]
    [InlineData("")]
    [InlineData(" x")]
    public void ApplyFiles_SkipsUnsafeLayoutNames(string name)
    {
        var report = new PortableReport();
        RecoveryImporter.ApplyFiles(new PortableRecovery { Layouts = new() { new PortableUserLayout { Name = name, Json = "X" } } }, _dir, report);

        Assert.False(Directory.Exists(Path.Combine(_dir, "Layouts")) && Directory.GetFiles(Path.Combine(_dir, "Layouts"), "*", SearchOption.AllDirectories).Length > 0);
        Assert.Equal(1, report.WarningCount);
    }

    [Fact]
    public void ApplyDriver_WritesDriverItems_AndScopesOnlyForConnectedKeyboards()
    {
        var driver = new FakeDriver { Emergency = new(), I8042 = new I8042Config(false, Array.Empty<I8042Profile>()) };
        driver.Scopes.Clear();
        var recovery = new PortableRecovery
        {
            EmergencyRelease = new() { 1, 2 },
            I8042 = new PortableI8042 { Enabled = true, Profiles = new() { new PortableI8042Profile { ModelKey = 9, Type = 4, LayerJpn = "k.dll" } } },
            DeviceScopes = new()
            {
                new PortableDeviceScope { UniqId = "0x55F70000", Binding = KbdLayerClient.BindingGlobal },
                new PortableDeviceScope { UniqId = "0x00010000", Binding = KbdLayerClient.BindingInstance }, // 接続していない
                new PortableDeviceScope { UniqId = "0x55F70001", Binding = 99 },                              // 範囲外
            },
        };
        var report = new PortableReport();

        RecoveryImporter.ApplyDriver(recovery, driver, new uint[] { 0x55F70000, 0x55F70001 }, report);

        Assert.Equal(new ushort[] { 1, 2 }, driver.Emergency);
        Assert.True(driver.I8042.Enabled);
        Assert.Equal("k.dll", driver.I8042.Profiles.Single().LayerJpn);
        Assert.Equal(new[] { 0x55F70000u }, driver.Scopes.Keys);
        Assert.Equal(KbdLayerClient.BindingGlobal, driver.Scopes[0x55F70000]);
        Assert.False(report.HasErrors);
    }

    [Fact]
    public void ApplyDriver_ReportsAFailureAndContinuesWithTheOtherItems()
    {
        var driver = new FakeDriver { FailEmergency = true };
        var recovery = new PortableRecovery
        {
            EmergencyRelease = new() { 1, 2 },
            I8042 = new PortableI8042 { Enabled = true },
        };
        var report = new PortableReport();

        RecoveryImporter.ApplyDriver(recovery, driver, Array.Empty<uint>(), report);

        Assert.True(report.HasErrors);
        Assert.True(driver.I8042.Enabled); // 緊急解除が失敗しても、i8042prt は書く
    }
}

/// <summary>インストール時の取り込み（復旧用の設定を、ドライバのレジストリと設定フォルダへ戻す）。</summary>
public class RecoveryInstallImportTests : IDisposable
{
    private const string TestRoot = @"Software\kasane-tests\RecoveryInstall";
    private const string LayerMaps = TestRoot + @"\LayerMaps";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kasane-recovery-install-" + Guid.NewGuid().ToString("N"));

    public RecoveryInstallImportTests()
    {
        Registry.CurrentUser.DeleteSubKeyTree(TestRoot, throwOnMissingSubKey: false);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(TestRoot, throwOnMissingSubKey: false);
        try { Directory.Delete(_dir, true); } catch (Exception) { }
    }

    private static PersistedSettings Store() => new(Registry.CurrentUser, LayerMaps);

    private static PortableRecovery Sample() => new()
    {
        TraySettings = new PortableTraySettings { DesktopViewMode = 3, ColorMode = "Dark" },
        EmergencyRelease = new() { 0x1D, 0x2A, 0x01 },
        I8042 = new PortableI8042 { Enabled = true, Profiles = new() { new PortableI8042Profile { ModelKey = 0x1234, Type = 7, Subtype = 0, Identifier = "PCAT_106KEY", LayerJpn = "kbd106.dll" } } },
        DeviceScopes = new() { new PortableDeviceScope { UniqId = "0x55F70000", Binding = KbdLayerClient.BindingInstance } },
        Layouts = new() { new PortableUserLayout { Name = "MY-LAYOUT", Json = "[]" } },
    };

    [Fact]
    public void ApplyRecovery_WritesTheDriverFormats()
    {
        var report = new PortableReport();

        int failed = DriverImport.ApplyRecovery(Sample(), Store(), report);

        Assert.Equal(0, failed);
        using var p = Registry.CurrentUser.OpenSubKey(TestRoot)!;
        using var em = p.OpenSubKey("EmergencyRelease")!;
        Assert.Equal(3, em.GetValue("EmergencyReleaseKeyCount"));
        Assert.Equal(new byte[] { 0x1D, 0, 0x2A, 0, 0x01, 0 }, (byte[])em.GetValue("EmergencyReleaseScanCodes")!);
        var i8042 = (byte[])p.GetValue("I8042Override")!;
        Assert.Equal(8 + 16 * (16 + 3 * 32 * 2), i8042.Length);   // KBDLAYER_I8042_CONFIG 全体
        Assert.Equal(1u, BitConverter.ToUInt32(i8042, 0));         // Enabled
        Assert.Equal(1u, BitConverter.ToUInt32(i8042, 4));         // ProfileCount
        Assert.Equal(0x1234u, BitConverter.ToUInt32(i8042, 8));    // Profiles[0].ModelKey
        using var binding = p.OpenSubKey("DeviceBinding")!;
        Assert.Equal((int)KbdLayerClient.BindingInstance, binding.GetValue("55F70000"));
    }

    [Fact]
    public void ApplyRecovery_RejectsInvalidValues_AndKeepsGoing()
    {
        var recovery = new PortableRecovery
        {
            EmergencyRelease = new() { 1 },                                   // 1個は不可（2〜6個）
            I8042 = new PortableI8042 { Enabled = true, Profiles = new() { new PortableI8042Profile { ModelKey = 0 } } }, // ModelKey 0 は不可
            DeviceScopes = new() { new PortableDeviceScope { UniqId = "0x55F70000", Binding = 2 } },
        };
        var report = new PortableReport();

        int failed = DriverImport.ApplyRecovery(recovery, Store(), report);

        Assert.Equal(2, failed);
        Assert.True(report.HasErrors);
        using var p = Registry.CurrentUser.OpenSubKey(TestRoot)!;
        Assert.Null(p.OpenSubKey("EmergencyRelease"));
        Assert.Null(p.GetValue("I8042Override"));
        Assert.Equal(2, p.OpenSubKey("DeviceBinding")!.GetValue("55F70000")); // 他の項目は、失敗しても書く
    }

    [Fact]
    public void Apply_FileWithRecoveryOnly_WritesRecovery()
    {
        var file = PortableExporter.NewFile(PortableScope.All);
        file.Recovery = Sample();

        var result = DriverImport.Apply(file, Store(), new PortableReport());

        Assert.Equal(0, result.Skipped);
        using var p = Registry.CurrentUser.OpenSubKey(TestRoot)!;
        Assert.NotNull(p.GetValue("I8042Override"));
    }

    [Fact]
    public void PendingImport_AppliesRecoveryFileItemsOnlyWhereMissing()
    {
        var file = PortableExporter.NewFile(PortableScope.All);
        file.Recovery = Sample();
        PortableIO.Write(PendingImport.FilePath(_dir), file);
        Directory.CreateDirectory(Path.Combine(_dir, "Layouts"));
        File.WriteAllText(Path.Combine(_dir, "Layouts", "kle-MY-LAYOUT.json"), "KEEP");

        var first = PendingImport.Run(_dir, Array.Empty<PortableService.TargetDevice>(), new PortableReport());

        Assert.True(first.TrayImported);
        Assert.Equal(3, TrayConfig.Load(Path.Combine(_dir, "tray.json")).DesktopViewMode);
        Assert.Equal("KEEP", File.ReadAllText(Path.Combine(_dir, "Layouts", "kle-MY-LAYOUT.json"))); // 同名のレイアウトは触らない

        // tray.json ができたあとは、トレイの設定を上書きしない。
        var tray = TrayConfig.Load(Path.Combine(_dir, "tray.json"));
        tray.DesktopViewMode = 1;
        tray.Save(Path.Combine(_dir, "tray.json"));
        PendingImport.Run(_dir, Array.Empty<PortableService.TargetDevice>(), new PortableReport());
        Assert.Equal(1, TrayConfig.Load(Path.Combine(_dir, "tray.json")).DesktopViewMode);
    }

    [Fact]
    public void OnlyMissing_ReturnsNullWhenNothingIsMissing()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "Layouts"));
        File.WriteAllText(Path.Combine(_dir, "Layouts", "kle-MY-LAYOUT.json"), "x");
        Assert.Null(RecoveryImporter.OnlyMissing(Sample(), trayFileMissing: false, _dir));
        Assert.NotNull(RecoveryImporter.OnlyMissing(Sample(), trayFileMissing: true, _dir));
        Assert.Null(RecoveryImporter.OnlyMissing(null, true, _dir));
    }
}
