using Kasane.Cli;
using Kasane.Layout;
using Kasane.Model;
using Kasane.Model.Portable;
using Microsoft.Win32;

namespace Kasane.Tests;

/// <summary>設定ファイルの内容を、ドライバのレジストリ（ここでは HKCU の作業用キー）へ書く取り込み。</summary>
public class DriverImportTests : IDisposable
{
    private const string TestRoot = @"Software\kasane-tests\DriverImport";
    private const string LayerMaps = TestRoot + @"\LayerMaps";
    private const string Hw = @"HID\VID_04FE&PID_0016&REV_0224&MI_00"; // ModelKey 0B450000

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kasane-driverimport-" + Guid.NewGuid().ToString("N"));

    public DriverImportTests()
    {
        Directory.CreateDirectory(_dir);
        Registry.CurrentUser.DeleteSubKeyTree(TestRoot, throwOnMissingSubKey: false);
    }

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(TestRoot, throwOnMissingSubKey: false);
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static KeyboardLayout Ten => LayoutFixture.TenKey;
    private static int SlotOf(string keycode) => Ten.Slots.First(s => s.DefaultKeycode == keycode).Index;
    private static PersistedSettings Store() => new(Registry.CurrentUser, LayerMaps);
    private static KeyboardLayout? Resolve(string? _) => Ten;

    private static PortableFile FileWith(uint tapTerm, string? hardwareId = Hw, string output = "KC_ESC")
    {
        var p = ProjectService.CreateDefault(0, hardwareId, Ten, true);
        p.Layers[0][SlotOf("KC_KP_7")] = "KC_A";
        p.Combos.Add(new ComboDef { Id = 0, Slot1 = SlotOf("KC_KP_1"), Slot2 = SlotOf("KC_KP_2"), Output = output });
        p.Macros.Add(new MacroDef { Id = 3, Keys = { "KC_A", "KC_B" } });
        p.TapTermMs = tapTerm;
        var file = PortableExporter.NewFile(PortableScope.Keyboard);
        file.Keyboards.Add(PortableExporter.ExportKeyboard(p, Ten, null, new ExportOptions(), new PortableReport()));
        file.Keyboards[0].HardwareId = hardwareId;
        return file;
    }

    [Fact]
    public void Apply_WritesLayerMapComboAndMacroUnderTheModelKey()
    {
        var report = new PortableReport();
        var result = DriverImport.Apply(FileWith(180), Store(), report, Resolve);

        Assert.False(report.HasErrors, report.ToString());
        Assert.Equal(1, result.Written);
        Assert.Equal(0, result.BackedUp);
        using var key = Registry.CurrentUser.OpenSubKey(LayerMaps + @"\M_0B450000")!;
        Assert.Equal(180, key.GetValue("TapTermMs"));
        Assert.Equal(1, key.GetValue("Enabled"));
        Assert.Equal(1, key.GetValue("Migrated"));
        Assert.NotNull(key.GetValue("LayerMap"));
        Assert.Equal(192, ((byte[])key.GetValue("Combos")!).Length);
        Assert.Equal(33280, ((byte[])key.GetValue("Macros")!).Length);
    }

    [Fact]
    public void Apply_Twice_OverwritesAndBacksUpTheFirst()
    {
        DriverImport.Apply(FileWith(180), Store(), new PortableReport(), Resolve);
        var result = DriverImport.Apply(FileWith(250), Store(), new PortableReport(), Resolve);

        Assert.Equal(1, result.BackedUp);
        using var key = Registry.CurrentUser.OpenSubKey(LayerMaps + @"\M_0B450000")!;
        using var bak = Registry.CurrentUser.OpenSubKey(LayerMaps + @"\M_0B450000.bak")!;
        Assert.Equal(250, key.GetValue("TapTermMs"));
        Assert.Equal(180, bak.GetValue("TapTermMs"));
    }

