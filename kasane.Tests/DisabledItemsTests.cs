using System.Text.Json;
using Kasane.Layout;
using Kasane.Model;
using Kasane.Model.Portable;
using Xunit;

namespace Kasane.Tests;

/// <summary>マクロ・コンボ・タップダンス・キー置換の「無効」（定義は残すが、キーボードへ適用しない）。</summary>
public class DisabledItemsTests
{
    private static readonly KeyboardLayout Layout = LayoutFixture.TenKey;

    private static KeymapProject NewProject() => ProjectService.CreateDefault(0xCDEC0010, "HID\\VID_T", Layout, bundled: true);

    private static KeymapProject Sample(bool disabled)
    {
        var p = NewProject();
        p.Macros.Add(new MacroDef { Id = 1, Keys = new List<string> { "KC_A", "KC_B" }, Disabled = disabled });
        p.Combos.Add(new ComboDef { Id = 2, Slot1 = 0, Slot2 = 1, Output = "KC_ESC", Disabled = disabled });
        p.TapDances.Add(new TapDanceDef { Id = 3, SingleTap = "KC_A", DoubleTap = "KC_B", Disabled = disabled });
        p.KeyOverrides.Add(new KeyOverrideDef { Id = 4, Mods = "MOD_LSFT", Key = "KC_2", Replacement = "KC_3", Disabled = disabled });
        return p;
    }

    [Fact]
    public void Compile_SendsEnabledItemsOnly()
    {
        var on = ProjectService.Compile(Sample(disabled: false), Layout);
        Assert.Contains(1, on.Macros.Keys);
        Assert.Contains(2, on.Combos.Keys);
        Assert.Contains(3, on.TapDances.Keys);
        Assert.Contains(4, on.KeyOverrides.Keys);

        var off = ProjectService.Compile(Sample(disabled: true), Layout);
        Assert.Empty(off.Macros);
        Assert.Empty(off.Combos);
        Assert.Empty(off.TapDances);
        Assert.Empty(off.KeyOverrides);
    }

    [Fact]
    public void Compile_DoesNotValidateDisabledItems()
    {
        var bad = NewProject();
        bad.Macros.Add(new MacroDef { Id = 0, Keys = new List<string> { "NOT_A_KEY" }, Disabled = true });
        bad.TapDances.Add(new TapDanceDef { Id = 0, SingleTap = "NOT_A_KEY", Disabled = true });
        bad.KeyOverrides.Add(new KeyOverrideDef { Id = 0, Mods = "MOD_LSFT", Key = "NOT_A_KEY", Replacement = "KC_A", Disabled = true });
        Assert.Empty(ProjectService.Compile(bad, Layout).Macros);

        // 有効にすると、検証される。
        bad.Macros[0].Disabled = false;
        Assert.Throws<InvalidOperationException>(() => ProjectService.Compile(bad, Layout));
    }

    [Fact]
    public void KeyAssignedToADisabledMacro_WarnsThatItIsDisabled()
    {
        var p = NewProject();
        p.Layers[0][0] = "MACRO(1)";
        p.Layers[0][1] = "MACRO(2)";
        p.Macros.Add(new MacroDef { Id = 1, Keys = new List<string> { "KC_A" }, Disabled = true });

        var warnings = ProjectService.Compile(p, Layout).Warnings;

        Assert.Contains(warnings, w => w.Contains("MACRO(1)") && w.Contains("無効"));
        Assert.Contains(warnings, w => w.Contains("MACRO(2)") && w.Contains("未設定"));   // 定義が無いものは、今までどおり
    }

    [Fact]
    public void KeyOverrideToADisabledMacro_Warns()
    {
        var p = NewProject();
        p.Macros.Add(new MacroDef { Id = 5, Keys = new List<string> { "KC_A" }, Disabled = true });
        p.KeyOverrides.Add(new KeyOverrideDef { Id = 0, Mods = "MOD_LSFT", Key = "KC_2", Replacement = "MACRO(5)" });

        var warnings = ProjectService.Compile(p, Layout).Warnings;

        Assert.Contains(warnings, w => w.Contains("キー置換0") && w.Contains("無効"));
    }

