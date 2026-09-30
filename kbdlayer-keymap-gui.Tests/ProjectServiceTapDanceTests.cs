using KbdLayerKeymapGui.Interop;
using KbdLayerKeymapGui.Keycodes;
using KbdLayerKeymapGui.Model;

namespace KbdLayerKeymapGui.Tests;

public class ProjectServiceTapDanceTests
{
    private static TapDanceDef Valid(string singleHold = "MT(MOD_LCTL,KC_NO)") => new()
    {
        Id = 0,
        SingleTap = "KC_ESC",
        SingleHold = singleHold,
        DoubleTap = "LCTL(KC_C)",
        TripleTap = "MACRO(0)",
    };

    [Fact]
    public void AllFourSlots_AcceptTheirDocumentedTypes()
    {
        // SingleTap/DoubleTap/TripleTap: KEY, MODDED_KEY, MACRO. SingleHold: KEY, MT, LT.
        var errors = new List<string>();
        var entry = ProjectService.TapDanceToEntry(Valid(), errors);

        Assert.Empty(errors);
        Assert.NotNull(entry);
        Assert.Equal(ActionType.Key, entry!.SingleTap.Type);
        Assert.Equal(ActionType.ModTap, entry.SingleHold.Type);
        Assert.Equal(ActionType.ModdedKey, entry.DoubleTap.Type);
        Assert.Equal(ActionType.Macro, entry.TripleTap.Type);
    }

    [Theory]
    [InlineData("KC_ESC")]
    [InlineData("LT(1,KC_NO)")]
    public void SingleHold_AcceptsKeyOrLayerTapToo(string singleHold)
    {
        var errors = new List<string>();
        var entry = ProjectService.TapDanceToEntry(Valid(singleHold), errors);

        Assert.Empty(errors);
        Assert.NotNull(entry);
    }

    [Fact]
    public void SingleHold_KC_NO_IsRejected()
    {
        // Regression test for a real mistake made while OIB-testing feature/tap-hold-reresolve-
        // quicktap on 2026-09-30: KC_NO is valid as MT/LT's own *tap* argument (MT(MOD_LCTL,KC_NO))
        // but NOT as a bare SingleHold value by itself. The CLI (KeymapTool --set-tapdance) rejects
        // it with "SingleHold "KC_NO" must be a plain key, MT(mod,kc), or LT(layer,kc)", and this
        // exact mistake was made in a PowerShell test script whose output-filtering helper silently
        // swallowed that error (it doesn't contain "fail"/"error"), so the tap-dance was silently
        // never configured and a later test ran against an unconfigured tap-dance for a while
        // before the bug was found. This unit test would have caught it immediately.
        var td = Valid(singleHold: "KC_NO");
        var errors = new List<string>();
        var entry = ProjectService.TapDanceToEntry(td, errors);

        Assert.Null(entry);
        Assert.Single(errors);
        Assert.Contains("ホールド", errors[0]);
    }

    [Theory]
    [InlineData("MO(1)")]      // not a plain key, modded key, or MACRO
    [InlineData("TAPDANCE(1)")] // tap-dance can't nest inside its own tap side
    [InlineData("NOT_A_KEY")]
    public void TapSlots_RejectUnsupportedTypes(string badToken)
    {
        var td = Valid();
        td.SingleTap = badToken;
        var errors = new List<string>();
        var entry = ProjectService.TapDanceToEntry(td, errors);

        Assert.Null(entry);
        Assert.Contains(errors, e => e.Contains("1回タップ"));
    }

    [Fact]
    public void AllFourInvalid_ReportsAllFourErrors()
    {
        var td = new TapDanceDef { Id = 2, SingleTap = "MO(1)", SingleHold = "KC_NO", DoubleTap = "MO(1)", TripleTap = "MO(1)" };
        var errors = new List<string>();
        var entry = ProjectService.TapDanceToEntry(td, errors);

        Assert.Null(entry);
        Assert.Equal(4, errors.Count); // SingleTap, DoubleTap, TripleTap, SingleHold -- one each
    }
}
