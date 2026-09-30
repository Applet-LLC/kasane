using KbdLayerKeymapGui.Model;

namespace KbdLayerKeymapGui.Tests;

public class ProjectServiceComboTests
{
    private static KeymapProject EmptyProject() => new() { UniqId = 0x12345678, LayoutName = "10key" };

    [Fact]
    public void ValidCombo_ResolvesToScanCodesAndOutput()
    {
        var project = EmptyProject();
        var layout = LayoutFixture.TenKey;
        var combo = new ComboDef { Id = 0, Slot1 = 0, Slot2 = 1, Output = "KC_ESC" };
        var errors = new List<string>();

        var entry = ProjectService.ComboToEntry(project, layout, combo, errors);

        Assert.Empty(errors);
        Assert.NotNull(entry);
        Assert.Equal(layout.Slots[0].DefaultScanCode!.Value, entry!.ScanCode1);
        Assert.Equal(layout.Slots[1].DefaultScanCode!.Value, entry.ScanCode2);
    }

    [Fact]
    public void SameSlotTwice_IsRejected()
    {
        var project = EmptyProject();
        var layout = LayoutFixture.TenKey;
        var combo = new ComboDef { Id = 0, Slot1 = 3, Slot2 = 3, Output = "KC_ESC" };
        var errors = new List<string>();

        var entry = ProjectService.ComboToEntry(project, layout, combo, errors);

        Assert.Null(entry);
        Assert.Single(errors);
        Assert.Contains("別のキーにしてください", errors[0]);
    }

    [Theory]
    [InlineData("MO(1)")]
    [InlineData("LCTL(KC_C)")]  // combo output must be a plain KEY, unlike macro/tap-dance slots
    [InlineData("MACRO(0)")]
    public void NonPlainKeyOutput_IsRejected(string badOutput)
    {
        var project = EmptyProject();
        var layout = LayoutFixture.TenKey;
        var combo = new ComboDef { Id = 0, Slot1 = 0, Slot2 = 1, Output = badOutput };
        var errors = new List<string>();

        var entry = ProjectService.ComboToEntry(project, layout, combo, errors);

        Assert.Null(entry);
        Assert.Single(errors);
        Assert.Contains("単純なキー", errors[0]);
    }

    [Fact]
    public void SlotOutOfRange_IsRejected()
    {
        var project = EmptyProject();
        var layout = LayoutFixture.TenKey;
        var combo = new ComboDef { Id = 0, Slot1 = 0, Slot2 = 999, Output = "KC_ESC" };
        var errors = new List<string>();

        var entry = ProjectService.ComboToEntry(project, layout, combo, errors);

        Assert.Null(entry);
        Assert.Single(errors);
        Assert.Contains("レイアウトにありません", errors[0]);
    }

    [Fact]
    public void MeasuredScanCode_OverridesDefaultScanCode()
    {
        var project = EmptyProject();
        var layout = LayoutFixture.TenKey;
        // Force slot0's effective scan code to collide with slot1's default, to prove
        // ComboToEntry reads EffectiveScanCode (measured-or-default), not just the default.
        project.MeasuredScanCodes[0] = layout.Slots[1].DefaultScanCode!.Value;
        var combo = new ComboDef { Id = 0, Slot1 = 0, Slot2 = 1, Output = "KC_ESC" };
        var errors = new List<string>();

        var entry = ProjectService.ComboToEntry(project, layout, combo, errors);

        Assert.Null(entry);
        Assert.Contains(errors, e => e.Contains("別のキーにしてください"));
    }
}