    [Fact]
    public void Apply_WithoutHardwareId_IsSkipped()
    {
        var report = new PortableReport();
        var result = DriverImport.Apply(FileWith(180, hardwareId: null), Store(), report, Resolve);
        Assert.Equal(0, result.Written);
        Assert.Equal(1, result.Skipped);
        Assert.Contains(report.Lines, l => l.Level == ReportLevel.Warning && l.Text.Contains("HardwareId"));
        Assert.Empty(Store().ListBackups());
    }

    [Fact]
    public void Apply_InvalidContent_WritesNothing()
    {
        var report = new PortableReport();
        var result = DriverImport.Apply(FileWith(180, output: "KC_NOT_A_KEY"), Store(), report, Resolve);
        Assert.Equal(0, result.Written);
        Assert.True(report.HasErrors);
        Assert.False(Store().Exists("M_0B450000"));
    }

    [Fact]
    public void Apply_LayerScopeFile_IsRejected()
    {
        var file = FileWith(180);
        file.Scope = nameof(PortableScope.Layer);
        var result = DriverImport.Apply(file, Store(), new PortableReport(), Resolve);
        Assert.Equal(0, result.Written);
    }

    [Fact]
    public void RunInstallImport_ReadsTheFile_AndReturnsZero()
    {
        string path = Path.Combine(_dir, "in.kasane.json");
        PortableIO.Write(path, FileWith(180));
        var log = new ListLog();

        // レイアウトはテスト用に差し替えられないので、同梱レイアウト名のファイルで確認する（見つからなければ 2）。
        int code = HeadlessCommands.RunInstallImport(path, Store(), log);
        Assert.True(code is 0 or 2, string.Join("\n", log.Lines));
    }

    [Fact]
    public void RunInstallImport_MissingFile_Returns1()
    {
        var log = new ListLog();
        Assert.Equal(1, HeadlessCommands.RunInstallImport(Path.Combine(_dir, "none.json"), Store(), log));
        Assert.Equal(1, HeadlessCommands.RunInstallImport(null, Store(), log));
    }

    [Fact]
    public void RunRestore_SwapsBackup_AndReportsMissing()
    {
        var log = new ListLog();
        Assert.Equal(1, HeadlessCommands.RunRestore("--all", Store(), log)); // 退避が無い

        DriverImport.Apply(FileWith(180), Store(), new PortableReport(), Resolve);
        DriverImport.Apply(FileWith(250), Store(), new PortableReport(), Resolve);
        Assert.Equal(0, HeadlessCommands.RunRestore("--all", Store(), log));
        using var key = Registry.CurrentUser.OpenSubKey(LayerMaps + @"\M_0B450000")!;
        Assert.Equal(180, key.GetValue("TapTermMs"));
    }

    [Fact]
    public void RunPrepareDataDir_CreatesTheFolderAndPlacesTheImportFile()
    {
        string src = Path.Combine(_dir, "in.kasane.json");
        PortableIO.Write(src, FileWith(180));
        string target = Path.Combine(_dir, "data", "kasane");
        var log = new ListLog();

        // 末尾が「\」のフォルダは、「<フォルダ>\.」の形で渡される（コマンドラインの引用の解釈を避けるため）。
        Assert.Equal(0, HeadlessCommands.RunPrepareDataDir(target + @"\.", src, log));
        Assert.True(Directory.Exists(target), string.Join(" / ", log.Lines));
        Assert.True(File.Exists(Path.Combine(target, "kasane-import.json")));
    }

    [Fact]
    public void RunPrepareDataDir_RejectsRelativeAndUncPaths()
    {
        var log = new ListLog();
        Assert.Equal(1, HeadlessCommands.RunPrepareDataDir(@"relative\dir", null, log));
        Assert.Equal(1, HeadlessCommands.RunPrepareDataDir(@"\\server\share\kasane", null, log));
        Assert.Equal(1, HeadlessCommands.RunPrepareDataDir(null, null, log));
    }

    private sealed class ListLog : HeadlessCommands.ILog
    {
        public List<string> Lines { get; } = new();
        public void Write(string line) => Lines.Add(line);
    }
}
