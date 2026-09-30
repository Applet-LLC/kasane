using KbdLayerKeymapGui.Model;

namespace KbdLayerKeymapGui.Tests;

public class ProjectServiceLayerTests
{
    [Fact]
    public void CreateDefault_FillsLayer0WithEachSlotsDefaultKeycode()
    {
        var layout = LayoutFixture.TenKey;
        var project = ProjectService.CreateDefault(0x12345678, "HID\\VID_TEST", layout, bundled: true);

        Assert.Single(project.Layers);
        Assert.Equal(layout.Slots.Count, project.Layers[0].Count);
        Assert.Equal("KC_NUMLOCK", project.Layers[0][0]);
        Assert.Null(project.LayoutPath); // bundled layouts are referenced by name only
        Assert.Equal(layout.Name, project.LayoutName);
    }

    [Fact]
    public void DefaultLayer_NonZeroLayerIsAllTransparent()
    {
        var layout = LayoutFixture.TenKey;
        var layer1 = ProjectService.DefaultLayer(layout, 1);
        Assert.All(layer1, token => Assert.Equal("KC_TRNS", token));
    }

    [Fact]
    public void NormalizeShape_PadsShortLayersAndTrimsLongOnes()
    {
        var layout = LayoutFixture.TenKey;
        var project = new KeymapProject { UniqId = 1, LayoutName = layout.Name };
        project.Layers.Add(new List<string> { "KC_A" }); // too short
        project.Layers.Add(Enumerable.Repeat("KC_B", layout.Slots.Count + 5).ToList()); // too long
        project.MeasuredScanCodes[layout.Slots.Count + 3] = 0x1234; // out-of-range slot, should be dropped

        ProjectService.NormalizeShape(project, layout);

        Assert.Equal(layout.Slots.Count, project.Layers[0].Count);
        Assert.Equal(layout.Slots.Count, project.Layers[1].Count);
        Assert.Empty(project.MeasuredScanCodes);
    }

    [Fact]
    public void ResolveMappedSlots_IncludesSlotsWithARealKeycodeOnAnyLayer()
    {
        var layout = LayoutFixture.TenKey;
        var project = ProjectService.CreateDefault(1, null, layout, bundled: true);
        var warnings = new List<string>();
        var errors = new List<string>();

        // layer0 (from CreateDefault) has every slot's own default keycode -- non-transparent
        // everywhere, so every slot should be included.
        var mapped = ProjectService.ResolveMappedSlots(project, layout, warnings, errors);

        Assert.Equal(layout.Slots.Count, mapped.Count);
        Assert.Empty(warnings);
        Assert.Empty(errors);
    }

    [Fact]
    public void ResolveMappedSlots_OmitsASlotThatIsTransparentOnEveryLayer()
    {
        var layout = LayoutFixture.TenKey;
        var project = new KeymapProject { UniqId = 1, LayoutName = layout.Name };
        // Two layers, all slots KC_TRNS except slot0 on layer0 -- only slot0 should be mapped;
        // every other slot is transparent on every layer and passes through untouched.
        var layer0 = ProjectService.DefaultLayer(layout, 0).Select((_, i) => i == 0 ? "KC_ESC" : "KC_TRNS").ToList();
        var layer1 = ProjectService.DefaultLayer(layout, 1);
        project.Layers.Add(layer0);
        project.Layers.Add(layer1);

        var warnings = new List<string>();
        var errors = new List<string>();
        var mapped = ProjectService.ResolveMappedSlots(project, layout, warnings, errors);

        Assert.Single(mapped);
        Assert.Equal(0, mapped[0].Slot.Index);
    }

    [Fact]
    public void ResolveMappedSlots_DuplicateScanCode_ReportsError()
    {
        var layout = LayoutFixture.TenKey;
        var project = ProjectService.CreateDefault(1, null, layout, bundled: true);
        // Force slot1's effective scan code to collide with slot0's.
        project.MeasuredScanCodes[1] = layout.Slots[0].DefaultScanCode!.Value;

        var warnings = new List<string>();
        var errors = new List<string>();
        var mapped = ProjectService.ResolveMappedSlots(project, layout, warnings, errors);

        Assert.Contains(errors, e => e.Contains("同じスキャンコード"));
        // slot0 wins (first seen); slot1 is excluded from the mapped result.
        Assert.DoesNotContain(mapped, m => m.Slot.Index == 1);
    }
}
