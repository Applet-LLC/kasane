using Kasane.Interop;
using Kasane.Layout;
using Kasane.Model;
using Kasane.Model.Portable;
using Xunit;

namespace Kasane.Tests;

public class ClearServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kasane-clear-" + Guid.NewGuid().ToString("N"));

    public ClearServiceTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static KeyboardLayout Ten => LayoutFixture.TenKey;

    private static KeymapProject Full(uint uniq)
    {
        var p = ProjectService.CreateDefault(uniq, "HID\\VID_1", Ten, true);
        p.Layers.Add(ProjectService.DefaultLayer(Ten, 1));
        p.Layers[0][0] = "MACRO(1)";
        p.LayerNames = new() { null, "仕事" };
        p.Macros.Add(new MacroDef { Id = 1, Keys = { "KC_A" } });
        p.MacroNames = new() { [1] = "あ" };
        p.Combos.Add(new ComboDef { Id = 0, Slot1 = 1, Slot2 = 2, Output = "KC_ESC" });
        p.TapDances.Add(new TapDanceDef { Id = 0, SingleTap = "KC_B" });
        p.KeyOverrides.Add(new KeyOverrideDef { Id = 3, Mods = "MOD_LSFT", Key = "KC_A", Replacement = "KC_Z" });
        p.TapTermMs = 180;
        p.ComboTermMs = 40;
        p.TapHoldMode = 1;
        p.QuickTapTermMs = 90;
        p.TappingToggle = 3;
        p.EnabledFeatures = 5;
        p.MeasuredScanCodes = new() { [0] = 0x1234 };
        return p;
    }

    private void Store(KeymapProject p, bool helper = true)
    {
        ProjectService.Save(p, _dir);
        if (helper) new HelperConfig().Save(HelperConfig.DevicePath(p.UniqId, _dir));
    }

    [Fact]
    public void ResetProject_KeyOverridesOnly_KeepsEverythingElse()
    {
        var p = Full(1);
        ClearService.ResetProject(p, Ten, new ClearOptions { KeyOverrides = true });
        Assert.Empty(p.KeyOverrides);
        Assert.Null(p.KeyOverrideNames);
        Assert.Single(p.Macros);
        Assert.Single(p.Combos);
        Assert.Single(p.TapDances);
        Assert.Equal(2, p.Layers.Count);
        Assert.Equal(180u, p.TapTermMs);
        Assert.Equal(5u, p.EnabledFeatures);
        Assert.Single(p.MeasuredScanCodes);
    }

    [Fact]
    public void ResetProject_Everything_MatchesAFreshProject()
    {
        var p = Full(1);
        ClearService.ResetProject(p, Ten, ClearOptions.Everything());
        var fresh = ProjectService.CreateDefault(1, "HID\\VID_1", Ten, true);

        Assert.Single(p.Layers);
        Assert.Equal(fresh.Layers[0], p.Layers[0]);
        Assert.Null(p.LayerNames);
        Assert.Empty(p.Macros);
        Assert.Null(p.MacroNames);
        Assert.Empty(p.Combos);
        Assert.Empty(p.TapDances);
        Assert.Empty(p.KeyOverrides);
        Assert.Equal(0u, p.TapTermMs);
        Assert.Equal(0u, p.ComboTermMs);
        Assert.Equal(0u, p.TapHoldMode);
        Assert.Equal(0u, p.QuickTapTermMs);
        Assert.Equal(0u, p.TappingToggle);
        Assert.Null(p.EnabledFeatures);
        Assert.Null(p.Nicola);
        Assert.True(p.ClearNicola); // 次の「適用」でも、ドライバの NICOLA を消す
        Assert.Empty(p.MeasuredScanCodes);
        Assert.Equal("HID\\VID_1", p.HardwareId); // 識別は残す
    }

    [Fact]
    public void ResetProject_Layers_NeedsALayout() =>
        Assert.Throws<ArgumentNullException>(() => ClearService.ResetProject(Full(1), null, new ClearOptions { Layers = true }));

    [Fact]
    public void ResetProject_WithoutLayers_DoesNotNeedALayout()
    {
        var p = Full(1);
        ClearService.ResetProject(p, null, new ClearOptions { Macros = true });
        Assert.Empty(p.Macros);
    }

    [Fact]
    public void Clear_WithNothingSelected_DoesNothing()
    {
        Store(Full(1));
        var report = new PortableReport();
        var cleared = ClearService.Clear(new uint[] { 1 }, new ClearOptions(), null, false, _dir, report);
        Assert.Empty(cleared);
        Assert.Single(ProjectService.TryLoad(1, _dir)!.Macros);
        Assert.False(Directory.Exists(Path.Combine(_dir, "backup")));
    }

    [Fact]
    public void Clear_SelectedKeyboardsAndCategories_OnlyThose_AndBacksUpFirst()
    {
        Store(Full(1));
        Store(Full(2));
        var report = new PortableReport();

        var cleared = ClearService.Clear(new uint[] { 1 }, new ClearOptions { Combos = true, KeyOverrides = true }, null, false, _dir, report);

        Assert.Equal(new uint[] { 1 }, cleared);
        Assert.False(report.HasErrors, report.ToString());
        var one = ProjectService.TryLoad(1, _dir)!;
        Assert.Empty(one.Combos);
        Assert.Empty(one.KeyOverrides);
        Assert.Single(one.Macros);                         // 選ばなかった項目は残る
        var two = ProjectService.TryLoad(2, _dir)!;
        Assert.Single(two.Combos);                         // 選ばなかったキーボードは触らない
        Assert.Single(two.KeyOverrides);
        string[] backups = Directory.GetFiles(Path.Combine(_dir, "backup"), "before-clear-*.kasane.json");
        Assert.Single(backups);
    }

    [Fact]
    public void Clear_Helper_DeletesOnlyThatKeyboardsHelperJson()
    {
        Store(Full(1));
        Store(Full(2));
        ClearService.Clear(new uint[] { 1 }, new ClearOptions { Helper = true }, null, false, _dir, new PortableReport());
        Assert.False(File.Exists(HelperConfig.DevicePath(1, _dir)));
        Assert.True(File.Exists(HelperConfig.DevicePath(2, _dir)));
        Assert.Single(ProjectService.TryLoad(1, _dir)!.Macros); // プロジェクトは触らない
    }

    [Fact]
    public void Clear_AllKeyboards_AlsoClearsTheTraysSharedHelper()
    {
        Store(Full(1));
        new TrayConfig { Helper = new HelperConfig() }.Save(PortableService.TrayPath(_dir));
        ClearService.Clear(new uint[] { 1 }, new ClearOptions { Helper = true }, null, includeTrayHelper: true, _dir, new PortableReport());
        Assert.Null(TrayConfig.Load(PortableService.TrayPath(_dir)).Helper);

        // 「このキーボード」だけのときは、共通のヘルパー設定は残す。
        new TrayConfig { Helper = new HelperConfig() }.Save(PortableService.TrayPath(_dir));
        ClearService.Clear(new uint[] { 1 }, new ClearOptions { Helper = true }, null, includeTrayHelper: false, _dir, new PortableReport());
        Assert.NotNull(TrayConfig.Load(PortableService.TrayPath(_dir)).Helper);
    }

    [Fact]
    public void Clear_Nicola_WithoutDriver_KeepsClearNicolaForTheNextApply()
    {
        var p = Full(1);
        Store(p);
        ClearService.Clear(new uint[] { 1 }, new ClearOptions { Nicola = true }, null, false, _dir, new PortableReport());
        Assert.True(ProjectService.TryLoad(1, _dir)!.ClearNicola); // ドライバを消せていないので、次の適用で消す
    }

    [Fact]
    public void Names_ListsEveryCategory()
    {
        Assert.Equal(10, ClearOptions.Everything().Names().Count);
        Assert.Empty(new ClearOptions().Names());
        Assert.True(ClearOptions.Everything().Any);
        Assert.False(new ClearOptions().Any);
    }
}
