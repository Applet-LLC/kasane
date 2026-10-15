using Kasane.Interop;
using Kasane.Keycodes;
using Kasane.Model;

namespace Kasane.Tests;

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

/// <summary>キー置換の置き換え先に MACRO(n) を指定する（2026-10-15）。</summary>
public class KeyOverrideMacroTests
{
    private static KeyOverrideEntry? Convert(string replacement, bool repeat, List<string> errors) =>
        ProjectService.KeyOverrideToEntry(
            new KeyOverrideDef { Id = 3, Mods = "MOD_LCTL", Key = "KC_F1", Replacement = replacement, RepeatMacro = repeat }, errors);

    [Fact]
    public void MacroReplacement_IsAccepted_WithoutRepeatByDefault()
    {
        var errors = new List<string>();
        var entry = Convert("MACRO(5)", repeat: false, errors);

        Assert.Empty(errors);
        Assert.NotNull(entry);
        Assert.Equal(ActionType.Macro, entry!.Replacement.Type);
        Assert.Equal(5, entry.Replacement.Param1);
        Assert.Equal(0, entry.Flags);
    }

    [Fact]
    public void RepeatMacro_SetsTheRepeatFlag()
    {
        var errors = new List<string>();
        var entry = Convert("MACRO(5)", repeat: true, errors);

        Assert.Empty(errors);
        Assert.Equal(KeyOverrideEntry.FlagRepeat, entry!.Flags);
    }

    [Fact]
    public void RepeatMacro_IsIgnoredWhenReplacementIsNotAMacro()
    {
        var errors = new List<string>();
        var entry = Convert("KC_A", repeat: true, errors);

        Assert.Empty(errors);
        Assert.Equal(0, entry!.Flags); // ドライバは、マクロ以外のFLAG_REPEATを拒否するため
    }

    [Theory]
    [InlineData("MACRO(128)")]
    [InlineData("MACRO(x)")]
    public void OutOfRangeMacro_IsRejected(string replacement)
    {
        var errors = new List<string>();
        Assert.Null(Convert(replacement, repeat: false, errors));
        Assert.Contains(errors, e => e.Contains("置き換え先"));
    }

    [Fact]
    public void FlagsArePersistedInTheBlob()
    {
        var blob = PersistedSettings.SerializeKeyOverrides(new Dictionary<int, KeyOverrideEntry?>
        {
            [0] = new KeyOverrideEntry(0x01, 0x003B, new KeyAction(ActionType.Macro, 2, 0), KeyOverrideEntry.FlagRepeat),
        });
        Assert.Equal(0x01, blob[4]);
        Assert.Equal(KeyOverrideEntry.FlagRepeat, blob[5]);
        Assert.Equal(0x003B, BitConverter.ToUInt16(blob, 6));
    }

    [Fact]
    public void RepeatMacro_IsSavedOnlyWhenSet()
    {
        var plain = System.Text.Json.JsonSerializer.Serialize(new KeyOverrideDef { Id = 0, Mods = "MOD_LCTL", Key = "KC_F1", Replacement = "MACRO(1)" });
        Assert.DoesNotContain("RepeatMacro", plain);

        var repeat = System.Text.Json.JsonSerializer.Serialize(new KeyOverrideDef { Id = 0, Mods = "MOD_LCTL", Key = "KC_F1", Replacement = "MACRO(1)", RepeatMacro = true });
        Assert.Contains("\"RepeatMacro\":true", repeat);
    }
}