    [Fact]
    public void DisabledCombo_FreesItsMemberKeysForAnotherCombo()
    {
        var p = NewProject();
        p.Combos.Add(new ComboDef { Id = 0, Slot1 = 0, Slot2 = 1, Output = "KC_ESC", Disabled = true });
        p.Combos.Add(new ComboDef { Id = 1, Slot1 = 0, Slot2 = 2, Output = "KC_ENTER" });

        var settings = ProjectService.Compile(p, Layout);

        Assert.Equal(new[] { 1 }, settings.Combos.Keys);
    }

    [Fact]
    public void KeyMarks_SkipDisabledComboAndKeyOverride_AndTagDisabledMacro()
    {
        var p = NewProject();
        p.Layers[0][0] = "MACRO(1)";
        p.Layers[0][2] = "KC_A";
        p.Macros.Add(new MacroDef { Id = 1, Keys = new List<string> { "KC_A" }, Disabled = true });
        p.Combos.Add(new ComboDef { Id = 0, Slot1 = 0, Slot2 = 1, Output = "KC_ESC", Disabled = true });
        p.KeyOverrides.Add(new KeyOverrideDef { Id = 0, Mods = "MOD_LSFT", Key = "KC_A", Replacement = "KC_B", Disabled = true });

        var slot0 = KeyMarks.For(p, Layout, 0, Layout.Slots.First(s => s.Index == 0));
        Assert.Equal("M", slot0.Marks);                 // 割り当ては MACRO(1) のまま。C は付かない
        Assert.Contains("無効", slot0.ToolTip);
        Assert.Equal("", KeyMarks.For(p, Layout, 0, Layout.Slots.First(s => s.Index == 2)).Marks);   // R は付かない
    }

    [Theory]
    [InlineData(2, 0, true, "マクロ(2)...")]
    [InlineData(3, 1, true, "マクロ(2・無効1)...")]
    [InlineData(1, 1, true, "マクロ(無効1)...")]
    [InlineData(3, 1, false, "マクロ(2・無効1・OFF)...")]
    public void ButtonText_ShowsDisabledCount(int count, int disabled, bool on, string expected) =>
        Assert.Equal(expected, KeyMarks.ButtonText("マクロ", count, on, disabled));

    [Fact]
    public void ProjectJson_OmitsDisabledWhenFalse_AndKeepsItWhenTrue()
    {
        Assert.DoesNotContain("Disabled", JsonSerializer.Serialize(Sample(disabled: false)));
        string json = JsonSerializer.Serialize(Sample(disabled: true));
        var back = JsonSerializer.Deserialize<KeymapProject>(json)!;
        Assert.True(back.Macros[0].Disabled);
        Assert.True(back.Combos[0].Disabled);
        Assert.True(back.TapDances[0].Disabled);
        Assert.True(back.KeyOverrides[0].Disabled);
    }

    [Fact]
    public void ProjectJson_OldFileWithoutDisabled_LoadsAsEnabled()
    {
        var back = JsonSerializer.Deserialize<KeymapProject>(JsonSerializer.Serialize(Sample(disabled: false)))!;
        Assert.False(back.Macros[0].Disabled);
        Assert.False(back.KeyOverrides[0].Disabled);
    }

    [Fact]
    public void PortableExportImport_KeepsDisabled()
    {
        var src = Sample(disabled: true);
        var kb = PortableExporter.ExportKeyboard(src, Layout, null, new ExportOptions(), new PortableReport());
        var target = ProjectService.CreateDefault(0xCDEC0011, "HID\\VID_T2", Layout, bundled: true);

        var res = PortableImporter.Import(kb, target, Layout, null, null, new ImportOptions(), layerScope: false);

        Assert.False(res.Report.HasErrors);
        Assert.True(target.Macros.Single().Disabled);
        Assert.True(target.Combos.Single().Disabled);
        Assert.True(target.TapDances.Single().Disabled);
        Assert.True(target.KeyOverrides.Single().Disabled);
    }
}
