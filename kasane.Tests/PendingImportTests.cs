using Kasane.Layout;
using Kasane.Model;
using Kasane.Model.Portable;

namespace Kasane.Tests;

/// <summary>保存先（KASANE のフォルダ）の kasane-import.json を、設定が無いものだけ取り込む。</summary>
public class PendingImportTests : IDisposable
{
    private const string Hw = @"HID\VID_04FE&PID_0016&REV_0224&MI_00";
    private const uint Uniq = 0xCDEC0000;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kasane-pending-" + Guid.NewGuid().ToString("N"));

    public PendingImportTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static KeyboardLayout Ten => LayoutFixture.TenKey;
    private static KeyboardLayout? Resolve(string? _) => Ten;
    private static int SlotOf(string keycode) => Ten.Slots.First(s => s.DefaultKeycode == keycode).Index;

    private static IReadOnlyList<PortableService.TargetDevice> Connected(uint uniq = Uniq) =>
        new[] { new PortableService.TargetDevice(uniq, Hw, uniq.ToString("X8")) };

    private void WriteImportFile(bool withTray = true, string output = "KC_ESC")
    {
        var p = ProjectService.CreateDefault(0x11110000, Hw, Ten, true);
        p.Layers[0][SlotOf("KC_KP_7")] = "KC_A";
        p.TapTermMs = 175;
        var file = PortableExporter.NewFile(PortableScope.All);
        file.Keyboards.Add(PortableExporter.ExportKeyboard(p, Ten, null, new ExportOptions(), new PortableReport()));
        file.Keyboards[0].HardwareId = Hw;
        if (withTray) file.Tray = new PortableTray { ImeLink = true };
        PortableIO.Write(PendingImport.FilePath(_dir), file);
    }

    [Fact]
    public void NoFile_DoesNothing()
    {
        var r = PendingImport.Run(_dir, Connected(), new PortableReport(), Resolve);
        Assert.False(r.FileFound);
    }

    [Fact]
    public void ImportsOnlyWhatIsUnset_ProjectAndTray()
    {
        WriteImportFile();
        var report = new PortableReport();
        var r = PendingImport.Run(_dir, Connected(), report, Resolve);

        Assert.False(report.HasErrors, report.ToString());
        Assert.Equal(1, r.KeyboardsImported);
        Assert.True(r.TrayImported);
        var project = ProjectService.TryLoad(Uniq, _dir)!;
        Assert.Equal(175u, project.TapTermMs);
        Assert.True(TrayConfig.Load(PortableService.TrayPath(_dir)).ImeLink);
    }

    [Fact]
    public void ExistingSettings_AreNotOverwritten()
    {
        var existing = ProjectService.CreateDefault(Uniq, Hw, Ten, true);
        existing.TapTermMs = 999;
        ProjectService.Save(existing, _dir);
        new TrayConfig { ImeLink = false }.Save(PortableService.TrayPath(_dir));

        WriteImportFile();
        var r = PendingImport.Run(_dir, Connected(), new PortableReport(), Resolve);

        Assert.Equal(0, r.KeyboardsImported);
        Assert.False(r.TrayImported);
        Assert.Equal(999u, ProjectService.TryLoad(Uniq, _dir)!.TapTermMs);
        Assert.False(TrayConfig.Load(PortableService.TrayPath(_dir)).ImeLink);
    }

    [Fact]
    public void KeyboardNotConnected_IsLeftForLater()
    {
        WriteImportFile(withTray: false);
        var r = PendingImport.Run(_dir, Array.Empty<PortableService.TargetDevice>(), new PortableReport(), Resolve);
        Assert.Equal(0, r.KeyboardsImported);
        Assert.Null(ProjectService.TryLoad(Uniq, _dir));
        // 接続されたあとの実行で取り込まれる。
        Assert.Equal(1, PendingImport.Run(_dir, Connected(), new PortableReport(), Resolve).KeyboardsImported);
    }

    [Fact]
    public void SecondRun_IsIdempotent()
    {
        WriteImportFile();
        PendingImport.Run(_dir, Connected(), new PortableReport(), Resolve);
        var again = PendingImport.Run(_dir, Connected(), new PortableReport(), Resolve);
        Assert.Equal(0, again.KeyboardsImported);
        Assert.False(again.TrayImported);
    }

    [Fact]
    public void UnreadableFile_IsReportedAndSkipped()
    {
        File.WriteAllText(PendingImport.FilePath(_dir), "{ not json");
        var report = new PortableReport();
        var r = PendingImport.Run(_dir, Connected(), report, Resolve);
        Assert.True(r.FileFound);
        Assert.True(report.HasErrors);
        Assert.Equal(0, r.KeyboardsImported);
    }
}
