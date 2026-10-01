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
    [InlineData("LCTL(KC_C)")]       // since feature/combo-td-extend (2026-10-02)
    [InlineData("LM(1,MOD_LCTL)")]
    [InlineData("MACRO(0)")]
    public void SingleHold_AcceptsKeyOrLayerTapToo(string singleHold)
    {
        var errors = new List<string>();
        var entry = ProjectService.TapDanceToEntry(Valid(singleHold), errors);

        Assert.Empty(errors);
        Assert.NotNull(entry);
    }

    [Theory]
    [InlineData("KC_NO")]
    [InlineData("")]
    public void SingleHold_KC_NO_OrBlank_MeansNoAction(string singleHold)
    {
        // History: until 2026-09-30 (feature/layer-to-tt) a bare KC_NO in SingleHold was rejected, and a
        // PowerShell test script whose output-filtering helper swallowed that error ran against an
        // unconfigured tap-dance for a while (dev-notes H-5). The spec then changed: KC_NO / blank in
        // any slot means "do nothing" for that tap count / the hold. The error-reporting half of that
        // lesson is kept by SingleHold_InvalidToken_IsRejected below.
        var td = Valid(singleHold: singleHold);
        var errors = new List<string>();
        var entry = ProjectService.TapDanceToEntry(td, errors);

        Assert.Empty(errors);
        Assert.NotNull(entry);
        Assert.Equal(ActionType.None, entry!.SingleHold.Type);
    }

    [Theory]
    [InlineData("KC_NO")]
    [InlineData("")]
    public void TapSlot_KC_NO_OrBlank_IsSentAsKeyZero(string token)
    {
        // The driver treats SingleTap.Type == NONE as "clear this tap-dance id", so "no action" on the
        // tap side must be encoded as KEY with scan code 0 (the driver outputs nothing for it).
        var td = Valid();
        td.SingleTap = token;
        td.DoubleTap = token;
        var errors = new List<string>();
        var entry = ProjectService.TapDanceToEntry(td, errors);

        Assert.Empty(errors);
        Assert.NotNull(entry);
        Assert.Equal(ActionType.Key, entry!.SingleTap.Type);
        Assert.Equal(0, entry.SingleTap.Param2);
        Assert.Equal(ActionType.Key, entry.DoubleTap.Type);
        Assert.Equal(0, entry.DoubleTap.Param2);
    }

    [Theory]
    [InlineData("MO(1)")]
    [InlineData("NOT_A_KEY")]
    public void SingleHold_InvalidToken_IsRejected(string singleHold)
    {
        // A mistyped hold must surface as an error (not silently become "no action"); see the history
        // note in SingleHold_KC_NO_OrBlank_MeansNoAction.
        var td = Valid(singleHold: singleHold);
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
        var td = new TapDanceDef { Id = 2, SingleTap = "MO(1)", SingleHold = "MO(1)", DoubleTap = "MO(1)", TripleTap = "MO(1)" };
        var errors = new List<string>();
        var entry = ProjectService.TapDanceToEntry(td, errors);

        Assert.Null(entry);
        Assert.Equal(4, errors.Count); // SingleTap, DoubleTap, TripleTap, SingleHold -- one each
    }
}
