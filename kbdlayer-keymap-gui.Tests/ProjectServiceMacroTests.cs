using KbdLayerKeymapGui.Interop;
using KbdLayerKeymapGui.Keycodes;
using KbdLayerKeymapGui.Model;

namespace KbdLayerKeymapGui.Tests;

public class ProjectServiceMacroTests
{
    [Fact]
    public void PlainKeys_ExpandToMakeBreakPairs()
    {
        var macro = new MacroDef { Id = 0, Keys = new List<string> { "KC_H", "KC_I" } };
        var errors = new List<string>();
        var steps = ProjectService.MacroSteps(macro, errors);

        Assert.Empty(errors);
        Assert.Equal(new List<MacroStep>
        {
            new(0x0023, false), new(0x0023, true), // KC_H (SC1(0x23)) down, up
            new(0x0017, false), new(0x0017, true), // KC_I (SC1(0x17)) down, up
        }, steps);
    }

    [Fact]
    public void ModdedKey_ExpandsToModDownKeyDownKeyUpModUp()
    {
        // LCTL(KC_C) -> Ctrl down, C down, C up, Ctrl up (matches the driver-side --set-macro expansion).
        var macro = new MacroDef { Id = 0, Keys = new List<string> { "LCTL(KC_C)" } };
        var errors = new List<string>();
        var steps = ProjectService.MacroSteps(macro, errors);

        Assert.Empty(errors);
        Assert.Equal(4, steps.Count);
        ushort ctrlSc = KeycodeTable.ModifierScanCodes[0]; // bit 0 = LCTL
        Assert.Equal(new MacroStep(ctrlSc, false), steps[0]);
        Assert.False(steps[1].IsBreak);
        Assert.True(steps[2].IsBreak);
        Assert.Equal(steps[1].ScanCode, steps[2].ScanCode);
        Assert.Equal(new MacroStep(ctrlSc, true), steps[3]);
    }

    [Fact]
    public void MultipleModifiers_MakeInOrderBreakInReverseOrder()
    {
        // LCTL(LSFT(KC_T)): mods make LCTL then LSFT (bit order), break LSFT then LCTL (reverse).
        var macro = new MacroDef { Id = 0, Keys = new List<string> { "LCTL(LSFT(KC_T))" } };
        var errors = new List<string>();
        var steps = ProjectService.MacroSteps(macro, errors);

        Assert.Empty(errors);
        Assert.Equal(6, steps.Count);
        ushort ctrlSc = KeycodeTable.ModifierScanCodes[0];
        ushort shiftSc = KeycodeTable.ModifierScanCodes[1];
        Assert.Equal(new MacroStep(ctrlSc, false), steps[0]);
        Assert.Equal(new MacroStep(shiftSc, false), steps[1]);
        Assert.False(steps[2].IsBreak);
        Assert.True(steps[3].IsBreak);
        Assert.Equal(new MacroStep(shiftSc, true), steps[4]);
        Assert.Equal(new MacroStep(ctrlSc, true), steps[5]);
    }

    [Theory]
    [InlineData("MO(1)")]
    [InlineData("MACRO(1)")]
    [InlineData("TAPDANCE(0)")]
    [InlineData("NOT_A_KEY")]
    public void NonKeyTokens_AreRejected(string token)
    {
        var macro = new MacroDef { Id = 3, Keys = new List<string> { token } };
        var errors = new List<string>();
        var steps = ProjectService.MacroSteps(macro, errors);

        Assert.Single(errors);
        Assert.Contains("キー(KC_*)または修飾付きキー", errors[0]);
        Assert.Empty(steps);
    }

    [Fact]
    public void TooManySteps_ProducesAnError()
    {
        // KbdLayerClient.MaxMacroSteps = 64; a plain key costs 2 steps, so 33 plain keys = 66 > 64.
        var keys = Enumerable.Repeat("KC_A", 33).ToList();
        var macro = new MacroDef { Id = 0, Keys = keys };
        var errors = new List<string>();
        var steps = ProjectService.MacroSteps(macro, errors);

        Assert.Single(errors);
        Assert.Contains("長すぎます", errors[0]);
    }

    public static IEnumerable<object[]> RoundTripTokenLists()
    {
        yield return new object[] { new List<string> { "KC_H", "KC_I" } };
        yield return new object[] { new List<string> { "LCTL(KC_C)" } };
        yield return new object[] { new List<string> { "LCTL(KC_C)", "KC_A", "LSFT(KC_1)" } };
        yield return new object[] { new List<string> { "LCTL(LSFT(KC_T))" } };
    }

    [Theory]
    [MemberData(nameof(RoundTripTokenLists))]
    public void MacroTokensFromSteps_RoundTripsMacroSteps(List<string> tokens)
    {
        var macro = new MacroDef { Id = 0, Keys = tokens };
        var errors = new List<string>();
        var steps = ProjectService.MacroSteps(macro, errors);
        Assert.Empty(errors);

        var roundTripped = ProjectService.MacroTokensFromSteps(steps);

        Assert.Equal(tokens, roundTripped);
    }
}
