using KbdLayerKeymapGui.Interop;
using KbdLayerKeymapGui.Keycodes;
using KbdLayerKeymapGui.Model;

namespace KbdLayerKeymapGui.Tests;

public class ProjectServiceKeyOverrideTests
{
    [Fact]
    public void ValidOverride_ProducesEntry()
    {
        var errors = new List<string>();
        var entry = ProjectService.KeyOverrideToEntry(
            new KeyOverrideDef { Id = 0, Mods = "MOD_LSFT", Key = "KC_BSPACE", Replacement = "KC_DELETE" }, errors);

        Assert.Empty(errors);
        Assert.NotNull(entry);
        Assert.Equal(0x02, entry!.TriggerMods);
        Assert.True(KeycodeTable.TryResolve("KC_BSPACE", out var bs));
        Assert.Equal(bs.Param2, entry.TriggerScanCode);
        Assert.Equal(ActionType.Key, entry.Replacement.Type);
    }

    [Fact]
    public void MultipleModsAndModdedReplacement_AreAccepted()
    {
        var errors = new List<string>();
        var entry = ProjectService.KeyOverrideToEntry(
            new KeyOverrideDef { Id = 1, Mods = "MOD_LCTL|MOD_LSFT", Key = "KC_Z", Replacement = "LCTL(KC_Y)" }, errors);

        Assert.Empty(errors);
        Assert.NotNull(entry);
        Assert.Equal(0x03, entry!.TriggerMods);
        Assert.Equal(ActionType.ModdedKey, entry.Replacement.Type);
    }

    [Theory]
    [InlineData("", "KC_A", "KC_B", "修飾キー")]
    [InlineData("SHIFT", "KC_A", "KC_B", "修飾キー")]
    [InlineData("MOD_LSFT", "MO(1)", "KC_B", "キー「")]
    [InlineData("MOD_LSFT", "KC_A", "MACRO(0)", "置き換え先")]
    [InlineData("MOD_LSFT", "KC_A", "KC_NO", "置き換え先")]
    public void InvalidParts_AreRejected(string mods, string key, string replacement, string expectedField)
    {
        var errors = new List<string>();
        var entry = ProjectService.KeyOverrideToEntry(
            new KeyOverrideDef { Id = 2, Mods = mods, Key = key, Replacement = replacement }, errors);

        Assert.Null(entry);
        Assert.Contains(errors, e => e.Contains(expectedField));
    }
}
